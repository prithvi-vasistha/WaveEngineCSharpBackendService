using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using WaveEngine.Application.DTOs.Audio;
using WaveEngine.Application.DTOs.Jobs;
using WaveEngine.Application.Interfaces;
using WaveEngine.Application.Models;

namespace WaveEngine.API.Controllers;

[ApiController]
[Route("api/jobs")]
public class JobsController : ControllerBase
{
    private static readonly JsonSerializerOptions _sseOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions _formOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly IJobStore _store;
    private readonly IJobOrchestrator _orchestrator;
    private readonly ISegmentWorkflowService _segmentWorkflow;
    private readonly IAudioAssemblyService _assemblyService;
    private readonly IVideoCompilationService _videoService;
    private readonly ILogger<JobsController> _log;

    public JobsController(
        IJobStore store,
        IJobOrchestrator orchestrator,
        ISegmentWorkflowService segmentWorkflow,
        IAudioAssemblyService assemblyService,
        IVideoCompilationService videoService,
        ILogger<JobsController> log)
    {
        _store           = store;
        _orchestrator    = orchestrator;
        _segmentWorkflow = segmentWorkflow;
        _assemblyService = assemblyService;
        _videoService    = videoService;
        _log             = log;
    }

    // ── POST /api/jobs ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates and immediately starts an orchestration job.
    /// Accepts multipart/form-data: video file + JSON "jobRequest" field.
    /// Returns a job ID the client can use to stream progress.
    /// </summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(524_288_000)]
    [ProducesResponseType(typeof(JobCreatedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateJobAsync(
        IFormFile video,
        [FromForm] string jobRequest,
        CancellationToken ct)
    {
        if (video is null || video.Length == 0)
            return BadRequest(new { error = "A non-empty video file is required." });

        if (string.IsNullOrWhiteSpace(jobRequest))
            return BadRequest(new { error = "The 'jobRequest' form field is required." });

        CreateJobRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<CreateJobRequest>(jobRequest, _formOptions);
        }
        catch (JsonException ex)
        {
            return BadRequest(new { error = $"jobRequest JSON is invalid: {ex.Message}" });
        }

        if (request is null)
            return BadRequest(new { error = "jobRequest deserialized to null." });

        if (string.IsNullOrWhiteSpace(request.AiConfig?.Provider))
            return BadRequest(new { error = "ai_config.provider is required." });

        if (string.IsNullOrWhiteSpace(request.AiConfig?.ApiKey))
            return BadRequest(new { error = "ai_config.api_key is required." });

        if (request.NarrativeGoals.Count == 0 || request.NarrativeGoals.All(g => string.IsNullOrWhiteSpace(g.Gist)))
            return BadRequest(new { error = "At least one narrative_goal with a non-empty gist is required." });

        var videoExt = Path.GetExtension(video.FileName).ToLowerInvariant();
        if (string.IsNullOrEmpty(videoExt)) videoExt = ".mp4";

        var jobId = Guid.NewGuid().ToString("N");

        _log.LogInformation(
            "POST /api/jobs — id={Id} provider={Provider} model={Model} goals={Goals}",
            jobId, request.AiConfig.Provider, request.AiConfig.Model,
            request.NarrativeGoals.Count);

        var job = await _store.CreateJobAsync(jobId, request, videoExt);

        await using (var stream = video.OpenReadStream())
            await _store.SaveVideoAsync(jobId, stream, videoExt, ct);

        _orchestrator.StartJob(job);

        _log.LogInformation("POST /api/jobs — job {Id} queued.", jobId);

        return Accepted(new JobCreatedResponse { JobId = jobId, CreatedAt = job.CreatedAt });
    }

    // ── GET /api/jobs/{jobId} ─────────────────────────────────────────────────

    /// <summary>Returns the current status snapshot for a job.</summary>
    [HttpGet("{jobId}")]
    [ProducesResponseType(typeof(JobStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobAsync(string jobId)
    {
        var job = await _store.GetJobAsync(jobId);
        return job is null
            ? NotFound(new { error = $"Job '{jobId}' not found." })
            : Ok(JobStatusResponse.From(job));
    }

    // ── GET /api/jobs ─────────────────────────────────────────────────────────

    /// <summary>Returns the 20 most recent jobs (for a simple job history list).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<JobStatusResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListJobsAsync()
    {
        var jobs = await _store.GetRecentJobsAsync(20);
        return Ok(jobs.Select(JobStatusResponse.From));
    }

    // ── GET /api/jobs/{jobId}/stream ──────────────────────────────────────────

    /// <summary>
    /// Server-Sent Events stream for a job.
    /// If the job is already complete/failed the terminal event is sent immediately
    /// and the connection is closed.
    /// </summary>
    [HttpGet("{jobId}/stream")]
    public async Task StreamJobEventsAsync(string jobId, CancellationToken ct)
    {
        var job = await _store.GetJobAsync(jobId);
        if (job is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"]    = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no"; // Nginx: disable proxy buffering

        // If the job already reached a terminal state, emit the final event and close.
        if (job.Status == JobStatus.Complete && job.ResultToken is not null)
        {
            await WriteSseAsync(JobEventDto.JobComplete(job.ResultToken), ct);
            return;
        }

        if (job.Status == JobStatus.Failed)
        {
            await WriteSseAsync(JobEventDto.JobError(job.ErrorMessage ?? "Unknown error"), ct);
            return;
        }

        var reader = _store.GetEventReader(jobId);

        if (reader is null)
        {
            // Channel was completed before the client connected.
            // Re-read job state which may have been updated.
            job = await _store.GetJobAsync(jobId);
            if (job?.Status == JobStatus.Complete && job.ResultToken is not null)
                await WriteSseAsync(JobEventDto.JobComplete(job.ResultToken), ct);
            else if (job?.Status == JobStatus.Failed)
                await WriteSseAsync(JobEventDto.JobError(job.ErrorMessage ?? "Unknown error"), ct);
            return;
        }

        try
        {
            await foreach (var @event in reader.ReadAllAsync(ct))
            {
                await WriteSseAsync(@event, ct);

                if (@event.Type is "job_complete" or "job_error")
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal, no action needed.
        }
    }

    // ── GET /api/jobs/result/{token} ──────────────────────────────────────────

    /// <summary>
    /// Streams the compiled MP4 for a given result token.
    /// Supports HTTP range requests so the browser video player can seek.
    /// </summary>
    [HttpGet("result/{token}")]
    public async Task<IActionResult> GetResultAsync(string token)
    {
        var result = await _store.OpenResultAsync(token);
        if (result is null)
            return NotFound(new { error = $"Result token '{token}' not found or expired." });

        var (stream, filename) = result.Value;

        return new FileStreamResult(stream, "video/mp4")
        {
            FileDownloadName      = filename,
            EnableRangeProcessing = true,
        };
    }

    // ── POST /api/jobs/{jobId}/segments/{segmentId}/rerun ─────────────────────

    /// <summary>
    /// Re-synthesises a single segment with a (possibly edited) script.
    /// Called from the review UI when the user manually corrects a script.
    /// Returns a SynthesizeSegmentResponse so the UI can show the new duration.
    /// </summary>
    [HttpPost("{jobId}/segments/{segmentId}/rerun")]
    [ProducesResponseType(typeof(SynthesizeSegmentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RerunSegmentAsync(
        string jobId,
        string segmentId,
        [FromBody] RerunSegmentRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Script))
            return BadRequest(new { error = "script is required." });

        var job = await _store.GetJobAsync(jobId);
        if (job is null) return NotFound(new { error = $"Job '{jobId}' not found." });
        if (job.Status != JobStatus.Complete)
            return BadRequest(new { error = "The job must be in Complete status to rerun a segment." });

        _log.LogInformation(
            "POST /api/jobs/{JobId}/segments/{SegId}/rerun — words={Words}",
            jobId, segmentId,
            request.Script.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

        try
        {
            var result = await _orchestrator.RerunSegmentAsync(
                jobId, segmentId, request.Script, request.VoiceOverride, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    // ── POST /api/jobs/{jobId}/recompile ──────────────────────────────────────

    /// <summary>
    /// Re-assembles and re-compiles the job using the current saved segment WAVs.
    /// Returns a new result token.
    /// </summary>
    [HttpPost("{jobId}/recompile")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RecompileAsync(string jobId, CancellationToken ct)
    {
        var job = await _store.GetJobAsync(jobId);
        if (job is null) return NotFound(new { error = $"Job '{jobId}' not found." });
        if (job.Status != JobStatus.Complete)
            return BadRequest(new { error = "The job must be in Complete status to recompile." });

        _log.LogInformation("POST /api/jobs/{JobId}/recompile", jobId);

        try
        {
            var token = await _orchestrator.RecompileAsync(jobId, ct);
            return Accepted(new { result_token = token });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Recompile failed for job {JobId}", jobId);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = ex.Message });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task WriteSseAsync(JobEventDto @event, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(@event, _sseOptions);
        await Response.WriteAsync($"data: {json}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}

/// <summary>Body of POST …/segments/{segmentId}/rerun</summary>
public class RerunSegmentRequest
{
    /// <summary>The (possibly edited) script text to synthesize.</summary>
    public string Script { get; set; } = string.Empty;

    /// <summary>Optional TTS voice override. Falls back to the job's configured voice.</summary>
    public string? VoiceOverride { get; set; }
}
