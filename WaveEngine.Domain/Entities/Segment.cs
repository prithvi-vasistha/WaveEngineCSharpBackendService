namespace WaveEngine.Domain.Entities;

public class Segment
{
    public string Id { get; set; } = string.Empty;
    public float StartTimeSeconds { get; set; }
    public float EndTimeSeconds { get; set; }
    public float TargetDuration { get; set; }
    public string Script { get; set; } = string.Empty;
    public TtsConfig TtsConfig { get; set; } = new();
}
