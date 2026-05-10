using WaveEngine.Application.DTOs.Audio;
using WaveEngine.Application.DTOs.Script;

namespace WaveEngine.Application.DTOs.Pipeline;

/// <summary>
/// Single request that drives the entire end-to-end pipeline:
/// script generation → synthesis → normalization → audio assembly → video compilation.
/// Sent as a JSON form field alongside the video file to POST /api/pipeline/execute.
/// </summary>
public class ExecutePipelineRequest
{
    /// <summary>
    /// All inputs required to generate the narration script.
    /// Contains project metadata, narrative goals, and the AI config
    /// (which is also reused for LLM rewrite calls during the synthesis loop).
    /// </summary>
    public GenerateScriptRequest ScriptRequest { get; set; } = new();

    /// <summary>
    /// Optional background music to layer under the narration.
    /// Omit for narration-only output.
    /// </summary>
    public BackgroundMusicConfig? BackgroundMusic { get; set; }

    /// <summary>
    /// When set, overrides the TTS voice for every segment.
    /// Omit to use each segment's LLM-chosen voice.
    /// </summary>
    public string? VoiceOverride { get; set; }

    /// <summary>
    /// When true, the original video audio is ducked to 15% and mixed with the narration.
    /// Default: true.
    /// </summary>
    public bool DuckOriginalAudio { get; set; } = true;
}
