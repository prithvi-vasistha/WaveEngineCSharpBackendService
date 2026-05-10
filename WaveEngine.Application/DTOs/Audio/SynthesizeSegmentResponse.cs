namespace WaveEngine.Application.DTOs.Audio;

public enum SegmentSynthesisStatus
{
    /// <summary>
    /// Audio fits within the target duration (possibly sped up slightly).
    /// </summary>
    Ok,

    /// <summary>
    /// Audio exceeds the target duration even at the maximum allowed speed-up (1.15×).
    /// The returned WavBase64 contains the hard-capped audio (1.15× speed).
    /// The caller should offer the user a chance to rewrite or edit the script.
    /// </summary>
    OverLimit,

    /// <summary>
    /// Audio is shorter than the target duration by more than 1.0 s.
    /// The returned WavBase64 contains the raw (un-padded) audio so the user hears
    /// the real speech without dead silence appended.
    /// UnderLimitGap holds the seconds of missing content.
    /// </summary>
    UnderLimit,
}

/// <summary>
/// Result of synthesizing and normalizing a single segment.
/// </summary>
public class SynthesizeSegmentResponse
{
    public string SegmentId { get; init; } = string.Empty;
    public double StartTimeSeconds { get; init; }

    /// <summary>Actual audio duration after normalization (seconds).</summary>
    public double ActualDuration { get; init; }

    /// <summary>Target duration for this segment (seconds).</summary>
    public double TargetDuration { get; init; }

    /// <summary>Speed factor applied by normalization (1.0 = no change, >1 = sped up).</summary>
    public double SpeedFactor { get; init; }

    /// <summary>Normalized (or raw, for UnderLimit) WAV audio, Base64-encoded.</summary>
    public string WavBase64 { get; init; } = string.Empty;

    /// <summary>Synthesis outcome.</summary>
    public SegmentSynthesisStatus Status { get; init; }

    /// <summary>
    /// The speed factor that would have been required to fit the target.
    /// Only meaningful when Status = OverLimit.
    /// </summary>
    public double OverLimitFactor { get; init; }

    /// <summary>
    /// Seconds of silence that would be needed to fill the slot.
    /// Only meaningful when Status = UnderLimit (target - actual).
    /// </summary>
    public double UnderLimitGap { get; init; }
}
