using WaveEngine.Application.DTOs.Pipeline;

namespace WaveEngine.Application.Interfaces;

public interface ISegmentMoveService
{
    /// <summary>
    /// Shifts a segment to a new start time, re-assembles the audio mix from existing
    /// workspace WAVs (no TTS call), and recompiles the video in place.
    /// </summary>
    Task<MoveSegmentResponse> MoveAsync(MoveSegmentRequest request, CancellationToken ct = default);
}
