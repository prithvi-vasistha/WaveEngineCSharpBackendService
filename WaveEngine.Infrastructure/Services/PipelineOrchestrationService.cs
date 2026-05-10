using Microsoft.Extensions.Logging;
using WaveEngine.Application.DTOs.Audio;
using WaveEngine.Application.DTOs.Pipeline;
using WaveEngine.Application.Interfaces;

namespace WaveEngine.Infrastructure.Services;

/// <summary>
/// Coordinates the full end-to-end pipeline in four sequential phases:
///   Phase 1 — Script generation     (LLM via Python script service)
///   Phase 2 — TTS synthesis loop    (synthesis + normalization + LLM-rewrite retries)
///   Phase 3 — Audio assembly        (FFmpeg — segment mix + optional background music)
///   Phase 4 — Video compilation     (FFmpeg — mux video with assembled narration)
///
/// The compiled MP4 is written to a temporary workspace directory.
/// The caller (PipelineController) is responsible for streaming the file and
/// deleting <see cref="PipelineVideoOutput.WorkspaceDirectory"/> after streaming completes.
/// </summary>
public class PipelineOrchestrationService : IPipelineOrchestrationService
{
    private readonly IScriptGenerationService _scriptService;
    private readonly ITtsOrchestrationService _ttsOrchestrationService;
    private readonly IVideoCompilationService _videoCompilationService;
    private readonly ILogger<PipelineOrchestrationService> _log;

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
            var script = await _scriptService.GetMasterPlanAsync(request.ScriptRequest, ct);

            _log.LogInformation(
                "Pipeline [1/4]: script ready — projectId={Id} segments={Count}",
                script.ProjectId, script.Segments?.Count ?? 0);

            // ── Phase 2 + 3: TTS synthesis loop + audio assembly ─────────────
            _log.LogInformation("Pipeline [2-3/4]: synthesizing segments and assembling audio…");

            var orchestrateRequest = new OrchestrateRequest
            {
                Script          = script,
                AiConfig        = request.ScriptRequest.AiConfig,
                BackgroundMusic = request.BackgroundMusic,
                VoiceOverride   = request.VoiceOverride,
                // Let the orchestrator use segment-based total duration;
                // the actual video duration is unknown until we probe it.
                VideoDurationSeconds = null,
            };

            var orchestrationResult = await _ttsOrchestrationService.OrchestrateAsync(
                orchestrateRequest, ct);

            _log.LogInformation(
                "Pipeline [2-3/4]: audio ready — {Count} segments, final mix assembled.",
                orchestrationResult.SegmentAudio?.Count ?? 0);

            // ── Phase 4: Video compilation ─────────────────────────────────────
            _log.LogInformation("Pipeline [4/4]: compiling video with narration track…");

            // Save uploaded video to workspace
            var videoExt = Path.GetExtension(videoFileName).ToLowerInvariant();
            if (string.IsNullOrEmpty(videoExt)) videoExt = ".mp4";
            var videoPath = Path.Combine(workspaceDir, $"input{videoExt}");

            await using (var fs = File.Create(videoPath))
                await videoStream.CopyToAsync(fs, ct);

            // Decode narration WAV
            var narrationBytes = Convert.FromBase64String(orchestrationResult.FinalMixWavBase64);
            var narrationWavPath = Path.Combine(workspaceDir, "narration.wav");
            await File.WriteAllBytesAsync(narrationWavPath, narrationBytes, ct);

            // Calculate narration duration for FFmpeg
            double totalDuration = script.Segments is { Count: > 0 }
                ? (double)script.Segments.Max(s => s.EndTimeSeconds)
                : 0;

            var outputPath = Path.Combine(workspaceDir, "output.mp4");

            await _videoCompilationService.CompileAsync(
                videoPath, narrationWavPath, totalDuration, outputPath,
                request.DuckOriginalAudio, ct);

            _log.LogInformation(
                "Pipeline [4/4]: compilation complete — output={Path}", outputPath);

            return new PipelineVideoOutput
            {
                VideoPath        = outputPath,
                WorkspaceDirectory = workspaceDir,
                ProjectId        = script.ProjectId,
            };
        }
        catch (Exception ex)
        {
            // Clean up workspace on failure — the caller never gets a chance to
            _log.LogError(ex, "Pipeline: failed. Cleaning up workspace {Dir}.", workspaceDir);
            try { Directory.Delete(workspaceDir, recursive: true); } catch { /* best-effort */ }
            throw;
        }
    }
}
