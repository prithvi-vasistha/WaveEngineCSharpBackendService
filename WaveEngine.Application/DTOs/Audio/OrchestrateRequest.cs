using WaveEngine.Application.DTOs.Script;
using WaveEngine.Domain.Entities;

namespace WaveEngine.Application.DTOs.Audio;

public class OrchestrateRequest
{
    /// <summary>The narration script produced by /generate-script.</summary>
    public NarrationScript Script { get; set; } = new();

    /// <summary>
    /// The LLM config to use if a segment needs to be rewritten shorter.
    /// Must match the provider used to generate the script.
    /// </summary>
    public AiConfigDto AiConfig { get; set; } = new();
}
