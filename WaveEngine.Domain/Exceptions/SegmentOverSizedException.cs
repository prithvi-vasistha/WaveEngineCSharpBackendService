namespace WaveEngine.Domain.Exceptions;

/// <summary>
/// Thrown when a synthesized segment is so far over its target duration that
/// applying atempo would produce an unnaturally fast result (speed factor > 1.25).
/// The caller should either truncate the segment or trigger a script regeneration.
/// </summary>
public class SegmentOverSizedException : Exception
{
    public string SegmentId { get; }
    public double ActualDuration { get; }
    public double TargetDuration { get; }
    public double SpeedFactor { get; }

    public SegmentOverSizedException(
        string segmentId,
        double actualDuration,
        double targetDuration,
        double speedFactor)
        : base(
            $"Segment '{segmentId}' is severely oversized: actual={actualDuration:F2}s, " +
            $"target={targetDuration:F2}s, required speed factor={speedFactor:F2}x " +
            $"exceeds the 1.25x limit. Script regeneration or truncation required.")
    {
        SegmentId = segmentId;
        ActualDuration = actualDuration;
        TargetDuration = targetDuration;
        SpeedFactor = speedFactor;
    }
}
