using WaveEngine.Domain.Entities;

namespace WaveEngine.Application.DTOs.Audio;

/// <summary>
/// Request to synthesize a single segment without auto-rewrite.
/// The caller is responsible for deciding what to do if the segment is over-limit.
/// </summary>
public class SynthesizeSegmentRequest
{
    /// <summary>The segment to synthesize. The segment's Script field is used as the TTS input.</summary>
    public Segment Segment { get; set; } = new();

    /// <summary>
    /// When set, overrides the voice in Segment.TtsConfig for this synthesis.
    /// Common values: "af_heart" (female), "am_adam" (male).
    /// </summary>
    public string? VoiceOverride { get; set; }
}
