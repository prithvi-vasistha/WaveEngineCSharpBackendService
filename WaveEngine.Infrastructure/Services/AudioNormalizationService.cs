using FFMpegCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaveEngine.Application.DTOs.Audio;
using WaveEngine.Application.Interfaces;
using WaveEngine.Application.Settings;
using WaveEngine.Domain.Exceptions;

namespace WaveEngine.Infrastructure.Services;

public class AudioNormalizationService : IAudioNormalizationService
{
    private readonly OrchestrationSettings _settings;
    private readonly ILogger<AudioNormalizationService> _log;

    public AudioNormalizationService(
        IOptions<OrchestrationSettings> settings,
        ILogger<AudioNormalizationService> log)
    {
        _settings = settings.Value;
        _log = log;
    }

    public async Task<AudioNormalizationResult> NormalizeAsync(
        byte[] inputWavBytes,
        double targetDurationSeconds,
        string segmentId,
        CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "waveengine", segmentId);
        Directory.CreateDirectory(tempDir);

        var inputPath = Path.Combine(tempDir, "input.wav");
        var outputPath = Path.Combine(tempDir, "output.wav");

        try
        {
            await File.WriteAllBytesAsync(inputPath, inputWavBytes, ct);

            // Step 1: Measure actual duration via FFProbe
            _log.LogInformation("FFProbe: analysing {Path}", inputPath);
            IMediaAnalysis mediaInfo;
            try
            {
                mediaInfo = await FFProbe.AnalyseAsync(inputPath, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "FFProbe failed for segment {Id} — is ffmpeg installed? Path={Path}",
                    segmentId, inputPath);
                throw;
            }

            double actualDuration = mediaInfo.Duration.TotalSeconds;
            double drift = actualDuration - targetDurationSeconds;

            _log.LogInformation(
                "Segment {Id}: actual={Actual:F2}s target={Target:F2}s drift={Drift:+0.00;-0.00}s",
                segmentId, actualDuration, targetDurationSeconds, drift);

            // Step 2: Within tolerance — return as-is
            if (Math.Abs(drift) <= _settings.ToleranceSeconds)
            {
                _log.LogInformation("Segment {Id}: within tolerance ({Tol:F2}s), no speed adjustment needed.",
                    segmentId, _settings.ToleranceSeconds);
                return new AudioNormalizationResult
                {
                    SegmentId      = segmentId,
                    ActualDuration = actualDuration,
                    TargetDuration = targetDurationSeconds,
                    SpeedFactor    = 1.0,
                    ActionTaken    = NormalizationAction.None,
                    WavBytes       = inputWavBytes,
                };
            }

            // Step 3: Audio is too long — speed up with atempo
            if (drift > _settings.ToleranceSeconds)
            {
                double speedFactor = actualDuration / targetDurationSeconds; // > 1.0

                if (speedFactor > _settings.MaxSpeedFactor)
                {
                    _log.LogWarning(
                        "Segment {Id}: required speed-up {Factor:F4}x exceeds cap {Cap:F4}x — LLM rewrite (CASE A) needed.",
                        segmentId, speedFactor, _settings.MaxSpeedFactor);
                    throw new SegmentOverSizedException(
                        segmentId, actualDuration, targetDurationSeconds, speedFactor);
                }

                _log.LogInformation(
                    "Segment {Id}: speeding up with atempo={Factor:F4}x (too long by {Drift:F2}s).",
                    segmentId, speedFactor, drift);

                await ApplyAtempoAsync(inputPath, outputPath, speedFactor, segmentId, ct);

                return new AudioNormalizationResult
                {
                    SegmentId      = segmentId,
                    ActualDuration = actualDuration,
                    TargetDuration = targetDurationSeconds,
                    SpeedFactor    = speedFactor,
                    ActionTaken    = NormalizationAction.Stretched,
                    WavBytes       = await File.ReadAllBytesAsync(outputPath, ct),
                };
            }

            // Step 4: Audio is too short — slow down with atempo
            {
                double slowFactor = actualDuration / targetDurationSeconds; // < 1.0
                double minSlowFactor = 1.0 / _settings.MaxSpeedFactor;

                if (slowFactor < minSlowFactor)
                {
                    _log.LogWarning(
                        "Segment {Id}: required slow-down {Factor:F4}x is below minimum {Min:F4}x — LLM rewrite (CASE B) needed.",
                        segmentId, slowFactor, minSlowFactor);
                    throw new SegmentUnderSizedException(
                        segmentId, actualDuration, targetDurationSeconds, slowFactor);
                }

                _log.LogInformation(
                    "Segment {Id}: slowing down with atempo={Factor:F4}x (too short by {Gap:F2}s).",
                    segmentId, slowFactor, -drift);

                await ApplyAtempoAsync(inputPath, outputPath, slowFactor, segmentId, ct);

                return new AudioNormalizationResult
                {
                    SegmentId      = segmentId,
                    ActualDuration = actualDuration,
                    TargetDuration = targetDurationSeconds,
                    SpeedFactor    = slowFactor,
                    ActionTaken    = NormalizationAction.Slowed,
                    WavBytes       = await File.ReadAllBytesAsync(outputPath, ct),
                };
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Best-effort fallback used after all LLM-rewrite retries are exhausted.
    /// Applies atempo at MaxSpeedFactor (too long) or 1/MaxSpeedFactor (too short).
    /// Gets the audio as close to the target as the speed-factor cap allows.
    /// </summary>
    public async Task<AudioNormalizationResult> ApplyFallbackAsync(
        byte[] inputWavBytes,
        double actualDurationSeconds,
        double targetDurationSeconds,
        string segmentId,
        CancellationToken ct = default)
    {
        bool tooLong = actualDurationSeconds > targetDurationSeconds;
        double fallbackFactor = tooLong
            ? _settings.MaxSpeedFactor          // speed up as much as allowed
            : 1.0 / _settings.MaxSpeedFactor;   // slow down as much as allowed

        var tempDir = Path.Combine(Path.GetTempPath(), "waveengine", $"{segmentId}_fallback");
        Directory.CreateDirectory(tempDir);

        var inputPath = Path.Combine(tempDir, "input.wav");
        var outputPath = Path.Combine(tempDir, "output.wav");

        try
        {
            await File.WriteAllBytesAsync(inputPath, inputWavBytes, ct);

            _log.LogWarning(
                "Segment {Id}: applying best-effort fallback atempo={Factor:F4}x " +
                "({Direction}) — audio will not perfectly fill its slot " +
                "(actual={Actual:F2}s, target={Target:F2}s). " +
                "Review the script word count for this segment.",
                segmentId, fallbackFactor,
                tooLong ? "speed-up" : "slow-down",
                actualDurationSeconds, targetDurationSeconds);

            await ApplyAtempoAsync(inputPath, outputPath, fallbackFactor, segmentId, ct);

            return new AudioNormalizationResult
            {
                SegmentId      = segmentId,
                ActualDuration = actualDurationSeconds,
                TargetDuration = targetDurationSeconds,
                SpeedFactor    = fallbackFactor,
                ActionTaken    = NormalizationAction.Fallback,
                WavBytes       = await File.ReadAllBytesAsync(outputPath, ct),
            };
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shared FFmpeg helper
    // ─────────────────────────────────────────────────────────────────────────

    private async Task ApplyAtempoAsync(
        string inputPath,
        string outputPath,
        double factor,
        string segmentId,
        CancellationToken ct)
    {
        try
        {
            await FFMpegArguments
                .FromFileInput(inputPath)
                .OutputToFile(outputPath, overwrite: true, options => options
                    .WithCustomArgument($"-filter:a atempo={factor:F4}"))
                .ProcessAsynchronously();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "FFmpeg atempo={Factor:F4} failed for segment {Id}.", factor, segmentId);
            throw;
        }
    }
}
