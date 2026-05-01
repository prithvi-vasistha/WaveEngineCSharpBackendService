namespace WaveEngine.Application.DTOs.Audio;

/// <summary>
/// Packages a normalized segment WAV together with its position on the video timeline,
/// ready to be handed to the audio assembly step.
/// </summary>
public class SegmentAudioAssemblyInput
{
    public string SegmentId { get; init; } = string.Empty;

    /// <summary>Seconds from the start of the video where this segment begins.</summary>
    public double StartTimeSeconds { get; init; }

    /// <summary>Normalized WAV audio bytes for this segment.</summary>
    public byte[] WavBytes { get; init; } = [];
}
