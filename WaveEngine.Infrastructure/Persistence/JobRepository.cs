using WaveEngine.Application.Interfaces;
using WaveEngine.Domain.Entities;

namespace WaveEngine.Infrastructure.Persistence;

public class JobRepository : IJobRepository
{
    private readonly WaveEngineDbContext _db;

    public JobRepository(WaveEngineDbContext db) => _db = db;

    public async Task<ProcessingJob> CreateAsync(ProcessingJob job, CancellationToken ct = default)
    {
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync(ct);
        return job;
    }

    public async Task<ProcessingJob?> GetAsync(Guid jobId, CancellationToken ct = default)
        => await _db.Jobs.FindAsync([jobId], ct);

    public async Task UpdateAsync(ProcessingJob job, CancellationToken ct = default)
    {
        job.UpdatedAt = DateTimeOffset.UtcNow;
        _db.Jobs.Update(job);
        await _db.SaveChangesAsync(ct);
    }
}
