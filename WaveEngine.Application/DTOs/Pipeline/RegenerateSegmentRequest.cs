namespace WaveEngine.Application.DTOs.Pipeline;

public class RegenerateSegmentRequest
{
    /// <summary>The job that owns the segment.</summary>
    public Guid JobId { get; init; }

    /// <summary>Slug id of the segment to regenerate, e.g. "seg_01".</summary>
    public string SegmentId { get; init; } = string.Empty;

    /// <summary>
    /// The script text to synthesize. This may be the user's manually-edited text OR
    /// the original script (when AiInstruction is set, the backend refines it first).
    /// </summary>
    public string NewScript { get; init; } = string.Empty;

    /// <summary>
    /// Optional new start time in seconds. When set, the segment is repositioned
    /// on the timeline before audio assembly.
    /// </summary>
    public float? NewStartTimeSeconds { get; init; }

    /// <summary>
    /// When provided the backend calls the LLM to refine NewScript according to
    /// this instruction before synthesizing. The refined script is returned in the response.
    /// </summary>
    public string? AiInstruction { get; init; }
}