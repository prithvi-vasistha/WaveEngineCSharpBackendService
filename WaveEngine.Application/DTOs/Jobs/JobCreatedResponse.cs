namespace WaveEngine.Application.DTOs.Jobs;

public class JobCreatedResponse
{
    public string JobId { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}
