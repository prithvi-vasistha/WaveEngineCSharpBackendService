using WaveEngine.Application.DTOs.Pipeline;

namespace WaveEngine.Application.Interfaces;

/// <summary>
/// Coordinates the full end-to-end pipeline:
///   1. Script generation (LLM)
///   2. Per-segment TTS synthesis with normalization and LLM-rewrite retry loop
///   3. Audio assembly (FFmpeg)
///   4. Video compilation (FFmpeg)
///
/// Returns a <see cref="PipelineVideoOutput"/> pointing to the compiled MP4 on disk.
/// The caller is responsible for streaming the file and deleting the workspace directory.
/// </summary>
public interface IPipelineOrchestrationService
{
    Task<PipelineVideoOutput> ExecuteAsync(
        Stream videoStream,
        string videoFileName,
        ExecutePipelineRequest request,
        CancellationToken ct = default);
}
