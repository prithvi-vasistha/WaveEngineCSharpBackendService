using System.Text.Json.Serialization;

namespace WaveEngine.Application.DTOs.Script;

public class GenerateScriptRequest
{
    [JsonPropertyName("project_name")]
    public string ProjectName { get; set; } = string.Empty;

    [JsonPropertyName("video_url")]
    public string? VideoUrl { get; set; }

    [JsonPropertyName("global_context")]
    public string GlobalContext { get; set; } = string.Empty;

    [JsonPropertyName("ai_config")]
    public AiConfigDto AiConfig { get; set; } = new();

    [JsonPropertyName("narrative_goals")]
    public List<NarrativeGoalDto> NarrativeGoals { get; set; } = [];
}
