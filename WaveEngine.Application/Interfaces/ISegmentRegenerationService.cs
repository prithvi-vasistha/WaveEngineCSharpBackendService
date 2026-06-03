using WaveEngine.Application.DTOs.Pipeline;

namespace WaveEngine.Application.Interfaces;

public interface ISegmentRegenerationService
{
    /// <summary>
    /// Re-synthesizes a single segment (optionally AI-refined first), re-assembles the full
    /// audio mix from workspace WAVs, and re-compiles the final video in place.
    /// </summary>
    Task<RegenerateSegmentResponse> RegenerateAsync(
        RegenerateSegmentRequest request,
        CancellationToken ct = default);
}
