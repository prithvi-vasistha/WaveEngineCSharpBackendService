namespace WaveEngine.Domain.Entities;

public class TtsConfig
{
    public string Voice { get; set; } = string.Empty;
    public float Speed { get; set; }
    public string Emotion { get; set; } = string.Empty;
}
