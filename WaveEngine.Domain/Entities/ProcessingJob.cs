namespace WaveEngine.Domain.Entities;

/// <summary>
/// Persisted record for one end-to-end voice-over pipeline execution.
/// Written to SQLite by JobRepository and polled by the frontend.
/// </summary>
public class ProcessingJob
{
    /// <summary>Primary key — returned to the client as the polling handle.</summary>
    public Guid JobId { get; set; } = Guid.NewGuid();

    public JobStatus Status { get; set; } = JobStatus.Created;

    /// <summary>0–100 coarse progress for the progress bar.</summary>
    public int ProgressPercentage { get; set; }

    /// <summary>Human-readable status sent to the UI (e.g. "Synthesizing segment 3 of 5…").</summary>
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>
    /// JSON-serialised NarrationScript (MasterPlan).
    /// Populated when Status == Completed.
    /// </summary>
    public string? ResultData { get; set; }

    /// <summary>Absolute path to the compiled MP4. Kept alive until the client downloads it.</summary>
    public string? VideoPath { get; set; }

    /// <summary>Temp workspace directory that owns VideoPath. Cleaned up after download.</summary>
    public string? WorkspaceDirectory { get; set; }

    /// <summary>Human-readable error when Status == Failed.</summary>
    public string? ErrorMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
