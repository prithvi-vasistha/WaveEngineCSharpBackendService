namespace WaveEngine.Application.DTOs.Pipeline;

public class MoveSegmentRequest
{
    public Guid JobId { get; init; }
    public string SegmentId { get; init; } = string.Empty;
    /// <summary>New start time in seconds. End time is shifted by the same delta.</summary>
    public float NewStartTimeSeconds { get; init; }
}
