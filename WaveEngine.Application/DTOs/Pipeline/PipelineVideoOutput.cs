namespace WaveEngine.Application.DTOs.Pipeline;

/// <summary>
/// Returned by IPipelineOrchestrationService.ExecuteAsync.
/// Contains the path to the compiled MP4 and the workspace directory that should
/// be deleted after the file has been streamed to the client.
/// </summary>
public class PipelineVideoOutput
{
    /// <summary>Absolute path to the compiled output MP4 file.</summary>
    public string VideoPath { get; init; } = string.Empty;

    /// <summary>
    /// Temporary workspace directory that owns the output file.
    /// The controller must delete this directory after streaming completes.
    /// </summary>
    public string WorkspaceDirectory { get; init; } = string.Empty;

    /// <summary>Project identifier sourced from the generated NarrationScript.</summary>
    public string ProjectId { get; init; } = string.Empty;
}
