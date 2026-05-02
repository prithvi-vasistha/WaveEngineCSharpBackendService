using Microsoft.Extensions.Logging;
using WaveEngine.Application.DTOs.Audio;
using WaveEngine.Application.DTOs.Script;
using WaveEngine.Application.DTOs.Synthesize;
using WaveEngine.Application.Interfaces;
using WaveEngine.Domain.Entities;
using WaveEngine.Domain.Exceptions;

namespace WaveEngine.Infrastructure.Services;

public class TtsOrchestrationService : ITtsOrchestrationService
{
    private const int MaxRewriteRetries = 2;

    private readonly IScriptGenerationService _scriptService;
    private readonly ITtsSynthesisService _synthesisService;
    private readonly IAudioNormalizationService _normalizationService;
    private readonly IAudioAssemblyService _assemblyService;
    private readonly ILogger<TtsOrchestrationService> _log;

    public TtsOrchestrationService(
        IScriptGenerationService scriptService,
        ITtsSynthesisService synthesisService,
        IAudioNormalizationService normalizationService,
        IAudioAssemblyService assemblyService,
        ILogger<TtsOrchestrationService> log)
    {
        _scriptService = scriptService;
        _synthesisService = synthesisService;
        _normalizationService = normalizationService;
        _assemblyService = assemblyService;
        _log = log;
    }

    public async Task<OrchestrationResult> OrchestrateAsync(
        OrchestrateRequest request,
        CancellationToken ct = default)
    {
        var script = request.Script;

        if (script.Segments is not { Count: > 0 })
            throw new ArgumentException(
                "request.script.segments is empty or missing. " +
                "Ensure the NarrationScript is nested under the \"script\" key in the request body.");

        _log.LogInformation(
            "Orchestration: synthesizing {Count} segments for project '{Id}'",
            script.Segments.Count, script.ProjectId);

        // Phase 1 — synthesize + normalize each segment, keeping raw WAV bytes for assembly
        var segmentResults  = new List<SegmentAudioResult>(script.Segments.Count);
        var assemblyInputs  = new List<SegmentAudioAssemblyInput>(script.Segments.Count);

        foreach (var segment in script.Segments)
        {
            var (result, rawWav) = await ProcessSegmentAsync(segment, request.AiConfig, ct);
            segmentResults.Add(result);
            assemblyInputs.Add(new SegmentAudioAssemblyInput
            {
                SegmentId        = segment.Id,
                StartTimeSeconds = segment.StartTimeSeconds,
                WavBytes         = rawWav,
            });
        }

        // Phase 2 — assemble all segments into a single mixed audio track
        //
        // Background music should loop for the full video length, not just until the last
        // narration segment ends — so prefer video_duration_seconds when the caller provides it.
        double segmentDuration = script.Segments.Max(s => (double)s.EndTimeSeconds);
        double totalDuration   = request.VideoDurationSeconds is > 0
            ? request.VideoDurationSeconds.Value
            : segmentDuration;

        _log.LogInformation(
            "Orchestration: assembling final mix — totalDuration={Duration:F2}s " +
            "(segmentMax={SegMax:F2}s, videoDuration={Vid}) track='{Track}'",
            totalDuration, segmentDuration,
            request.VideoDurationSeconds?.ToString("F2") ?? "not provided",
            request.BackgroundMusic?.TrackFileName ?? "none");

        var finalMixWav = await _assemblyService.AssembleAsync(
            assemblyInputs, request.BackgroundMusic, totalDuration, ct);

        return new OrchestrationResult
        {
            Script          = script,
            SegmentAudio    = segmentResults,
            FinalMixWavBase64 = Convert.ToBase64String(finalMixWav),
        };
    }

    /// <summary>
    /// Synthesizes, normalizes (with rewrite retry), and returns the DTO + raw WAV bytes.
    /// Raw bytes are kept separate to avoid a Base64 encode→decode round-trip for assembly.
    /// </summary>
    private async Task<(SegmentAudioResult Dto, byte[] RawWav)> ProcessSegmentAsync(
        Segment segment,
        AiConfigDto aiConfig,
        CancellationToken ct)
    {
        _log.LogInformation(
            "Segment {Id}: synthesizing (target={Duration:F1}s)",
            segment.Id, segment.TargetDuration);

        // Initial synthesize
        var currentScript = segment.Script;
        var rawWav = await SynthesizeAsync(segment, currentScript, ct);

        // Attempt normalization — retry with rewrite if oversized
        for (int attempt = 0; attempt <= MaxRewriteRetries; attempt++)
        {
            try
            {
                var normResult = await _normalizationService.NormalizeAsync(
                    rawWav, segment.TargetDuration, segment.Id, ct);

                if (attempt > 0)
                    _log.LogInformation(
                        "Segment {Id}: normalized successfully after {N} rewrite(s).", segment.Id, attempt);

                return (ToSegmentResult(segment, normResult), normResult.WavBytes);
            }
            catch (SegmentOverSizedException ex)
            {
                if (attempt == MaxRewriteRetries)
                {
                    // All retries exhausted — apply hard-cap and move on
                    _log.LogWarning(
                        "⚠️  Segment {Id}: HARD-CAP FALLBACK after {N} rewrite attempts. " +
                        "actual={Actual:F2}s, target={Target:F2}s, factor={Factor:F2}x. " +
                        "Audio will bleed. Review script word count for this segment.",
                        ex.SegmentId, MaxRewriteRetries,
                        ex.ActualDuration, ex.TargetDuration, ex.SpeedFactor);

                    var hardCapResult = await _normalizationService.ApplyHardCapAsync(
                        rawWav, ex.ActualDuration, ex.TargetDuration, segment.Id, ct);

                    return (ToSegmentResult(segment, hardCapResult), hardCapResult.WavBytes);
                }

                _log.LogWarning(
                    "Segment {Id}: oversized (factor={Factor:F2}x). " +
                    "Rewrite attempt {Attempt}/{Max} …",
                    ex.SegmentId, ex.SpeedFactor, attempt + 1, MaxRewriteRetries);

                // Rewrite the script shorter and re-synthesize
                (currentScript, rawWav) = await RewriteAndSynthesizeAsync(
                    segment, currentScript, aiConfig, ct);
            }
        }

        // Unreachable — loop always returns or throws
        throw new InvalidOperationException($"Segment {segment.Id}: unexpected loop exit.");
    }

    /// <summary>Calls /rewrite-shorter, then re-synthesizes. Returns (newScript, rawWav).</summary>
    private async Task<(string newScript, byte[] rawWav)> RewriteAndSynthesizeAsync(
        Segment segment,
        string currentScript,
        AiConfigDto aiConfig,
        CancellationToken ct)
    {
        var rewriteRequest = new RewriteShorterRequest
        {
            OriginalScript = currentScript,
            TargetDurationSeconds = segment.TargetDuration,
            AiConfig = aiConfig,
            Keywords = [],  // keywords were already woven in; preserve meaning, not exact terms
        };

        var rewritten = await _scriptService.RewriteShorterAsync(rewriteRequest, ct);

        _log.LogInformation(
            "Segment {Id}: rewritten from {Old} → {New} words (limit {Max}).",
            segment.Id,
            currentScript.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
            rewritten.WordCount,
            rewritten.MaxWordCount);

        var newWav = await SynthesizeAsync(segment, rewritten.RewrittenScript, ct);
        return (rewritten.RewrittenScript, newWav);
    }

    private async Task<byte[]> SynthesizeAsync(Segment segment, string scriptText, CancellationToken ct)
    {
        var request = new SynthesizeRequest
        {
            Text = scriptText,
            Filename = $"{segment.Id}.wav",
            Voice = segment.TtsConfig.Voice,
            Speed = segment.TtsConfig.Speed,
        };

        try
        {
            return await _synthesisService.GenerateVoiceAsync(request, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Synthesis failed for segment {Id}", segment.Id);
            throw;
        }
    }

    private static SegmentAudioResult ToSegmentResult(Segment segment, AudioNormalizationResult n) =>
        new()
        {
            SegmentId        = n.SegmentId,
            StartTimeSeconds = segment.StartTimeSeconds,
            ActualDuration   = n.ActualDuration,
            TargetDuration   = n.TargetDuration,
            ActionTaken      = n.ActionTaken,
            SpeedFactor      = n.SpeedFactor,
            WavBase64        = Convert.ToBase64String(n.WavBytes),
        };
}
