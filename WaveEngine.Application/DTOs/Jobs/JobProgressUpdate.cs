using WaveEngine.Domain.Entities;

namespace WaveEngine.Application.DTOs.Jobs;

/// <summary>
/// Fired by PipelineOrchestrationService at each pipeline milestone.
/// The PipelineController background task uses this to update the job record.
/// </summary>
public record JobProgressUpdate(
    int Percentage,
    string Message,
    JobStatus Status);
