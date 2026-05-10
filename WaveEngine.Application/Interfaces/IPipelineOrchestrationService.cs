using WaveEngine.Application.DTOs.Jobs;
using WaveEngine.Application.DTOs.Pipeline;

namespace WaveEngine.Application.Interfaces;

/// <summary>
/// Coordinates the full end-to-end pipeline:
///   1. Script generation (LLM)
///   2. Per-segment TTS synthesis with normalization and LLM-rewrite retry loop
///   3. Audio assembly (FFmpeg)
///   4. Video compilation (FFmpeg)
///
/// Reports progress through the optional <paramref name="progress"/> callback so the
/// caller can persist milestone updates without coupling the service to the job store.
/// </summary>
public interface IPipelineOrchestrationService
{
    Task<PipelineVideoOutput> ExecuteAsync(
        Stream videoStream,
        string videoFileName,
        ExecutePipelineRequest request,
        IProgress<JobProgressUpdate>? progress = null,
        CancellationToken ct = default);
}
