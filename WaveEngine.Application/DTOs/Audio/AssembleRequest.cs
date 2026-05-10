namespace WaveEngine.Application.DTOs.Audio;

/// <summary>One resolved segment ready for assembly into the final mix.</summary>
public class AssembleSegmentInput
{
    public string SegmentId { get; set; } = string.Empty;
    public double StartTimeSeconds { get; set; }

    /// <summary>
    /// End time of this segment on the video timeline.
    /// Used as the fallback total-duration when VideoDurationSeconds is not supplied.
    /// </summary>
    public double EndTimeSeconds { get; set; }

    /// <summary>Normalized WAV audio, Base64-encoded.</summary>
    public string WavBase64 { get; set; } = string.Empty;
}

public class AssembleRequest
{
    /// <summary>All resolved segments (ok or user-accepted over-limit), in timeline order.</summary>
    public List<AssembleSegmentInput> Segments { get; set; } = [];

    /// <summary>Optional background music. Null or empty TrackFileName = no music.</summary>
    public BackgroundMusicConfig? BackgroundMusic { get; set; }

    /// <summary>
    /// Duration of the source video in seconds. When provided, the background music loop
    /// is extended to cover the full video rather than stopping after the last narration segment.
    /// Omit (or pass null / 0) to fall back to max(segment.EndTimeSeconds).
    /// </summary>
    public double? VideoDurationSeconds { get; set; }
}
