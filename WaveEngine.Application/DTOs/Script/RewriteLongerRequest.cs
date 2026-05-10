using System.Text.Json.Serialization;

namespace WaveEngine.Application.DTOs.Script;

/// <summary>
/// Request to expand a script that is too short for its target duration (CASE B).
/// Sent to the Python /rewrite-longer endpoint.
/// </summary>
public class RewriteLongerRequest
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
