namespace WaveEngine.Application.DTOs.Pipeline;

public class RegenerateSegmentResponse
{
    public string SegmentId { get; init; } = string.Empty;

    /// <summary>The final script text that was synthesized (may differ from input if AI-refined).</summary>
    public string FinalScript { get; init; } = string.Empty;

    public float StartTimeSeconds { get; init; }
    public float EndTimeSeconds { get; init; }

    /// <summary>
    /// Video URL with a cache-buster query param so the player reloads the updated file.
    /// e.g. /api/pipeline/video/{jobId}?v=1715000000000
    /// </summary>
    public string VideoUrl { get; init; } = string.Empty;
}
