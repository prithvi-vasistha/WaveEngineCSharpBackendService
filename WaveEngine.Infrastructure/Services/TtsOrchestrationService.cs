using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaveEngine.Application.DTOs.Audio;
using WaveEngine.Application.DTOs.Pipeline;
using WaveEngine.Application.DTOs.Script;
using WaveEngine.Application.DTOs.Synthesize;
using WaveEngine.Application.Interfaces;
using WaveEngine.Application.Settings;
using WaveEngine.Domain.Entities;
using WaveEngine.Domain.Exceptions;

namespace WaveEngine.Infrastructure.Services;

public class TtsOrchestrationService : ITtsOrchestrationService
{
    private readonly OrchestrationSettings _settings;
    private readonly IScriptGenerationService _scriptService;
    private readonly ITtsSynthesisService _synthesisService;
    private readonly IAudioNormalizationService _normalizationService;
    private readonly IAudioAssemblyService _assemblyService;
    private readonly ILogger<TtsOrchestrationService> _log;

    public TtsOrchestrationService(
        IOptions<OrchestrationSettings> settings,
        IScriptGenerationService scriptService,
        ITtsSynthesisService synthesisService,
        IAudioNormalizationService normalizationService,
        IAudioAssemblyService assemblyService,
        ILogger<TtsOrchestrationService> log)
    {
        _settings = settings.Value;
        _scriptService = scriptService;
        _synthesisService = synthesisService;
        _normalizationService = normalizationService;
        _assemblyService = assemblyService;
        _log = log;
    }

    public async Task<OrchestrationResult> OrchestrateAsync(
        OrchestrateRequest request,
        IProgress<(int Completed, int Total)>? segmentProgress = null,
        CancellationToken ct = default)
    {
        var script = request.Script;

        if (script.Segments is not { Count: > 0 })
            throw new ArgumentException(
                "request.script.segments is empty or missing. " +
                "Ensure the NarrationScript is nested under the \"script\" key in the request body.");

        int parallelism = Math.Max(1, _settings.MaxTtsParallelism);
        int totalSegments = script.Segments.Count;

        _log.LogInformation(
            "Orchestration: synthesizing {Count} segments for project '{Id}' " +
            "(tolerance=±{Tol:F2}s, maxSpeedFactor={Factor:F2}x, maxRetries={Retries}, parallelism={P})",
            totalSegments, script.ProjectId,
            _settings.ToleranceSeconds, _settings.MaxSpeedFactor,
            _settings.MaxRetryAttempts, parallelism);

        if (!string.IsNullOrWhiteSpace(request.VoiceOverride))
            _log.LogInformation(
                "Orchestration: voice_override='{Voice}' — all segments will use this voice.",
                request.VoiceOverride);

        // ── Phase 1: Parallel segment synthesis ───────────────────────────────
        using var fatalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var semaphore = new SemaphoreSlim(parallelism, parallelism);

        var orderedResults  = new (SegmentAudioResult Dto, byte[] RawWav)[totalSegments];
        var assemblyInputs  = new SegmentAudioAssemblyInput[totalSegments];
        int completedCount  = 0;

        var tasks = script.Segments.Select((segment, index) =>
            SynthesizeWithSemaphoreAsync(
                semaphore, segment, index, request.AiConfig, request.VoiceOverride,
                orderedResults, assemblyInputs, fatalCts,
                onComplete: () =>
                {
                    int n = Interlocked.Increment(ref completedCount);
                    segmentProgress?.Report((n, totalSegments));
                },
                fatalCts.Token));

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "One or more segment synthesis tasks failed with a fatal error. " +
                "The pipeline has been aborted.");
        }

        // ── Phase 2: Audio assembly ───────────────────────────────────────────
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
            Script            = script,
            SegmentAudio      = orderedResults.Select(r => r.Dto).ToList(),
            FinalMixWavBase64 = Convert.ToBase64String(finalMixWav),
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Semaphore wrapper
    // ─────────────────────────────────────────────────────────────────────────

    private async Task SynthesizeWithSemaphoreAsync(
        SemaphoreSlim semaphore,
        Segment segment,
        int index,
        AiConfigDto aiConfig,
        string? voiceOverride,
        (SegmentAudioResult Dto, byte[] RawWav)[] results,
        SegmentAudioAssemblyInput[] assemblyInputs,
        CancellationTokenSource fatalCts,
        Action onComplete,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            var (dto, rawWav) = await ProcessSegmentAsync(
                segment, aiConfig, voiceOverride, ct);

            results[index] = (dto, rawWav);
            assemblyInputs[index] = new SegmentAudioAssemblyInput
            {
                SegmentId        = segment.Id,
                StartTimeSeconds = segment.StartTimeSeconds,
                WavBytes         = rawWav,
            };

            onComplete();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Segment {Id}: FATAL error during synthesis. Cancelling all remaining segments.",
                segment.Id);
            fatalCts.Cancel();
            throw;
        }
        finally
        {
            semaphore.Release();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-segment synthesis + rewrite loop
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<(SegmentAudioResult Dto, byte[] RawWav)> ProcessSegmentAsync(
        Segment segment,
        AiConfigDto aiConfig,
        string? voiceOverride,
        CancellationToken ct)
    {
        _log.LogInformation(
            "Segment {Id}: starting synthesis loop (target={Duration:F1}s, voice={Voice})",
            segment.Id, segment.TargetDuration,
            voiceOverride ?? segment.TtsConfig.Voice);

        var currentScript = segment.Script;
        var rawWav = await SynthesizeAsync(segment, currentScript, voiceOverride, ct);

        var failedAttempts = new List<SegmentSynthesisAttempt>();

        for (int attempt = 0; attempt <= _settings.MaxRetryAttempts; attempt++)
        {
            try
            {
                var normResult = await _normalizationService.NormalizeAsync(
                    rawWav, segment.TargetDuration, segment.Id, ct);

                if (attempt > 0)
                    _log.LogInformation(
                        "Segment {Id}: accepted after {N} rewrite(s). action={Action} speedFactor={Factor:F4}x",
                        segment.Id, attempt, normResult.ActionTaken, normResult.SpeedFactor);

                return (ToSegmentResult(segment, normResult), normResult.WavBytes);
            }
            catch (SegmentOverSizedException ex)
            {
                _log.LogWarning(
                    "Segment {Id} [attempt {Attempt}/{Max}]: CASE A — too long " +
                    "(actual={Actual:F2}s target={Target:F2}s requiredFactor={Factor:F4}x). " +
                    "Requesting shorter rewrite…",
                    ex.SegmentId, attempt, _settings.MaxRetryAttempts,
                    ex.ActualDuration, ex.TargetDuration, ex.SpeedFactor);

                failedAttempts.Add(new SegmentSynthesisAttempt
                {
                    AttemptNumber  = attempt,
                    Script         = currentScript,
                    WavBytes       = rawWav,
                    ActualDuration = ex.ActualDuration,
                    Delta          = ex.ActualDuration - ex.TargetDuration,
                });

                if (attempt == _settings.MaxRetryAttempts) break;

                (currentScript, rawWav) = await RewriteAndSynthesizeAsync(
                    segment, currentScript, aiConfig, voiceOverride, RewriteDirection.Shorter, ct);
            }
            catch (SegmentUnderSizedException ex)
            {
                _log.LogWarning(
                    "Segment {Id} [attempt {Attempt}/{Max}]: CASE B — too short " +
                    "(actual={Actual:F2}s target={Target:F2}s requiredFactor={Factor:F4}x). " +
                    "Requesting longer rewrite…",
                    ex.SegmentId, attempt, _settings.MaxRetryAttempts,
                    ex.ActualDuration, ex.TargetDuration, ex.SlowFactor);

                failedAttempts.Add(new SegmentSynthesisAttempt
                {
                    AttemptNumber  = attempt,
                    Script         = currentScript,
                    WavBytes       = rawWav,
                    ActualDuration = ex.ActualDuration,
                    Delta          = ex.ActualDuration - ex.TargetDuration,
                });

                if (attempt == _settings.MaxRetryAttempts) break;

                (currentScript, rawWav) = await RewriteAndSynthesizeAsync(
                    segment, currentScript, aiConfig, voiceOverride, RewriteDirection.Longer, ct);
            }
        }

        var best = failedAttempts.MinBy(a => Math.Abs(a.Delta))!;

        _log.LogWarning(
            "⚠️  Segment {Id}: all {Max} rewrite attempt(s) exhausted. " +
            "Best attempt was #{Num} (delta={Delta:+0.00;-0.00}s). " +
            "Applying best-effort fallback atempo.",
            segment.Id, _settings.MaxRetryAttempts, best.AttemptNumber, best.Delta);

        var fallbackResult = await _normalizationService.ApplyFallbackAsync(
            best.WavBytes, best.ActualDuration, segment.TargetDuration, segment.Id, ct);

        return (ToSegmentResult(segment, fallbackResult), fallbackResult.WavBytes);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LLM rewrite + re-synthesis
    // ─────────────────────────────────────────────────────────────────────────

    private enum RewriteDirection { Shorter, Longer }

    private async Task<(string newScript, byte[] rawWav)> RewriteAndSynthesizeAsync(
        Segment segment,
        string currentScript,
        AiConfigDto aiConfig,
        string? voiceOverride,
        RewriteDirection rewriteDirection,
        CancellationToken ct)
    {
        int originalWordCount = currentScript.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        RewrittenScriptDto rewritten;

        if (rewriteDirection == RewriteDirection.Shorter)
        {
            rewritten = await _scriptService.RewriteShorterAsync(new RewriteShorterRequest
            {
                OriginalScript        = currentScript,
                TargetDurationSeconds = segment.TargetDuration,
                AiConfig              = aiConfig,
                Keywords              = [],
            }, ct);

            _log.LogInformation(
                "Segment {Id}: rewritten SHORTER — {Old} → {New} words (word-count limit {Max}).",
                segment.Id, originalWordCount, rewritten.WordCount, rewritten.MaxWordCount);
        }
        else
        {
            rewritten = await _scriptService.RewriteLongerAsync(new RewriteLongerRequest
            {
                OriginalScript        = currentScript,
                TargetDurationSeconds = segment.TargetDuration,
                AiConfig              = aiConfig,
                Keywords              = [],
            }, ct);

            _log.LogInformation(
                "Segment {Id}: rewritten LONGER — {Old} → {New} words (target min {Max}).",
                segment.Id, originalWordCount, rewritten.WordCount, rewritten.MaxWordCount);
        }

        var newWav = await SynthesizeAsync(segment, rewritten.RewrittenScript, voiceOverride, ct);
        return (rewritten.RewrittenScript, newWav);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TTS synthesis
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<byte[]> SynthesizeAsync(
        Segment segment,
        string scriptText,
        string? voiceOverride,
        CancellationToken ct)
    {
        var request = new SynthesizeRequest
        {
            Text     = scriptText,
            Filename = $"{segment.Id}.wav",
            Voice    = voiceOverride ?? segment.TtsConfig.Voice,
            Speed    = segment.TtsConfig.Speed,
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

    // ─────────────────────────────────────────────────────────────────────────
    // Result mapping
    // ─────────────────────────────────────────────────────────────────────────

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
