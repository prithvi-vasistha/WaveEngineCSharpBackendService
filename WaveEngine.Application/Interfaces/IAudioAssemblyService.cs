using WaveEngine.Application.DTOs.Audio;

namespace WaveEngine.Application.Interfaces;

/// <summary>
/// Assembles all per-segment narration WAVs into a single mixed audio track,
/// optionally layering a looping background music track underneath.
/// </summary>
public interface IAudioAssemblyService
{
    /// <summary>
    /// Mixes narration segments onto a timeline, optionally with background music.
    /// </summary>
    /// <param name="segments">
    ///   Normalized segment WAVs with their start-time positions on the video timeline.
    ///   Must be in chronological order.
    /// </param>
    /// <param name="backgroundMusic">
    ///   Background music config. Pass null or TrackFileName = null for no music.
    /// </param>
    /// <param name="totalDurationSeconds">
    ///   The total length of the output audio track (= last segment's end time).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Raw WAV bytes of the final mixed audio track.</returns>
    Task<byte[]> AssembleAsync(
        IReadOnlyList<SegmentAudioAssemblyInput> segments,
        BackgroundMusicConfig? backgroundMusic,
        double totalDurationSeconds,
        CancellationToken ct = default);
}
