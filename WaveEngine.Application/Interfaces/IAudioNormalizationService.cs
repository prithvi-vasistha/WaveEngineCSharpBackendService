using WaveEngine.Application.DTOs.Audio;

namespace WaveEngine.Application.Interfaces;

public interface IAudioNormalizationService
{
    /// <summary>
    /// Measures actual duration then:
    /// - Does nothing if within ToleranceSeconds of target.
    /// - Applies atempo &gt; 1.0 to speed up if too long  (max MaxSpeedFactor — throws SegmentOverSizedException beyond that).
    /// - Applies atempo &lt; 1.0 to slow down if too short (min 1/MaxSpeedFactor — throws SegmentUnderSizedException beyond that).
    /// </summary>
    Task<AudioNormalizationResult> NormalizeAsync(
        byte[] inputWavBytes,
        double targetDurationSeconds,
        string segmentId,
        CancellationToken ct = default);

    /// <summary>
    /// Best-effort fallback used after all LLM-rewrite retries are exhausted.
    /// Applies atempo at MaxSpeedFactor (too long) or 1/MaxSpeedFactor (too short).
    /// Gets the audio as close to the target as the speed-factor cap allows. Never throws.
    /// </summary>
    Task<AudioNormalizationResult> ApplyFallbackAsync(
        byte[] inputWavBytes,
        double actualDurationSeconds,
        double targetDurationSeconds,
        string segmentId,
        CancellationToken ct = default);
}
