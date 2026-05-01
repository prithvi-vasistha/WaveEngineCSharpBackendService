using System.Text.Json.Serialization;

namespace WaveEngine.Application.DTOs.Script;

public class RewriteShorterRequest
{
    [JsonPropertyName("original_script")]
    public string OriginalScript { get; set; } = string.Empty;

    [JsonPropertyName("target_duration_seconds")]
    public double TargetDurationSeconds { get; set; }

    [JsonPropertyName("ai_config")]
    public AiConfigDto AiConfig { get; set; } = new();

    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = [];
}
