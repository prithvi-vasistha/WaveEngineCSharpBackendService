using WaveEngine.Application.DTOs.Audio;
using WaveEngine.Application.DTOs.Script;

namespace WaveEngine.Application.DTOs.Jobs;

/// <summary>
/// Payload submitted when creating a new orchestration job.
/// Sent as a JSON form field alongside the video binary in a multipart request.
/// </summary>
public class CreateJobRequest
{
    public string ProjectName { get; set; } = string.Empty;
    public string GlobalContext { get; set; } = string.Empty;
    public AiConfigDto AiConfig { get; set; } = new();
    public List<NarrativeGoalDto> NarrativeGoals { get; set; } = [];

    /// <summary>TTS voice to use for all segments (e.g. "am_adam", "af_heart").</summary>
    public string VoiceOverride { get; set; } = "am_adam";

    /// <summary>Optional background music track and volume. Null = no background music.</summary>
    public BackgroundMusicConfig? BackgroundMusic { get; set; }

    /// <summary>When true, ducks the original video audio by 85% under the narration.</summary>
    public bool DuckOriginalAudio { get; set; } = true;

    /// <summary>
    /// Duration of the source video in seconds. Passed to the assembler so that
    /// background music loops for the full video length, not just until the last segment.
    /// </summary>
    public double? VideoDurationSeconds { get; set; }
}
