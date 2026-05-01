using System.Text.Json.Serialization;

namespace WaveEngine.Application.DTOs.Script;

public class RewrittenScriptDto
{
    [JsonPropertyName("rewritten_script")]
    public string RewrittenScript { get; set; } = string.Empty;

    [JsonPropertyName("word_count")]
    public int WordCount { get; set; }

    [JsonPropertyName("max_word_count")]
    public int MaxWordCount { get; set; }
}
