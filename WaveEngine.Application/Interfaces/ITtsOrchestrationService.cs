using WaveEngine.Application.DTOs.Audio;

namespace WaveEngine.Application.Interfaces;

public interface ITtsOrchestrationService
{
    /// <summary>
    /// Synthesizes and normalizes each segment in the script in parallel
    /// (up to MaxTtsParallelism concurrent tasks).
    /// Triggers the self-healing LLM-rewrite retry loop for segments outside tolerance.
    ///
    /// The optional <paramref name="segmentProgress"/> callback receives
    /// (completedCount, totalCount) after each segment finishes, so the caller
    /// can map these to a coarse progress percentage.
    /// </summary>
    Task<OrchestrationResult> OrchestrateAsync(
        OrchestrateRequest request,
        IProgress<(int Completed, int Total)>? segmentProgress = null,
        CancellationToken ct = default);
}
