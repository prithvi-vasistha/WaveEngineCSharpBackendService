using FFMpegCore;
using Microsoft.Extensions.Logging;
using WaveEngine.Application.DTOs.Audio;
using WaveEngine.Application.Interfaces;
using WaveEngine.Domain.Exceptions;

namespace WaveEngine.Infrastructure.Services;

public class AudioNormalizationService : IAudioNormalizationService
{
    private const double ToleranceSeconds = 0.2;
    private const double MaxSpeedFactor = 1.15;

    private readonly ILogger<AudioNormalizationService> _log;

    public AudioNormalizationService(ILogger<AudioNormalizationService> log)
    {
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
            if (Math.Abs(drift) <= ToleranceSeconds)
            {
                _log.LogInformation("Segment {Id}: within tolerance, no action needed.", segmentId);
                return new AudioNormalizationResult
                {
                    SegmentId = segmentId,
                    ActualDuration = actualDuration,
                    TargetDuration = targetDurationSeconds,
                    SpeedFactor = 1.0,
                    ActionTaken = NormalizationAction.None,
                    WavBytes = inputWavBytes,
                };
            }

            // Step 3: Audio is too long — apply atempo
            if (drift > ToleranceSeconds)
            {
                double speedFactor = actualDuration / targetDurationSeconds;

                if (speedFactor > MaxSpeedFactor)
                {
                    throw new SegmentOverSizedException(
                        segmentId, actualDuration, targetDurationSeconds, speedFactor);
                }

                _log.LogInformation(
                    "Segment {Id}: stretching with atempo={Factor:F4}", segmentId, speedFactor);

                try
                {
                    await FFMpegArguments
                        .FromFileInput(inputPath)
                        .OutputToFile(outputPath, overwrite: true, options => options
                            .WithCustomArgument($"-filter:a atempo={speedFactor:F4}"))
                        .ProcessAsynchronously();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "FFmpeg atempo failed for segment {Id} (factor={Factor:F4})",
                        segmentId, speedFactor);
                    throw;
                }

                return new AudioNormalizationResult
                {
                    SegmentId = segmentId,
                    ActualDuration = actualDuration,
                    TargetDuration = targetDurationSeconds,
                    SpeedFactor = speedFactor,
                    ActionTaken = NormalizationAction.Stretched,
                    WavBytes = await File.ReadAllBytesAsync(outputPath, ct),
                };
            }

            // Step 4: Audio is too short — pad with silence to exact target duration
            _log.LogInformation(
                "Segment {Id}: padding {Gap:F2}s of silence to fill target.", segmentId, -drift);

            try
            {
                await FFMpegArguments
                    .FromFileInput(inputPath)
                    .OutputToFile(outputPath, overwrite: true, options => options
                        .WithCustomArgument($"-filter:a apad -t {targetDurationSeconds:F4}"))
                    .ProcessAsynchronously();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "FFmpeg apad failed for segment {Id}", segmentId);
                throw;
            }

            return new AudioNormalizationResult
            {
                SegmentId = segmentId,
                ActualDuration = actualDuration,
                TargetDuration = targetDurationSeconds,
                SpeedFactor = 1.0,
                ActionTaken = NormalizationAction.Padded,
                WavBytes = await File.ReadAllBytesAsync(outputPath, ct),
            };
        }
        finally
        {
            // Clean up temp files regardless of outcome
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    public async Task<AudioNormalizationResult> ApplyHardCapAsync(
        byte[] inputWavBytes,
        double actualDurationSeconds,
        double targetDurationSeconds,
        string segmentId,
        CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "waveengine", $"{segmentId}_hardcap");
        Directory.CreateDirectory(tempDir);

        var inputPath = Path.Combine(tempDir, "input.wav");
        var outputPath = Path.Combine(tempDir, "output.wav");

        try
        {
            await File.WriteAllBytesAsync(inputPath, inputWavBytes, ct);

            _log.LogWarning(
                "Segment {Id}: applying hard-cap atempo={Factor}x. " +
                "Audio will bleed ({Actual:F2}s into {Target:F2}s slot). " +
                "Consider reducing the script for this segment.",
                segmentId, MaxSpeedFactor, actualDurationSeconds, targetDurationSeconds);

            await FFMpegArguments
                .FromFileInput(inputPath)
                .OutputToFile(outputPath, overwrite: true, options => options
                    .WithCustomArgument($"-filter:a atempo={MaxSpeedFactor:F4}"))
                .ProcessAsynchronously();

            return new AudioNormalizationResult
            {
                SegmentId = segmentId,
                ActualDuration = actualDurationSeconds,
                TargetDuration = targetDurationSeconds,
                SpeedFactor = MaxSpeedFactor,
                ActionTaken = NormalizationAction.Stretched,
                WavBytes = await File.ReadAllBytesAsync(outputPath, ct),
            };
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }
}
