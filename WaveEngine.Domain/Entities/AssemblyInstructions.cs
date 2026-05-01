namespace WaveEngine.Domain.Entities;

public class AssemblyInstructions
{
    public bool BackgroundMusicDucking { get; set; }
    public string OutputFormat { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;
    public string? Notes { get; set; }
}
