using WaveEngine.Domain.Entities;

namespace WaveEngine.Application.DTOs.Pipeline;

/// <summary>
/// Returned by IPipelineOrchestrationService.ExecuteAsync.
/// Contains the path to the compiled MP4, the workspace directory, and the
/// full NarrationScript so callers can persist the master-plan without
/// re-fetching it.
/// </summary>
public class PipelineVideoOutput
{
    /// <summary>Absolute path to the compiled output MP4 file.</summary>
    public string VideoPath { get; init; } = string.Empty;

    /// <summary>
    /// Temporary workspace directory that owns the output file.
    /// Kept alive until the client downloads the video; deleted afterwards.
    /// </summary>
    public string WorkspaceDirectory { get; init; } = string.Empty;

    /// <summary>Project identifier sourced from the generated NarrationScript.</summary>
    public string ProjectId { get; init; } = string.Empty;

    /// <summary>The full narration script produced in Phase 1. Persisted as JSON by the job store.</summary>
    public NarrationScript Script { get; init; } = new();
}
