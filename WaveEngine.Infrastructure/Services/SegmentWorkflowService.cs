using Microsoft.Extensions.Logging;
using WaveEngine.Application.DTOs.Audio;
using WaveEngine.Application.DTOs.Synthesize;
using WaveEngine.Application.Interfaces;
using WaveEngine.Domain.Exceptions;

namespace WaveEngine.Infrastructure.Services;

public class SegmentWorkflowService : ISegmentWorkflowService
{
    private const double UnderLimitThresholdSeconds = 1.0;

    // One TTS synthesis at a time: the Kokoro ML model is single-threaded and
    // memory-intensive; concurrent requests cause OOM kills (exit 137).
    private static readonly SemaphoreSlim _synthesisSemaphore = new(1, 1);

    private readonly ITtsSynthesisService _synthesisService;
    private readonly IAudioNormalizationService _normalizationService;
    private readonly ILogger<SegmentWorkflowService> _log;

    public SegmentWorkflowService(
        ITtsSynthesisService synthesisService,
        IAudioNormalizationService normalizationService,
        ILogger<SegmentWorkflowService> log)
    {
        _synthesisService     = synthesisService;
        _normalizationService = normalizationService;
        _log                  = log;
    }

    public async Task<SynthesizeSegmentResponse> SynthesizeAsync(
        SynthesizeSegmentRequest request,
        CancellationToken ct)
    {
        var seg   = request.Segment;
        var voice = request.VoiceOverride ?? seg.TtsConfig?.Voice ?? "am_adam";

        _log.LogInformation(
            "SegmentWorkflow — {Id}: waiting for semaphore (target={Target:F1}s voice={Voice})…",
            seg.Id, seg.TargetDuration, voice);

        await _synthesisSemaphore.WaitAsync(ct);
        _log.LogInformation("SegmentWorkflow — {Id}: semaphore acquired.", seg.Id);

        try
        {
            // ── 1. Synthesize text → raw WAV ────────────────────────────────
            var synthReq = new SynthesizeRequest
            {
                Text     = seg.Script,
                Filename = $"{seg.Id}.wav",
                Voice    = voice,
                Speed    = seg.TtsConfig?.Speed ?? 1.0f,
            };
            var rawWav = await _synthesisService.GenerateVoiceAsync(synthReq, ct);

            // ── 2. Normalize duration ───────────────────────────────────────
            double targetDuration = seg.TargetDuration; // float → double implicit cast
            try
            {
                var norm = await _normalizationService.NormalizeAsync(rawWav, targetDuration, seg.Id, ct);

                // Under-limit detection: padding gap > threshold → surface as UnderLimit
                if (norm.ActionTaken == NormalizationAction.Padded)
                {
                    double gap = targetDuration - norm.ActualDuration;
                    if (gap > UnderLimitThresholdSeconds)
                    {
                        _log.LogInformation(
                            "SegmentWorkflow — {Id}: UNDER_LIMIT actual={Actual:F2}s target={Target:F2}s gap={Gap:F2}s",
                            seg.Id, norm.ActualDuration, targetDuration, gap);

                        return new SynthesizeSegmentResponse
                        {
                            SegmentId        = seg.Id,
                            StartTimeSeconds = seg.StartTimeSeconds,
                            ActualDuration   = norm.ActualDuration,
                            TargetDuration   = targetDuration,
                            SpeedFactor      = 1.0,
                            WavBase64        = Convert.ToBase64String(rawWav), // raw, not padded
                            Status           = SegmentSynthesisStatus.UnderLimit,
                            UnderLimitGap    = gap,
                        };
                    }
                }

                _log.LogInformation(
                    "SegmentWorkflow — {Id}: OK action={Action} actual={Actual:F2}s",
                    seg.Id, norm.ActionTaken, norm.ActualDuration);

                return new SynthesizeSegmentResponse
                {
                    SegmentId        = norm.SegmentId,
                    StartTimeSeconds = seg.StartTimeSeconds,
                    ActualDuration   = norm.ActualDuration,
                    TargetDuration   = norm.TargetDuration,
                    SpeedFactor      = norm.SpeedFactor,
                    WavBase64        = Convert.ToBase64String(norm.WavBytes),
                    Status           = SegmentSynthesisStatus.Ok,
                };
            }
            catch (SegmentOverSizedException ex)
            {
                var hardCap = await _normalizationService.ApplyHardCapAsync(
                    rawWav, ex.ActualDuration, ex.TargetDuration, seg.Id, ct);

                _log.LogWarning(
                    "SegmentWorkflow — {Id}: OVER_LIMIT actual={Actual:F2}s target={Target:F2}s factor={Factor:F2}x",
                    ex.SegmentId, ex.ActualDuration, ex.TargetDuration, ex.SpeedFactor);

                return new SynthesizeSegmentResponse
                {
                    SegmentId        = ex.SegmentId,
                    StartTimeSeconds = seg.StartTimeSeconds,
                    ActualDuration   = ex.ActualDuration,
                    TargetDuration   = ex.TargetDuration,
                    SpeedFactor      = ex.SpeedFactor,
                    WavBase64        = Convert.ToBase64String(hardCap.WavBytes),
                    Status           = SegmentSynthesisStatus.OverLimit,
                    OverLimitFactor  = ex.SpeedFactor,
                };
            }
        }
        finally
        {
            _synthesisSemaphore.Release();
            _log.LogInformation("SegmentWorkflow — {Id}: semaphore released.", seg.Id);
        }
    }
}
