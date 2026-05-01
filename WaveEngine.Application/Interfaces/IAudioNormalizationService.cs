using WaveEngine.Application.DTOs.Audio;

namespace WaveEngine.Application.Interfaces;

public interface IAudioNormalizationService
{
    /// <summary>
    /// Measures actual duration then:
    /// - Does nothing if within 0.2s of target.
    /// - Applies atempo to speed up if too long (max 1.15x — throws SegmentOverSizedException beyond that).
    /// - Pads with silence if too short.
    /// </summary>
    Task<AudioNormalizationResult> NormalizeAsync(
        byte[] inputWavBytes,
        double targetDurationSeconds,
        string segmentId,
        CancellationToken ct = default);

    /// <summary>
    /// Hard-cap fallback: always applies exactly MaxSpeedFactor (1.15x) atempo.
    /// Used after all rewrite retries are exhausted. Never throws.
    /// </summary>
    Task<AudioNormalizationResult> ApplyHardCapAsync(
        byte[] inputWavBytes,
        double actualDurationSeconds,
        double targetDurationSeconds,
        string segmentId,
        CancellationToken ct = default);
}
