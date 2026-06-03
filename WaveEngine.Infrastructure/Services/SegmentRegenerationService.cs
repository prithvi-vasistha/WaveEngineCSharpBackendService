using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WaveEngine.Application.DTOs.Audio;
using WaveEngine.Application.DTOs.Pipeline;
using WaveEngine.Application.DTOs.Script;
using WaveEngine.Application.Interfaces;
using WaveEngine.Domain.Entities;

namespace WaveEngine.Infrastructure.Services;

/// <summary>
/// Handles post-generation segment editing:
///   1. Optional AI refinement of the script (LLM call to /refine-segment).
///   2. Re-synthesis of the single changed segment via TtsOrchestrationService.
///   3. Save the new WAV to {workspace}/{segmentId}.wav (overwriting the original).
///   4. Re-assemble the full narration mix from all workspace WAVs.
///   5. Re-compile the final video in place (overwrites output.mp4 atomically).
///   6. Update job.ResultData with the new NarrationScript JSON.
/// </summary>
public class SegmentRegenerationService : ISegmentRegenerationService
{
    private readonly IJobRepository _jobRepo;
    private readonly IScriptGenerationService _scriptService;
    private readonly ITtsOrchestrationService _ttsOrchestration;
    private readonly IAudioAssemblyService _assemblyService;
    private readonly IVideoCompilationService _videoCompilation;
    private readonly ILogger<SegmentRegenerationService> _log;

    private static readonly JsonSerializerOptions _snakeCaseOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    public SegmentRegenerationService(
        IJobRepository jobRepo,
        IScriptGenerationService scriptService,
        ITtsOrchestrationService ttsOrchestration,
        IAudioAssemblyService assemblyService,
        IVideoCompilationService videoCompilation,
        ILogger<SegmentRegenerationService> log)
    {
        _jobRepo         = jobRepo;
        _scriptService   = scriptService;
        _ttsOrchestration = ttsOrchestration;
        _assemblyService  = assemblyService;
        _videoCompilation = videoCompilation;
        _log              = log;
    }

    public async Task<RegenerateSegmentResponse> RegenerateAsync(
        RegenerateSegmentRequest request,
        CancellationToken ct = default)
    {
        // ── 1. Load job ───────────────────────────────────────────────────────
        var job = await _jobRepo.GetAsync(request.JobId, ct)
            ?? throw new KeyNotFoundException($"Job {request.JobId} not found.");

        if (string.IsNullOrEmpty(job.WorkspaceDirectory) || !Directory.Exists(job.WorkspaceDirectory))
            throw new InvalidOperationException(
                $"Workspace directory for job {request.JobId} does not exist. " +
                "The original pipeline may not have completed successfully.");

        if (string.IsNullOrEmpty(job.ResultData))
            throw new InvalidOperationException($"Job {request.JobId} has no result data.");

        // ── 2. Deserialize current NarrationScript ────────────────────────────
        var script = JsonSerializer.Deserialize<NarrationScript>(job.ResultData, _snakeCaseOptions)
            ?? throw new InvalidOperationException("Failed to deserialize NarrationScript from job result.");

        var segment = script.Segments.FirstOrDefault(s => s.Id == request.SegmentId)
            ?? throw new KeyNotFoundException(
                $"Segment '{request.SegmentId}' not found in job {request.JobId}.");

        // ── 3. Load pipeline config (bg music, voice override, duck setting) ──
        var configPath = Path.Combine(job.WorkspaceDirectory, "pipeline_config.json");
        if (!File.Exists(configPath))
            throw new InvalidOperationException(
                "pipeline_config.json not found in workspace. " +
                "This job was created before segment regeneration was supported.");

        var pipelineRequest = JsonSerializer.Deserialize<ExecutePipelineRequest>(
            await File.ReadAllTextAsync(configPath, ct), _snakeCaseOptions)
            ?? throw new InvalidOperationException("Failed to deserialize pipeline_config.json.");

        _log.LogInformation(
            "RegenerateSegment: job={JobId} segment={SegId} hasAiInstruction={HasAi}",
            request.JobId, request.SegmentId, !string.IsNullOrWhiteSpace(request.AiInstruction));

        // ── 4. AI refinement (optional) ───────────────────────────────────────
        string finalScript = request.NewScript;

        if (!string.IsNullOrWhiteSpace(request.AiInstruction))
        {
            _log.LogInformation(
                "RegenerateSegment: calling AI refine — instruction='{Instruction}'",
                request.AiInstruction.Length > 80
                    ? request.AiInstruction[..80] + "…"
                    : request.AiInstruction);

            var refined = await _scriptService.RefineAsync(new RefineScriptRequest
            {
                OriginalScript        = request.NewScript,
                UserInstruction       = request.AiInstruction,
                TargetDurationSeconds = segment.TargetDuration,
                AiConfig              = pipelineRequest.ScriptRequest.AiConfig,
                Keywords              = [],
            }, ct);

            finalScript = refined.RewrittenScript;
            _log.LogInformation(
                "RegenerateSegment: AI refined — {Old} → {New} words.",
                request.NewScript.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
                refined.WordCount);
        }

        // ── 5. Update segment in script ───────────────────────────────────────
        float newStart = request.NewStartTimeSeconds ?? segment.StartTimeSeconds;
        // Keep target_duration fixed; shift end_time by the same delta as start_time.
        float timeDelta    = newStart - segment.StartTimeSeconds;
        float newEnd       = segment.EndTimeSeconds + timeDelta;

        segment.Script           = finalScript;
        segment.StartTimeSeconds = newStart;
        segment.EndTimeSeconds   = newEnd;

        // ── 6. Re-synthesize the single changed segment ───────────────────────
        _log.LogInformation(
            "RegenerateSegment: synthesizing segment '{SegId}' (target={Target:F1}s)…",
            segment.Id, segment.TargetDuration);

        var singleSegScript = new NarrationScript
        {
            ProjectId            = script.ProjectId,
            GeneratedAt          = script.GeneratedAt,
            Segments             = [segment],
            AssemblyInstructions = script.AssemblyInstructions,
        };

        var orchestrateReq = new OrchestrateRequest
        {
            Script               = singleSegScript,
            AiConfig             = pipelineRequest.ScriptRequest.AiConfig,
            BackgroundMusic      = null,   // no bg music for single-segment synthesis
            VoiceOverride        = pipelineRequest.VoiceOverride,
            VideoDurationSeconds = null,
        };

        var synthResult = await _ttsOrchestration.OrchestrateAsync(
            orchestrateReq, segmentProgress: null, ct);

        // ── 7. Overwrite segment WAV in workspace ─────────────────────────────
        var newWavBytes = Convert.FromBase64String(synthResult.SegmentAudio[0].WavBase64);
        var segWavPath  = Path.Combine(job.WorkspaceDirectory, $"{segment.Id}.wav");
        await File.WriteAllBytesAsync(segWavPath, newWavBytes, ct);
        _log.LogInformation(
            "RegenerateSegment: saved new WAV → {Path} ({Bytes} bytes).", segWavPath, newWavBytes.Length);

        // ── 8. Re-assemble full mix from all workspace WAVs ───────────────────
        var assemblyInputs = new List<SegmentAudioAssemblyInput>(script.Segments.Count);
        foreach (var seg in script.Segments)
        {
            var wavPath  = Path.Combine(job.WorkspaceDirectory, $"{seg.Id}.wav");
            var wavBytes = await File.ReadAllBytesAsync(wavPath, ct);
            assemblyInputs.Add(new SegmentAudioAssemblyInput
            {
                SegmentId        = seg.Id,
                StartTimeSeconds = seg.StartTimeSeconds,
                WavBytes         = wavBytes,
            });
        }

        double totalDuration = script.Segments.Max(s => (double)s.EndTimeSeconds);
        var mixWav = await _assemblyService.AssembleAsync(
            assemblyInputs, pipelineRequest.BackgroundMusic, totalDuration, ct);

        // ── 9. Write narration.wav and re-compile video ───────────────────────
        var narrationPath = Path.Combine(job.WorkspaceDirectory, "narration.wav");
        await File.WriteAllBytesAsync(narrationPath, mixWav, ct);

        // Find the original input video (input.mp4, input.mov, etc.)
        var inputVideoPath = Directory
            .GetFiles(job.WorkspaceDirectory, "input.*")
            .FirstOrDefault()
            ?? throw new FileNotFoundException(
                "Original input video not found in workspace.", job.WorkspaceDirectory);

        var outputVideoPath = Path.Combine(job.WorkspaceDirectory, "output.mp4");
        var tempOutputPath  = Path.Combine(job.WorkspaceDirectory, "output_tmp.mp4");

        _log.LogInformation(
            "RegenerateSegment: compiling video — input={Input} narration={Narration}",
            inputVideoPath, narrationPath);

        await _videoCompilation.CompileAsync(
            inputVideoPath, narrationPath, totalDuration, tempOutputPath,
            pipelineRequest.DuckOriginalAudio, ct);

        // Atomic replace
        File.Move(tempOutputPath, outputVideoPath, overwrite: true);
        _log.LogInformation("RegenerateSegment: video compiled → {Path}.", outputVideoPath);

        // ── 10. Update job ResultData ─────────────────────────────────────────
        job.ResultData = JsonSerializer.Serialize(script, _snakeCaseOptions);
        await _jobRepo.UpdateAsync(job, ct);

        // ── 11. Return cache-busted video URL ─────────────────────────────────
        var cacheBuster = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new RegenerateSegmentResponse
        {
            SegmentId         = segment.Id,
            FinalScript       = finalScript,
            StartTimeSeconds  = segment.StartTimeSeconds,
            EndTimeSeconds    = segment.EndTimeSeconds,
            VideoUrl          = $"/api/pipeline/video/{request.JobId}?v={cacheBuster}",
        };
    }
}
