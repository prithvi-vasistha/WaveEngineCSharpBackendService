using WaveEngine.Application.DTOs.Audio;
using WaveEngine.Application.Models;

namespace WaveEngine.Application.Interfaces;

/// <summary>
/// Drives the end-to-end orchestration lifecycle for a job:
/// script generation → parallel segment synthesis (with retry) → assembly → video compilation.
/// </summary>
public interface IJobOrchestrator
{
    /// <summary>
    /// Starts the orchestration pipeline for the given job as a fire-and-forget background task.
    /// Progress events are published to the job's SSE channel via <see cref="IJobStore"/>.
    /// </summary>
    void StartJob(OrchestratorJob job);

    /// <summary>
    /// Re-synthesizes a single segment using the provided script override.
    /// Used in the review phase when the user manually edits a script.
    /// Saves the WAV and updates the job record on success.
    /// </summary>
    Task<SynthesizeSegmentResponse> RerunSegmentAsync(
        string jobId,
        string segmentId,
        string script,
        string? voiceOverride,
        CancellationToken ct);

    /// <summary>
    /// Re-assembles and re-compiles the job using the current saved segment WAVs.
    /// Returns the new result token.
    /// </summary>
    Task<string> RecompileAsync(string jobId, CancellationToken ct);
}
