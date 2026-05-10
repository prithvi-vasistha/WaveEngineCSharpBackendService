using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using WaveEngine.Infrastructure;
using WaveEngine.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// ── Large-file upload limits (video ingestion up to 500 MB) ────────────────
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = 524_288_000; // 500 MB
});

builder.Services.Configure<FormOptions>(form =>
{
    form.MultipartBodyLengthLimit = 524_288_000; // 500 MB
});
// ───────────────────────────────────────────────────────────────────────────

// ── CORS — allow the WaveEngineUI dev server and any local origin ──────────
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.SetIsOriginAllowed(_ => true)   // dev-only: allow all origins
              .AllowAnyHeader()
              .AllowAnyMethod());
});
// ───────────────────────────────────────────────────────────────────────────

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        // Accept and produce snake_case JSON to match the Python services
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

// ── Ensure SQLite database and schema exist ─────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WaveEngineDbContext>();
    db.Database.EnsureCreated();
}
// ───────────────────────────────────────────────────────────────────────────

// Global exception handler
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var logger = context.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("GlobalExceptionHandler");

        var feature = context.Features.Get<IExceptionHandlerFeature>();
        if (feature?.Error is { } ex)
        {
            logger.LogError(ex,
                "UNHANDLED EXCEPTION on {Method} {Path}",
                context.Request.Method, context.Request.Path);
        }

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            error = feature?.Error?.Message ?? "An unexpected error occurred.",
            type  = feature?.Error?.GetType().Name,
        });
    });
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapControllers();

app.Run();
