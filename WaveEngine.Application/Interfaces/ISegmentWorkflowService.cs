using WaveEngine.Application.DTOs.Audio;

namespace WaveEngine.Application.Interfaces;

public interface ISegmentWorkflowService
{
    /// <summary>
    /// Synthesizes and normalizes a single segment.
    /// Owns the serialization semaphore (one TTS request at a time).
    /// Returns Ok, OverLimit (with hard-capped audio), or UnderLimit (with raw audio).
    /// </summary>
    Task<SynthesizeSegmentResponse> SynthesizeAsync(SynthesizeSegmentRequest request, CancellationToken ct);
}
