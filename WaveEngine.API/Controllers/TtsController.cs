using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using WaveEngine.Application.DTOs.Audio;
using WaveEngine.Application.DTOs.Script;
using WaveEngine.Application.DTOs.Synthesize;
using WaveEngine.Application.Interfaces;
using WaveEngine.Domain.Entities;

namespace WaveEngine.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TtsController : ControllerBase
{
    private readonly IScriptGenerationService _scriptService;
    private readonly ITtsSynthesisService _synthesisService;
    private readonly ITtsOrchestrationService _orchestrationService;
    private readonly IVideoCompilationService _videoCompilationService;
    private readonly ILogger<TtsController> _log;

    // Reuse one instance for manual deserialization (compile-video form field)
    private static readonly JsonSerializerOptions _snakeCaseOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    public TtsController(
        IScriptGenerationService scriptService,
        ITtsSynthesisService synthesisService,
        ITtsOrchestrationService orchestrationService,
        IVideoCompilationService videoCompilationService,
        ILogger<TtsController> log)
    {
        _scriptService           = scriptService;
        _synthesisService        = synthesisService;
        _orchestrationService    = orchestrationService;
        _videoCompilationService = videoCompilationService;
        _log                     = log;
    }

    /// <summary>
    /// Generates a structured narration script via the LLM script service.
    /// </summary>
    [HttpPost("generate-script")]
    [ProducesResponseType(typeof(NarrationScript), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetMasterPlanAsync(
        [FromBody] GenerateScriptRequest request,
        CancellationToken ct)
    {
        _log.LogInformation("POST /generate-script — provider={Provider} model={Model}",
            request.AiConfig?.Provider, request.AiConfig?.Model);
        try
        {
            var result = await _scriptService.GetMasterPlanAsync(request, ct);
            _log.LogInformation("POST /generate-script — OK, {Count} segments returned.",
                result.Segments?.Count ?? 0);
            return Ok(result);
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            _log.LogError(ex, "POST /generate-script — upstream HTTP {Status}", (int)ex.StatusCode.Value);
            return StatusCode((int)ex.StatusCode.Value, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "POST /generate-script — UNHANDLED {Type}: {Message}",
                ex.GetType().Name, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Synthesizes text to speech and returns a WAV file.
    /// </summary>
    [HttpPost("synthesize")]
    [Produces("audio/wav")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GenerateVoiceAsync(
        [FromBody] SynthesizeRequest request,
        CancellationToken ct)
    {
        _log.LogInformation("POST /synthesize — voice={Voice} speed={Speed} filename={File}",
            request.Voice, request.Speed, request.Filename);
        try
        {
            var wavBytes = await _synthesisService.GenerateVoiceAsync(request, ct);

            var filename = request.Filename.EndsWith(".wav")
                ? request.Filename
                : request.Filename + ".wav";

            _log.LogInformation("POST /synthesize — OK, {Bytes} bytes returned.", wavBytes.Length);
            return File(wavBytes, "audio/wav", filename);
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            _log.LogError(ex, "POST /synthesize — upstream HTTP {Status}", (int)ex.StatusCode.Value);
            return StatusCode((int)ex.StatusCode.Value, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "POST /synthesize — UNHANDLED {Type}: {Message}",
                ex.GetType().Name, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Decodes a Base64-encoded WAV from an OrchestrationResult and returns it as a playable audio/wav file.
    /// Use ?target=final_mix for the complete mixed track, or ?target={segmentId} for a single segment.
    /// </summary>
    [HttpPost("decode-audio")]
    [Produces("audio/wav")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult DecodeAudio(
        [FromBody] OrchestrationResult result,
        [FromQuery] string target = "final_mix")
    {
        _log.LogInformation("POST /decode-audio — target='{Target}'", target);

        string base64;
        string filename;

        if (string.Equals(target, "final_mix", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(result.FinalMixWavBase64))
                return BadRequest(new { error = "final_mix_wav_base64 is empty in the supplied payload." });

            base64   = result.FinalMixWavBase64;
            filename = $"{result.Script?.ProjectId ?? "project"}_final_mix.wav";
        }
        else
        {
            var segment = result.SegmentAudio?.FirstOrDefault(
                s => string.Equals(s.SegmentId, target, StringComparison.OrdinalIgnoreCase));

            if (segment is null)
                return NotFound(new
                {
                    error = $"Segment '{target}' not found.",
                    available = result.SegmentAudio?.Select(s => s.SegmentId).ToArray() ?? [],
                });

            if (string.IsNullOrWhiteSpace(segment.WavBase64))
                return BadRequest(new { error = $"wav_base64 is empty for segment '{target}'." });

            base64   = segment.WavBase64;
            filename = $"{target}.wav";
        }

        byte[] wavBytes;
        try
        {
            wavBytes = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            _log.LogError(ex, "POST /decode-audio — Base64 decode failed for target='{Target}'", target);
            return BadRequest(new { error = $"The Base64 value for '{target}' is not valid: {ex.Message}" });
        }

        _log.LogInformation(
            "POST /decode-audio — returning {Bytes} bytes as '{File}'", wavBytes.Length, filename);

        return File(wavBytes, "audio/wav", filename);
    }

    /// <summary>
    /// Full pipeline: generate script → synthesize each segment → normalize duration.
    /// Returns the narration script JSON plus all normalized segment WAVs as Base64.
    /// </summary>
    [HttpPost("orchestrate")]
    [ProducesResponseType(typeof(OrchestrationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> OrchestrateAsync(
        [FromBody] OrchestrateRequest request,
        CancellationToken ct)
    {
        _log.LogInformation(
            "POST /orchestrate — projectId={ProjectId} segments={Count} provider='{Provider}' model='{Model}'",
            request.Script?.ProjectId, request.Script?.Segments?.Count,
            request.AiConfig?.Provider, request.AiConfig?.Model);

        // Validate script.segments up-front — catches the common mistake of sending the
        // NarrationScript fields at the root level instead of nested under "script".
        if (request.Script?.Segments is not { Count: > 0 })
        {
            _log.LogWarning("POST /orchestrate — rejected: script.segments is empty or missing.");
            return BadRequest(new
            {
                error = "script.segments is empty or missing. " +
                        "The NarrationScript must be nested under the \"script\" key: " +
                        "{ \"script\": { \"segments\": [...] }, \"ai_config\": {...} }",
            });
        }

        // Validate ai_config up-front — it's required if any segment needs a rewrite.
        if (string.IsNullOrWhiteSpace(request.AiConfig?.Provider))
        {
            _log.LogWarning("POST /orchestrate — rejected: ai_config.provider is missing or empty.");
            return BadRequest(new
            {
                error = "ai_config.provider is required. Must be one of: claude, openai, gemini, google, mistral.",
            });
        }

        if (string.IsNullOrWhiteSpace(request.AiConfig?.ApiKey))
        {
            _log.LogWarning("POST /orchestrate — rejected: ai_config.api_key is missing or empty.");
            return BadRequest(new { error = "ai_config.api_key is required." });
        }

        try
        {
            var result = await _orchestrationService.OrchestrateAsync(request, segmentProgress: null, ct);
            _log.LogInformation("POST /orchestrate — OK, {Count} segments processed.", result.SegmentAudio?.Count ?? 0);
            return Ok(result);
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            _log.LogError(ex, "POST /orchestrate — upstream HTTP {Status}", (int)ex.StatusCode.Value);
            return StatusCode((int)ex.StatusCode.Value, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "POST /orchestrate — UNHANDLED {Type}: {Message}",
                ex.GetType().Name, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Ingests a video file and an OrchestrationResult (the output of /orchestrate), then
    /// produces a compiled MP4 by muxing the video with the assembled narration track.
    ///
    /// The video's original audio is optionally ducked to 15% volume beneath the narration.
    /// The compiled MP4 is streamed back directly — the temp workspace is deleted after the
    /// response finishes, without buffering the full file in memory.
    ///
    /// Request: multipart/form-data
    ///   video      — the source video file (mp4, mov, mkv, …) — up to 500 MB
    ///   masterPlan — the OrchestrationResult JSON produced by /orchestrate
    ///
    /// Query params:
    ///   duckOriginalAudio (bool, default true) — mix video audio at 15% with narration
    /// </summary>
    [HttpPost("compile-video")]
    [Consumes("multipart/form-data")]
    [Produces("video/mp4")]
    [RequestSizeLimit(524_288_000)] // 500 MB — must also match Kestrel / FormOptions limits in Program.cs
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CompileVideoAsync(
        IFormFile video,
        [FromForm] string masterPlan,
        [FromQuery] bool duckOriginalAudio = true,
        CancellationToken ct = default)
    {
        // ── Input validation ────────────────────────────────────────────────
        if (video is null || video.Length == 0)
            return BadRequest(new { error = "A non-empty video file is required." });

        if (string.IsNullOrWhiteSpace(masterPlan))
            return BadRequest(new { error = "masterPlan JSON is required." });

        OrchestrationResult? plan;
        try
        {
            plan = JsonSerializer.Deserialize<OrchestrationResult>(masterPlan, _snakeCaseOptions);
        }
        catch (JsonException ex)
        {
            _log.LogWarning("POST /compile-video — masterPlan JSON parse error: {Msg}", ex.Message);
            return BadRequest(new { error = $"masterPlan is not valid JSON: {ex.Message}" });
        }

        if (string.IsNullOrWhiteSpace(plan?.FinalMixWavBase64))
            return BadRequest(new
            {
                error = "masterPlan.final_mix_wav_base64 is required and must be non-empty. " +
                        "Run /orchestrate first to produce an OrchestrationResult.",
            });

        // ── Workspace setup ─────────────────────────────────────────────────
        // Workspace is kept alive for the duration of the operation and then
        // transferred to DeferredDeleteFileStream on success (so cleanup happens
        // after streaming completes, not before).
        Workspace? workspace = null;
        try
        {
            workspace = new Workspace();

            var videoExt = Path.GetExtension(video.FileName).ToLowerInvariant();
            if (string.IsNullOrEmpty(videoExt)) videoExt = ".mp4";

            _log.LogInformation(
                "POST /compile-video — workspace={Dir} file='{Name}' size={Size} duck={Duck}",
                workspace.Directory, video.FileName, video.Length, duckOriginalAudio);

            // ── 1. Save the uploaded video stream to disk ───────────────────
            string videoPath;
            await using (var src = video.OpenReadStream())
                videoPath = await workspace.SaveStreamAsync(src, $"input{videoExt}", ct);

            // ── 2. Decode narration WAV and save to disk ────────────────────
            byte[] narrationBytes;
            try
            {
                narrationBytes = Convert.FromBase64String(plan.FinalMixWavBase64);
            }
            catch (FormatException ex)
            {
                return BadRequest(new
                {
                    error = $"final_mix_wav_base64 is not valid Base64: {ex.Message}",
                });
            }

            var narrationWavPath = workspace.GetPath("narration.wav");
            await System.IO.File.WriteAllBytesAsync(narrationWavPath, narrationBytes, ct);

            // ── 3. Determine total narration duration ───────────────────────
            double totalDuration = plan.Script?.Segments?.Count > 0
                ? (double)plan.Script.Segments.Max(s => s.EndTimeSeconds)
                : 0;

            // ── 4. Run FFmpeg to produce the compiled MP4 ───────────────────
            var outputPath = workspace.GetPath("output.mp4");

            await _videoCompilationService.CompileAsync(
                videoPath, narrationWavPath, totalDuration, outputPath, duckOriginalAudio, ct);

            _log.LogInformation(
                "POST /compile-video — FFmpeg done, streaming {Path}", outputPath);

            // ── 5. Stream result — transfer workspace ownership to the stream ─
            // DeferredDeleteFileStream deletes workspace.Directory after the
            // response body has been fully sent, so we must NOT dispose workspace here.
            var stream = new DeferredDeleteFileStream(outputPath, workspace.Directory);
            workspace  = null; // prevent cleanup in finally

            var projectId = plan.Script?.ProjectId ?? "output";
            return new FileStreamResult(stream, "video/mp4")
            {
                FileDownloadName    = $"{projectId}_final.mp4",
                EnableRangeProcessing = true,
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "POST /compile-video — UNHANDLED {Type}: {Message}",
                ex.GetType().Name, ex.Message);
            throw;
        }
        finally
        {
            // Only reached on error paths (workspace is null on success)
            if (workspace is not null)
                await workspace.DisposeAsync();
        }
    }
}
