using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using WaveEngine.Application.DTOs.Jobs;
using WaveEngine.Application.DTOs.Pipeline;
using WaveEngine.Application.Interfaces;
using WaveEngine.Domain.Entities;
using WaveEngine.Infrastructure.Persistence;

namespace WaveEngine.API.Controllers;

/// <summary>
/// Async pipeline controller.
///
/// POST /api/pipeline/execute  (multipart/form-data)
///   video           — source video file (mp4, mov, mkv, …) up to 500 MB
///   pipelineRequest — JSON string containing ExecutePipelineRequest
///   → 202 Accepted  { job_id: "…" }
///
/// GET /api/pipeline/status/{jobId}
///   → 200 JobStatusResponse   (poll until status == "Completed" or "Failed")
///
/// GET /api/pipeline/video/{jobId}
///   → 200 video/mp4 stream   (only when status == "Completed")
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class PipelineController : ControllerBase
{
    private readonly IJobRepository _jobRepository;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PipelineController> _log;

    private static readonly JsonSerializerOptions _snakeCaseOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    public PipelineController(
        IJobRepository jobRepository,
        IServiceScopeFactory scopeFactory,
        ILogger<PipelineController> log)
    {
        _jobRepository = jobRepository;
        _scopeFactory  = scopeFactory;
        _log           = log;
    }

    // ── POST /api/pipeline/execute ────────────────────────────────────────────

    [HttpPost("execute")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(524_288_000)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
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
            return BadRequest(new { error = $"pipelineRequest is not valid JSON: {ex.Message}" });
        }

        if (request?.ScriptRequest is null)
            return BadRequest(new { error = "pipelineRequest.script_request is required." });

        if (string.IsNullOrWhiteSpace(request.ScriptRequest.AiConfig?.Provider))
            return BadRequest(new { error = "script_request.ai_config.provider is required." });

        if (string.IsNullOrWhiteSpace(request.ScriptRequest.AiConfig?.ApiKey))
            return BadRequest(new { error = "script_request.ai_config.api_key is required." });

        // ── Buffer the upload to disk ────────────────────────────────────────
        // The HTTP request stream is disposed when this action returns.
        // We must save the video to a temp file before firing the background task.
        var videoExt      = Path.GetExtension(video.FileName).ToLowerInvariant();
        if (string.IsNullOrEmpty(videoExt)) videoExt = ".mp4";
        var tempVideoPath = Path.Combine(Path.GetTempPath(), $"waveengine_upload_{Guid.NewGuid():N}{videoExt}");

        try
        {
            await using (var fs = System.IO.File.Create(tempVideoPath))
                await video.CopyToAsync(fs, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to buffer uploaded video to {Path}", tempVideoPath);
            return StatusCode(500, new { error = "Failed to buffer the uploaded video." });
        }

        // ── Create job record ────────────────────────────────────────────────
        var job = new ProcessingJob
        {
            Status        = JobStatus.Created,
            StatusMessage = "Job queued — pipeline starting…",
        };
        await _jobRepository.CreateAsync(job, ct);

        _log.LogInformation(
            "Pipeline job {JobId} created — project='{Name}' provider='{Provider}'",
            job.JobId, request.ScriptRequest.ProjectName,
            request.ScriptRequest.AiConfig.Provider);

        // ── Fire background task ─────────────────────────────────────────────
        var jobId        = job.JobId;
        var videoFileName = video.FileName;
        _ = Task.Run(() => RunPipelineAsync(jobId, tempVideoPath, videoFileName, request));

        return Accepted(new { job_id = jobId });
    }

    // ── GET /api/pipeline/status/{jobId} ─────────────────────────────────────

    [HttpGet("status/{jobId:guid}")]
    [ProducesResponseType(typeof(JobStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStatusAsync(Guid jobId, CancellationToken ct)
    {
        var job = await _jobRepository.GetAsync(jobId, ct);
        if (job is null) return NotFound(new { error = $"Job {jobId} not found." });

        JsonElement? masterPlan = null;
        if (job.Status == JobStatus.Completed && !string.IsNullOrEmpty(job.ResultData))
        {
            try { masterPlan = JsonDocument.Parse(job.ResultData).RootElement; }
            catch { /* malformed JSON — surface as null */ }
        }

        var response = new JobStatusResponse
        {
            JobId              = job.JobId,
            Status             = job.Status.ToString(),
            ProgressPercentage = job.ProgressPercentage,
            StatusMessage      = job.StatusMessage,
            VideoUrl           = job.Status == JobStatus.Completed
                                     ? $"/api/pipeline/video/{job.JobId}"
                                     : null,
            MasterPlan         = masterPlan,
            ErrorMessage       = job.ErrorMessage,
        };

        return Ok(response);
    }

    // ── GET /api/pipeline/video/{jobId} ──────────────────────────────────────

    [HttpGet("video/{jobId:guid}")]
    [HttpHead("video/{jobId:guid}")]
    [Produces("video/mp4")]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetVideoAsync(Guid jobId, CancellationToken ct)
    {
        var job = await _jobRepository.GetAsync(jobId, ct);
        if (job is null) return NotFound(new { error = $"Job {jobId} not found." });

        if (job.Status != JobStatus.Completed)
            return Conflict(new { error = $"Job is not completed yet. Current status: {job.Status}." });

        if (string.IsNullOrEmpty(job.VideoPath) || !System.IO.File.Exists(job.VideoPath))
            return NotFound(new { error = "Video file not found on disk." });

        // HEAD: return metadata headers only so Vidstack can probe the stream.
        if (HttpMethods.IsHead(Request.Method))
        {
            var info = new FileInfo(job.VideoPath);
            Response.Headers.ContentType   = "video/mp4";
            Response.Headers.AcceptRanges  = "bytes";
            Response.Headers.ContentLength = info.Length;
            return Ok();
        }

        // GET: stream the file. No FileDownloadName → Content-Disposition stays inline
        // so the browser's video element can load it without treating it as an attachment.
        var stream = new FileStream(
            job.VideoPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81_920, useAsync: true);

        return new FileStreamResult(stream, "video/mp4")
        {
            EnableRangeProcessing = true,
        };
    }

    // ── Background pipeline worker ────────────────────────────────────────────

    private async Task RunPipelineAsync(
        Guid jobId,
        string tempVideoPath,
        string videoFileName,
        ExecutePipelineRequest request)
    {
        await using var scope    = _scopeFactory.CreateAsyncScope();
        var jobRepo   = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var pipeline  = scope.ServiceProvider.GetRequiredService<IPipelineOrchestrationService>();

        // Re-fetch so EF Core tracks this instance in the background scope
        var job = await jobRepo.GetAsync(jobId);
        if (job is null)
        {
            _log.LogError("Background task: job {JobId} disappeared from DB before pipeline start.", jobId);
            return;
        }

        try
        {
            // Progress callback — fires at each milestone inside PipelineOrchestrationService
            var progress = new Progress<JobProgressUpdate>(update =>
            {
                // Fire-and-forget DB update per milestone.
                // Each update opens its own scope to avoid EF tracking conflicts.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await using var updateScope = _scopeFactory.CreateAsyncScope();
                        var updateRepo = updateScope.ServiceProvider.GetRequiredService<IJobRepository>();
                        var latestJob  = await updateRepo.GetAsync(jobId);
                        if (latestJob is null) return;

                        latestJob.Status             = update.Status;
                        latestJob.ProgressPercentage = update.Percentage;
                        latestJob.StatusMessage      = update.Message;
                        await updateRepo.UpdateAsync(latestJob);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Progress DB update failed for job {JobId} (non-fatal).", jobId);
                    }
                });
            });

            await using var videoStream = System.IO.File.OpenRead(tempVideoPath);

            var output = await pipeline.ExecuteAsync(
                videoStream, videoFileName, request, progress);

            // Persist the result
            var scriptJson = JsonSerializer.Serialize(output.Script, _snakeCaseOptions);

            job.Status             = JobStatus.Completed;
            job.ProgressPercentage = 100;
            job.StatusMessage      = "Your video is ready!";
            job.ResultData         = scriptJson;
            job.VideoPath          = output.VideoPath;
            job.WorkspaceDirectory = output.WorkspaceDirectory;
            await jobRepo.UpdateAsync(job);

            _log.LogInformation(
                "Pipeline job {JobId} completed — video={Path}", jobId, output.VideoPath);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Pipeline job {JobId} failed.", jobId);

            try
            {
                job.Status        = JobStatus.Failed;
                job.StatusMessage = "Pipeline failed.";
                job.ErrorMessage  = ex.Message;
                await jobRepo.UpdateAsync(job);
            }
            catch (Exception dbEx)
            {
                _log.LogError(dbEx, "Failed to persist failure status for job {JobId}.", jobId);
            }
        }
        finally
        {
            // Always clean up the buffered upload temp file
            try { System.IO.File.Delete(tempVideoPath); } catch { /* best-effort */ }
        }
    }
}
