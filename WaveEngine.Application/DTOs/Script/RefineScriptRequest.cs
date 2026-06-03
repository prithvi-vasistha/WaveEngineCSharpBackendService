using System.Text.Json.Serialization;
using WaveEngine.Application.DTOs.Script;

namespace WaveEngine.Application.DTOs.Script;

public class RefineScriptRequest
{
    [JsonPropertyName("original_script")]
    public string OriginalScript { get; init; } = string.Empty;

    [JsonPropertyName("user_instruction")]
    public string UserInstruction { get; init; } = string.Empty;

    [JsonPropertyName("target_duration_seconds")]
    public float TargetDurationSeconds { get; init; }

    [JsonPropertyName("ai_config")]
    public AiConfigDto AiConfig { get; init; } = new();

    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; init; } = [];
}
