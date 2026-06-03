using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WaveEngine.Application.DTOs.Audio;
using WaveEngine.Application.DTOs.Pipeline;
using WaveEngine.Application.Interfaces;
using WaveEngine.Domain.Entities;

namespace WaveEngine.Infrastructure.Services;

/// <summary>
/// Shifts a narration segment to a new start time without re-synthesizing audio.
/// Steps:
///   1. Load job + deserialise NarrationScript.
///   2. Patch segment StartTimeSeconds / EndTimeSeconds.
///   3. Re-assemble narration.wav from existing workspace WAVs at updated timings.
///   4. Re-compile output.mp4 atomically.
///   5. Persist updated NarrationScript back to job.ResultData.
/// </summary>
public class SegmentMoveService : ISegmentMoveService
{
    private readonly IJobRepository _jobRepo;
    private readonly IAudioAssemblyService _assembly;
    private readonly IVideoCompilationService _video;
    private readonly ILogger<SegmentMoveService> _log;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    public SegmentMoveService(
        IJobRepository jobRepo,
        IAudioAssemblyService assembly,
        IVideoCompilationService video,
        ILogger<SegmentMoveService> log)
    {
        _jobRepo  = jobRepo;
        _assembly = assembly;
        _video    = video;
        _log      = log;
    }

    public async Task<MoveSegmentResponse> MoveAsync(MoveSegmentRequest request, CancellationToken ct = default)
    {
        // ── 1. Load job ───────────────────────────────────────────────────────
        var job = await _jobRepo.GetAsync(request.JobId, ct)
            ?? throw new KeyNotFoundException($"Job {request.JobId} not found.");

        if (string.IsNullOrEmpty(job.WorkspaceDirectory) || !Directory.Exists(job.WorkspaceDirectory))
            throw new InvalidOperationException($"Workspace for job {request.JobId} does not exist.");

        if (string.IsNullOrEmpty(job.ResultData))
            throw new InvalidOperationException($"Job {request.JobId} has no result data.");

        var configPath = Path.Combine(job.WorkspaceDirectory, "pipeline_config.json");
        if (!File.Exists(configPath))
            throw new InvalidOperationException("pipeline_config.json not found — job pre-dates move support.");

        var pipelineRequest = JsonSerializer.Deserialize<ExecutePipelineRequest>(
            await File.ReadAllTextAsync(configPath, ct), _json)
            ?? throw new InvalidOperationException("Failed to deserialise pipeline_config.json.");

        // ── 2. Deserialise NarrationScript + patch timing ─────────────────────
        var script = JsonSerializer.Deserialize<NarrationScript>(job.ResultData, _json)
            ?? throw new InvalidOperationException("Failed to deserialise NarrationScript.");

        var seg = script.Segments.FirstOrDefault(s => s.Id == request.SegmentId)
            ?? throw new KeyNotFoundException($"Segment '{request.SegmentId}' not found in job {request.JobId}.");

        float delta = request.NewStartTimeSeconds - seg.StartTimeSeconds;
        seg.StartTimeSeconds = request.NewStartTimeSeconds;
        seg.EndTimeSeconds  += delta;

        _log.LogInformation(
            "MoveSegment: job={Job} seg={Seg} Δ={Delta:+0.##;-0.##}s → [{Start:F2},{End:F2}]",
            request.JobId, request.SegmentId, delta, seg.StartTimeSeconds, seg.EndTimeSeconds);

        // ── 3. Re-assemble narration.wav from existing workspace WAVs ─────────
        var assemblyInputs = new List<SegmentAudioAssemblyInput>(script.Segments.Count);
        foreach (var s in script.Segments)
        {
            var wavPath = Path.Combine(job.WorkspaceDirectory, $"{s.Id}.wav");
            if (!File.Exists(wavPath))
                throw new FileNotFoundException($"WAV for segment '{s.Id}' not found in workspace.", wavPath);

            assemblyInputs.Add(new SegmentAudioAssemblyInput
            {
                SegmentId        = s.Id,
                StartTimeSeconds = s.StartTimeSeconds,
                WavBytes         = await File.ReadAllBytesAsync(wavPath, ct),
            });
        }

        double totalDuration = script.Segments.Max(s => (double)s.EndTimeSeconds);
        var mixWav = await _assembly.AssembleAsync(
            assemblyInputs, pipelineRequest.BackgroundMusic, totalDuration, ct);

        var narrationPath = Path.Combine(job.WorkspaceDirectory, "narration.wav");
        await File.WriteAllBytesAsync(narrationPath, mixWav, ct);

        // ── 4. Re-compile video ───────────────────────────────────────────────
        var inputVideo = Directory.GetFiles(job.WorkspaceDirectory, "input.*").FirstOrDefault()
            ?? throw new FileNotFoundException("Input video not found in workspace.", job.WorkspaceDirectory);

        var outputPath = Path.Combine(job.WorkspaceDirectory, "output.mp4");
        var tempPath   = Path.Combine(job.WorkspaceDirectory, "output_tmp.mp4");

        await _video.CompileAsync(
            inputVideo, narrationPath, totalDuration, tempPath,
            pipelineRequest.DuckOriginalAudio, ct);

        File.Move(tempPath, outputPath, overwrite: true);
        _log.LogInformation("MoveSegment: video recompiled → {Path}.", outputPath);

        // ── 5. Persist updated script ─────────────────────────────────────────
        job.ResultData = JsonSerializer.Serialize(script, _json);
        await _jobRepo.UpdateAsync(job, ct);

        var cacheBuster = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new MoveSegmentResponse
        {
            SegmentId        = seg.Id,
            StartTimeSeconds = seg.StartTimeSeconds,
            EndTimeSeconds   = seg.EndTimeSeconds,
            VideoUrl         = $"/api/pipeline/video/{request.JobId}?v={cacheBuster}",
        };
    }
}
