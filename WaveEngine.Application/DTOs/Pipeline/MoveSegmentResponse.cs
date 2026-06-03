namespace WaveEngine.Application.DTOs.Pipeline;

public class MoveSegmentResponse
{
    public string SegmentId { get; init; } = string.Empty;
    public float StartTimeSeconds { get; init; }
    public float EndTimeSeconds { get; init; }
    /// <summary>Cache-busted video URL so the player reloads the updated file.</summary>
    public string VideoUrl { get; init; } = string.Empty;
}
