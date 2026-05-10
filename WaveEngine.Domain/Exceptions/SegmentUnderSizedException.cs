namespace WaveEngine.Domain.Exceptions;

/// <summary>
/// Thrown when a synthesized segment is so short that slowing it down via atempo
/// would require a factor below the configured minimum (1 / MaxSpeedFactor).
/// The caller should trigger a script expansion (rewrite-longer) to add words.
/// </summary>
public class SegmentUnderSizedException : Exception
{
    public string SegmentId { get; }
    public double ActualDuration { get; }
    public double TargetDuration { get; }

    /// <summary>
    /// The atempo factor that would be required (&lt; 1.0).
    /// Values below 1/MaxSpeedFactor are too slow to apply naturally.
    /// </summary>
    public double SlowFactor { get; }

    public SegmentUnderSizedException(
        string segmentId,
        double actualDuration,
        double targetDuration,
        double slowFactor)
        : base(
            $"Segment '{segmentId}' is severely undersized: actual={actualDuration:F2}s, " +
            $"target={targetDuration:F2}s, required slow factor={slowFactor:F4}x " +
            $"is below the minimum allowed. Script expansion required.")
    {
        SegmentId = segmentId;
        ActualDuration = actualDuration;
        TargetDuration = targetDuration;
        SlowFactor = slowFactor;
    }
}
