using System.Text.Json.Serialization;

namespace WaveEngine.Application.DTOs.Synthesize;

public class SynthesizeRequest
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "output.wav";

    [JsonPropertyName("voice")]
    public string Voice { get; set; } = "af_heart";

    [JsonPropertyName("speed")]
    public float Speed { get; set; } = 1.0f;

    [JsonPropertyName("lang_code")]
    public string LangCode { get; set; } = "a";
}
