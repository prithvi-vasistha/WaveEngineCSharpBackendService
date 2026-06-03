using FFMpegCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WaveEngine.Application.Interfaces;
using WaveEngine.Application.Settings;
using WaveEngine.Infrastructure.HttpClients;
using WaveEngine.Infrastructure.Persistence;
using WaveEngine.Infrastructure.Services;

namespace WaveEngine.Infrastructure;

public static class ServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── SQLite job-persistence store ──────────────────────────────────────
        services.AddDbContext<WaveEngineDbContext>(options =>
            options.UseSqlite(
                configuration.GetConnectionString("DefaultConnection")
                ?? "Data Source=waveengine_jobs.db"));

        services.AddScoped<IJobRepository, JobRepository>();

        // ── Strongly-typed orchestration settings ─────────────────────────────
        services.Configure<OrchestrationSettings>(
            configuration.GetSection("OrchestrationSettings"));

        // ── HTTP clients for Python microservices ─────────────────────────────
        services.AddHttpClient<IScriptGenerationService, ScriptGenerationClient>(client =>
        {
            client.BaseAddress = new Uri(configuration["Services:ScriptService:BaseUrl"]
                ?? throw new InvalidOperationException("Services:ScriptService:BaseUrl is not configured."));

            client.Timeout = TimeSpan.FromSeconds(120);
        });

        services.AddHttpClient<ITtsSynthesisService, TtsSynthesisClient>(client =>
        {
            client.BaseAddress = new Uri(configuration["Services:TtsService:BaseUrl"]
                ?? throw new InvalidOperationException("Services:TtsService:BaseUrl is not configured."));

            client.Timeout = TimeSpan.FromSeconds(120);
        });

        // ── Infrastructure services ───────────────────────────────────────────
        services.AddSingleton<IAudioNormalizationService, AudioNormalizationService>();
        services.AddSingleton<IAudioAssemblyService, AudioAssemblyService>();
        services.AddSingleton<IVideoCompilationService, VideoCompilationService>();
        services.AddScoped<ITtsOrchestrationService, TtsOrchestrationService>();
        services.AddScoped<IPipelineOrchestrationService, PipelineOrchestrationService>();
        services.AddScoped<ISegmentRegenerationService, SegmentRegenerationService>();
        services.AddScoped<ISegmentMoveService, SegmentMoveService>();

        // ── FFMpegCore binary path ────────────────────────────────────────────
        var ffmpegPath = configuration["FFmpeg:BinaryFolder"];
        if (!string.IsNullOrWhiteSpace(ffmpegPath))
            GlobalFFOptions.Configure(opts => opts.BinaryFolder = ffmpegPath);

        return services;
    }
}
