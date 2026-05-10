using Microsoft.EntityFrameworkCore;
using WaveEngine.Domain.Entities;

namespace WaveEngine.Infrastructure.Persistence;

public class WaveEngineDbContext : DbContext
{
    public WaveEngineDbContext(DbContextOptions<WaveEngineDbContext> options)
        : base(options) { }

    public DbSet<ProcessingJob> Jobs => Set<ProcessingJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcessingJob>(entity =>
        {
            entity.HasKey(j => j.JobId);

            // Store the enum as a readable string rather than an integer
            entity.Property(j => j.Status)
                  .HasConversion<string>()
                  .HasMaxLength(32);

            entity.Property(j => j.StatusMessage).HasMaxLength(512);
            entity.Property(j => j.ErrorMessage).HasMaxLength(2048);
        });
    }
}
