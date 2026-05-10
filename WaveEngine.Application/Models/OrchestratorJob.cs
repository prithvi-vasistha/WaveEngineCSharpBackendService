using WaveEngine.Application.DTOs.Jobs;
using WaveEngine.Domain.Entities;

namespace WaveEngine.Application.Models;

public enum JobStatus { Created, Running, Complete, Failed }

/// <summary>
/// Represents the full lifecycle state of a background orchestration job.
/// Persisted to disk so jobs survive server restarts.
/// </summary>
public class OrchestratorJob
{
    public string JobId { get; set; } = string.Empty;
    public JobStatus Status { get; set; } = JobStatus.Created;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Job input parameters (minus the video bytes, stored as a file).</summary>
    public CreateJobRequest Request { get; set; } = new();

    /// <summary>
    /// Extension of the original uploaded video file (e.g. ".mp4", ".mov").
    /// Used to reconstruct the input file path on disk.
    /// </summary>
    public string VideoFileExtension { get; set; } = ".mp4";

    /// <summary>The narration script generated during Phase 1. Set once script generation succeeds.</summary>
    public NarrationScript? Script { get; set; }

    /// <summary>Per-segment synthesis results, populated incrementally as each segment completes.</summary>
    public List<SegmentJobResult> SegmentResults { get; set; } = [];

    /// <summary>Opaque token used in the preview/download URL. Set when the job reaches Complete.</summary>
    public string? ResultToken { get; set; }

    /// <summary>Human-readable error, populated on Failed status.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Outcome record for a single synthesized segment within a job.
/// </summary>
public class SegmentJobResult
{
    public string SegmentId { get; set; } = string.Empty;
    public string FinalScript { get; set; } = string.Empty;
    public double ActualDuration { get; set; }
    public double TargetDuration { get; set; }

    /// <summary>True when the segment was accepted as-is after exhausting all retries.</summary>
    public bool NeedsReview { get; set; }

    /// <summary>"over_limit" | "under_limit" — reason the segment needs review.</summary>
    public string? NeedsReviewReason { get; set; }

    /// <summary>Silence gap in seconds (only meaningful for under_limit).</summary>
    public double? Gap { get; set; }

    /// <summary>Relative file path within the job directory (e.g. "segments/seg_001.wav").</summary>
    public string WavFileName { get; set; } = string.Empty;
}
