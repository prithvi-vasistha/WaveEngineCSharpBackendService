using System.Text.Json;
using Microsoft.Extensions.Logging;
using WaveEngine.Application.DTOs.Audio;
using WaveEngine.Application.DTOs.Jobs;
using WaveEngine.Application.DTOs.Pipeline;
using WaveEngine.Application.Interfaces;
using WaveEngine.Domain.Entities;

namespace WaveEngine.Infrastructure.Services;

/// <summary>
/// Coordinates the full end-to-end pipeline in four sequential phases:
///   Phase 1 — Script generation     (LLM via Python script service)
///   Phase 2 — TTS synthesis loop    (synthesis + normalization + LLM-rewrite retries)
///   Phase 3 — Audio assembly        (FFmpeg — segment mix + optional background music)
///   Phase 4 — Video compilation     (FFmpeg — mux video with assembled narration)
///
/// Progress is reported via the optional IProgress callback at each milestone:
///   0 %  → job created (set by the controller before calling this method)
///  15 %  → script ready           (Phase 1 complete)
///  15–85% → per-segment synthesis  (Phase 2, proportional)
///  87 %  → audio assembled        (Phase 3 complete)
///  100 % → video compiled         (Phase 4 complete)
/// </summary>
public class PipelineOrchestrationService : IPipelineOrchestrationService
{
    private readonly IScriptGenerationService _scriptService;
    private readonly ITtsOrchestrationService _ttsOrchestrationService;
    private readonly IVideoCompilationService _videoCompilationService;
    private readonly ILogger<PipelineOrchestrationService> _log;

    private static readonly JsonSerializerOptions _snakeCaseOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public PipelineOrchestrationService(
        IScriptGenerationService scriptService,
        ITtsOrchestrationService ttsOrchestrationService,
        IVideoCompilationService videoCompilationService,
        ILogger<PipelineOrchestrationService> log)
    {
        _scriptService           = scriptService;
        _ttsOrchestrationService = ttsOrchestrationService;
        _videoCompilationService = videoCompilationService;
        _log                     = log;
    }

    public async Task<PipelineVideoOutput> ExecuteAsync(
        Stream videoStream,
        string videoFileName,
        ExecutePipelineRequest request,
        IProgress<JobProgressUpdate>? progress = null,
        CancellationToken ct = default)
    {
        var workspaceDir = Path.Combine(
            Path.GetTempPath(), "waveengine", "pipeline", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceDir);

        _log.LogInformation(
            "Pipeline: starting — project='{Name}' workspace={Dir}",
            request.ScriptRequest.ProjectName, workspaceDir);

        try
        {
            // ── Phase 1: Script generation ────────────────────────────────────
            _log.LogInformation("Pipeline [1/4]: generating narration script…");

            progress?.Report(new JobProgressUpdate(5, "AI is writing your narration script…", JobStatus.Scripting));

            var script = await _scriptService.GetMasterPlanAsync(request.ScriptRequest, ct);

            _log.LogInformation(
                "Pipeline [1/4]: script ready — projectId={Id} segments={Count}",
                script.ProjectId, script.Segments?.Count ?? 0);

            int totalSegments = script.Segments?.Count ?? 1;
            progress?.Report(new JobProgressUpdate(
                15,
                $"Script ready — synthesizing {totalSegments} voice segment{(totalSegments == 1 ? "" : "s")}…",
                JobStatus.Synthesizing));

            // ── Phase 2 + 3: TTS synthesis loop + audio assembly ─────────────
            _log.LogInformation("Pipeline [2-3/4]: synthesizing segments and assembling audio…");

            // Map per-segment completions to the 15–85 % range
            var segmentProgress = new Progress<(int Completed, int Total)>(tuple =>
            {
                int pct = 15 + (int)(70.0 * tuple.Completed / Math.Max(tuple.Total, 1));
                progress?.Report(new JobProgressUpdate(
                    pct,
                    $"Synthesizing segment {tuple.Completed} of {tuple.Total}…",
                    JobStatus.Synthesizing));
            });

            var orchestrateRequest = new OrchestrateRequest
            {
                Script          = script,
                AiConfig        = request.ScriptRequest.AiConfig,
                BackgroundMusic = request.BackgroundMusic,
                VoiceOverride   = request.VoiceOverride,
                VideoDurationSeconds = null,
            };

            var orchestrationResult = await _ttsOrchestrationService.OrchestrateAsync(
                orchestrateRequest, segmentProgress, ct);

            _log.LogInformation(
                "Pipeline [2-3/4]: audio ready — {Count} segments assembled.",
                orchestrationResult.SegmentAudio?.Count ?? 0);

            progress?.Report(new JobProgressUpdate(87, "Audio assembled — compiling video…", JobStatus.Stitching));

            // ── Phase 4: Video compilation ─────────────────────────────────────
            _log.LogInformation("Pipeline [4/4]: compiling video with narration track…");

            var videoExt = Path.GetExtension(videoFileName).ToLowerInvariant();
            if (string.IsNullOrEmpty(videoExt)) videoExt = ".mp4";
            var videoPath = Path.Combine(workspaceDir, $"input{videoExt}");

            await using (var fs = File.Create(videoPath))
                await videoStream.CopyToAsync(fs, ct);

            var narrationBytes = Convert.FromBase64String(orchestrationResult.FinalMixWavBase64);
            var narrationWavPath = Path.Combine(workspaceDir, "narration.wav");
            await File.WriteAllBytesAsync(narrationWavPath, narrationBytes, ct);

            // Persist individual segment WAVs so SegmentRegenerationService can do partial re-renders.
            foreach (var segResult in orchestrationResult.SegmentAudio)
            {
                var segWavBytes = Convert.FromBase64String(segResult.WavBase64);
                await File.WriteAllBytesAsync(
                    Path.Combine(workspaceDir, $"{segResult.SegmentId}.wav"), segWavBytes, ct);
            }

            // Persist the pipeline config so regeneration can re-use bg music, voice override, etc.
            var configJson = JsonSerializer.Serialize(request, _snakeCaseOptions);
            await File.WriteAllTextAsync(
                Path.Combine(workspaceDir, "pipeline_config.json"), configJson, ct);

            double totalDuration = script.Segments is { Count: > 0 }
                ? (double)script.Segments.Max(s => s.EndTimeSeconds)
                : 0;

            var outputPath = Path.Combine(workspaceDir, "output.mp4");

            await _videoCompilationService.CompileAsync(
                videoPath, narrationWavPath, totalDuration, outputPath,
                request.DuckOriginalAudio, ct);

            _log.LogInformation("Pipeline [4/4]: compilation complete — output={Path}", outputPath);

            return new PipelineVideoOutput
            {
                VideoPath          = outputPath,
                WorkspaceDirectory = workspaceDir,
                ProjectId          = script.ProjectId,
                Script             = script,
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Pipeline: failed. Cleaning up workspace {Dir}.", workspaceDir);
            try { Directory.Delete(workspaceDir, recursive: true); } catch { /* best-effort */ }
            throw;
        }
    }
}
