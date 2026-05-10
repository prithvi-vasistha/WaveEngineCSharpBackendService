using WaveEngine.Domain.Entities;

namespace WaveEngine.Application.Interfaces;

public interface IJobRepository
{
    Task<ProcessingJob> CreateAsync(ProcessingJob job, CancellationToken ct = default);
    Task<ProcessingJob?> GetAsync(Guid jobId, CancellationToken ct = default);
    Task UpdateAsync(ProcessingJob job, CancellationToken ct = default);
}
