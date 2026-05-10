using System.Text.Json;

namespace WaveEngine.Application.DTOs.Jobs;

/// <summary>
/// Returned by GET /api/pipeline/status/{jobId}.
/// The frontend polls this until Status is "Completed" or "Failed".
/// </summary>
public class JobStatusResponse
{
    public Guid JobId { get; init; }

    /// <summary>String representation of JobStatus enum (e.g. "Synthesizing").</summary>
    public string Status { get; init; } = string.Empty;

    public int ProgressPercentage { get; init; }

    public string StatusMessage { get; init; } = string.Empty;

    /// <summary>
    /// Relative URL to stream the final MP4.
    /// Only present when Status == "Completed".
    /// </summary>
    public string? VideoUrl { get; init; }

    /// <summary>
    /// The full NarrationScript (MasterPlan) as a raw JSON element.
    /// Only present when Status == "Completed".
    /// Passed through without re-encoding so the frontend receives the original structure.
    /// </summary>
    public JsonElement? MasterPlan { get; init; }

    /// <summary>Only present when Status == "Failed".</summary>
    public string? ErrorMessage { get; init; }
}
