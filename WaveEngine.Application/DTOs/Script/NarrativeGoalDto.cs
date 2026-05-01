using System.Text.Json.Serialization;

namespace WaveEngine.Application.DTOs.Script;

public class NarrativeGoalDto
{
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("gist")]
    public string Gist { get; set; } = string.Empty;

    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = [];

    [JsonPropertyName("max_duration")]
    public float? MaxDuration { get; set; }
}
