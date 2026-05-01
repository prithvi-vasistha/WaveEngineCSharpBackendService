using WaveEngine.Application.DTOs.Audio;

namespace WaveEngine.Application.Interfaces;

public interface ITtsOrchestrationService
{
    /// <summary>
    /// Synthesizes and normalizes each segment in the script.
    /// Triggers the self-healing rewrite loop for segments that are too long.
    /// </summary>
    Task<OrchestrationResult> OrchestrateAsync(OrchestrateRequest request, CancellationToken ct = default);
}
