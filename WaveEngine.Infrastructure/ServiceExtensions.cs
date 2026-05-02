using FFMpegCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WaveEngine.Application.Interfaces;
using WaveEngine.Infrastructure.HttpClients;
using WaveEngine.Infrastructure.Services;

namespace WaveEngine.Infrastructure;

public static class ServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
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

        services.AddSingleton<IAudioNormalizationService, AudioNormalizationService>();
        services.AddSingleton<IAudioAssemblyService, AudioAssemblyService>();
        services.AddSingleton<IVideoCompilationService, VideoCompilationService>();
        services.AddScoped<ITtsOrchestrationService, TtsOrchestrationService>();

        // Configure FFMpegCore binary path (override via config for non-PATH installs)
        var ffmpegPath = configuration["FFmpeg:BinaryFolder"];
        if (!string.IsNullOrWhiteSpace(ffmpegPath))
            GlobalFFOptions.Configure(opts => opts.BinaryFolder = ffmpegPath);

        return services;
    }
}
