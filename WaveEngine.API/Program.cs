using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using WaveEngine.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// ── Large-file upload limits (video ingestion up to 500 MB) ────────────────
// Kestrel: controls the raw HTTP request body limit
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = 524_288_000; // 500 MB
});

// FormOptions: controls the multipart body parser limit (must match Kestrel)
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
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

// Global exception handler — logs every unhandled exception to the console before returning 500
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

// Lightweight liveness probe used by Docker healthcheck and load balancers
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapControllers();

app.Run();
