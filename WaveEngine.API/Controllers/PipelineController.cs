using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using WaveEngine.Application.DTOs.Pipeline;
using WaveEngine.Application.Interfaces;

namespace WaveEngine.API.Controllers;

/// <summary>
/// Unified end-to-end pipeline endpoint.
///
/// POST /api/pipeline/execute  (multipart/form-data)
///   video           — source video file (mp4, mov, mkv, …) — up to 500 MB
///   pipelineRequest — JSON string containing ExecutePipelineRequest
///
/// Runs the full pipeline:
///   1. Script generation  (LLM)
///   2. Per-segment TTS synthesis with normalization + LLM-rewrite retry loop
///   3. Audio assembly     (FFmpeg)
///   4. Video compilation  (FFmpeg — mux video with assembled narration)
///
/// Returns the compiled video as a streaming video/mp4 response.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class PipelineController : ControllerBase
{
    private readonly IPipelineOrchestrationService _pipelineService;
    private readonly ILogger<PipelineController> _log;

    private static readonly JsonSerializerOptions _snakeCaseOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    public PipelineController(
        IPipelineOrchestrationService pipelineService,
        ILogger<PipelineController> log)
    {
        _pipelineService = pipelineService;
        _log             = log;
    }

    /// <summary>
    /// Executes the full voice-over pipeline and streams back the compiled MP4.
    /// </summary>
    [HttpPost("execute")]
    [Consumes("multipart/form-data")]
    [Produces("video/mp4")]
    [RequestSizeLimit(524_288_000)] // 500 MB — matches Kestrel limit in Program.cs
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> ExecuteAsync(
        IFormFile video,
        [FromForm] string pipelineRequest,
        CancellationToken ct)
    {
        // ── Input validation ─────────────────────────────────────────────────
        if (video is null || video.Length == 0)
            return BadRequest(new { error = "A non-empty video file is required." });

        if (string.IsNullOrWhiteSpace(pipelineRequest))
            return BadRequest(new { error = "pipelineRequest JSON is required." });

        ExecutePipelineRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ExecutePipelineRequest>(
                pipelineRequest, _snakeCaseOptions);
        }
        catch (JsonException ex)
        {
            _log.LogWarning("POST /pipeline/execute — pipelineRequest JSON parse error: {Msg}", ex.Message);
            return BadRequest(new { error = $"pipelineRequest is not valid JSON: {ex.Message}" });
        }

        if (request?.ScriptRequest is null)
            return BadRequest(new { error = "pipelineRequest.script_request is required." });

        if (string.IsNullOrWhiteSpace(request.ScriptRequest.AiConfig?.Provider))
            return BadRequest(new
            {
                error = "script_request.ai_config.provider is required. " +
                        "Must be one of: claude, openai, gemini, google, mistral.",
            });

        if (string.IsNullOrWhiteSpace(request.ScriptRequest.AiConfig?.ApiKey))
            return BadRequest(new { error = "script_request.ai_config.api_key is required." });

        _log.LogInformation(
            "POST /pipeline/execute — project='{Name}' provider='{Provider}' model='{Model}' " +
            "videoSize={Size} duck={Duck}",
            request.ScriptRequest.ProjectName,
            request.ScriptRequest.AiConfig.Provider,
            request.ScriptRequest.AiConfig.Model,
            video.Length,
            request.DuckOriginalAudio);

        // ── Execute pipeline ─────────────────────────────────────────────────
        try
        {
            await using var videoStream = video.OpenReadStream();

            var output = await _pipelineService.ExecuteAsync(
                videoStream, video.FileName, request, ct);

            _log.LogInformation(
                "POST /pipeline/execute — pipeline complete, streaming {Path}", output.VideoPath);

            // Stream MP4 back; DeferredDeleteFileStream cleans up the workspace after send.
            var stream = new DeferredDeleteFileStream(output.VideoPath, output.WorkspaceDirectory);

            return new FileStreamResult(stream, "video/mp4")
            {
                FileDownloadName      = $"{output.ProjectId}_final.mp4",
                EnableRangeProcessing = true,
            };
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            _log.LogError(ex, "POST /pipeline/execute — upstream HTTP {Status}", (int)ex.StatusCode.Value);
            return StatusCode((int)ex.StatusCode.Value, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "POST /pipeline/execute — UNHANDLED {Type}: {Message}",
                ex.GetType().Name, ex.Message);
            throw;
        }
    }
}
