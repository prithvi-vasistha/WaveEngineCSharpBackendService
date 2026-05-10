using WaveEngine.Application.Models;
using WaveEngine.Domain.Entities;

namespace WaveEngine.Application.DTOs.Jobs;

/// <summary>
/// Snapshot of a job's current state, returned by GET /api/jobs/{jobId}.
/// </summary>
public class JobStatusResponse
{
    public string JobId { get; init; } = string.Empty;
    public JobStatus Status { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }

    /// <summary>Available once script generation completes.</summary>
    public NarrationScript? Script { get; init; }

    public List<SegmentJobResult> SegmentResults { get; init; } = [];

    /// <summary>Populated when Status = Complete. Use with /api/jobs/result/{token}.</summary>
    public string? ResultToken { get; init; }

    public string? ErrorMessage { get; init; }

    public static JobStatusResponse From(OrchestratorJob job) => new()
    {
        JobId          = job.JobId,
        Status         = job.Status,
        CreatedAt      = job.CreatedAt,
        StartedAt      = job.StartedAt,
        CompletedAt    = job.CompletedAt,
        Script         = job.Script,
        SegmentResults = job.SegmentResults,
        ResultToken    = job.ResultToken,
        ErrorMessage   = job.ErrorMessage,
    };
}
