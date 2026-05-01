namespace WaveEngine.Domain.Entities;

public class NarrationScript
{
    public string ProjectId { get; set; } = string.Empty;
    public string GeneratedAt { get; set; } = string.Empty;
    public List<Segment> Segments { get; set; } = [];
    public AssemblyInstructions AssemblyInstructions { get; set; } = new();
}
