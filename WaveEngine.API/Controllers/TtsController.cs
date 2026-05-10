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
    private readonly IAudioNormalizationService _normalizationService;
    private readonly IAudioAssemblyService _assemblyService;
    private readonly ITtsOrchestrationService _orchestrationService;
    private readonly IVideoCompilationService _videoCompilationService;
    private readonly ISegmentWorkflowService _segmentWorkflowService;
    private readonly ILogger<TtsController> _log;

    // Reuse one instance for manual deserialization (compile-video form field)
    private static readonly JsonSerializerOptions _snakeCaseOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public TtsController(
        IScriptGenerationService scriptService,
        ITtsSynthesisService synthesisService,
        IAudioNormalizationService normalizationService,
        IAudioAssemblyService assemblyService,
        ITtsOrchestrationService orchestrationService,
        IVideoCompilationService videoCompilationService,
        ISegmentWorkflowService segmentWorkflowService,
        ILogger<TtsController> log)
    {
        _scriptService           = scriptService;
        _synthesisService        = synthesisService;
        _normalizationService    = normalizationService;
        _assemblyService         = assemblyService;
        _orchestrationService    = orchestrationService;
        _videoCompilationService = videoCompilationService;
        _segmentWorkflowService  = segmentWorkflowService;
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
                    error     = $"Segment '{target}' not found.",
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
            var result = await _orchestrationService.OrchestrateAsync(request, ct);
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
    /// Ingests a video file and an OrchestrationResult, then produces a compiled MP4.
    /// </summary>
    [HttpPost("compile-video")]
    [Consumes("multipart/form-data")]
    [Produces("video/mp4")]
    [RequestSizeLimit(524_288_000)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CompileVideoAsync(
        IFormFile video,
        [FromForm] string masterPlan,
        [FromQuery] bool duckOriginalAudio = true,
        CancellationToken ct = default)
    {
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

        Workspace? workspace = null;
        try
        {
            workspace = new Workspace();

            var videoExt = Path.GetExtension(video.FileName).ToLowerInvariant();
            if (string.IsNullOrEmpty(videoExt)) videoExt = ".mp4";

            _log.LogInformation(
                "POST /compile-video — workspace={Dir} file='{Name}' size={Size} duck={Duck}",
                workspace.Directory, video.FileName, video.Length, duckOriginalAudio);

            string videoPath;
            await using (var src = video.OpenReadStream())
                videoPath = await workspace.SaveStreamAsync(src, $"input{videoExt}", ct);

            byte[] narrationBytes;
            try
            {
                narrationBytes = Convert.FromBase64String(plan.FinalMixWavBase64);
            }
            catch (FormatException ex)
            {
                return BadRequest(new { error = $"final_mix_wav_base64 is not valid Base64: {ex.Message}" });
            }

            var narrationWavPath = workspace.GetPath("narration.wav");
            await System.IO.File.WriteAllBytesAsync(narrationWavPath, narrationBytes, ct);

            double totalDuration = plan.Script?.Segments?.Count > 0
                ? (double)plan.Script.Segments.Max(s => s.EndTimeSeconds)
                : 0;

            var outputPath = workspace.GetPath("output.mp4");

            await _videoCompilationService.CompileAsync(
                videoPath, narrationWavPath, totalDuration, outputPath, duckOriginalAudio, ct);

            _log.LogInformation("POST /compile-video — FFmpeg done, streaming {Path}", outputPath);

            var stream    = new DeferredDeleteFileStream(outputPath, workspace.Directory);
            workspace     = null;

            var projectId = plan.Script?.ProjectId ?? "output";
            return new FileStreamResult(stream, "video/mp4")
            {
                FileDownloadName      = $"{projectId}_final.mp4",
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
            if (workspace is not null)
                await workspace.DisposeAsync();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Human-in-the-loop endpoints
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Synthesizes a single segment and normalizes its duration WITHOUT automatic rewriting.
    /// Returns Ok, OverLimit (hard-capped audio), or UnderLimit (raw audio + gap size).
    /// </summary>
    [HttpPost("synthesize-segment")]
    [ProducesResponseType(typeof(SynthesizeSegmentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SynthesizeSegmentAsync(
        [FromBody] SynthesizeSegmentRequest request,
        CancellationToken ct)
    {
        var seg = request.Segment;

        if (seg is null || string.IsNullOrWhiteSpace(seg.Id))
            return BadRequest(new { error = "segment.id is required." });

        if (string.IsNullOrWhiteSpace(seg.Script))
            return BadRequest(new { error = "segment.script is required." });

        _log.LogInformation(
            "POST /synthesize-segment — id={Id} target={Target:F1}s",
            seg.Id, seg.TargetDuration);

        try
        {
            var result = await _segmentWorkflowService.SynthesizeAsync(request, ct);
            return Ok(result);
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            return StatusCode((int)ex.StatusCode.Value, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "POST /synthesize-segment — UNHANDLED {Type}: {Message}",
                ex.GetType().Name, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Rewrites an over-limit segment script shorter so it fits the target duration.
    /// </summary>
    [HttpPost("rewrite-segment")]
    [ProducesResponseType(typeof(RewrittenScriptDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RewriteSegmentAsync(
        [FromBody] RewriteShorterRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.OriginalScript))
            return BadRequest(new { error = "original_script is required." });

        if (request.TargetDurationSeconds <= 0)
            return BadRequest(new { error = "target_duration_seconds must be positive." });

        if (string.IsNullOrWhiteSpace(request.AiConfig?.Provider))
            return BadRequest(new { error = "ai_config.provider is required." });

        if (string.IsNullOrWhiteSpace(request.AiConfig?.ApiKey))
            return BadRequest(new { error = "ai_config.api_key is required." });

        _log.LogInformation(
            "POST /rewrite-segment — target={Target:F1}s words={Words} provider={Provider}",
            request.TargetDurationSeconds,
            request.OriginalScript.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
            request.AiConfig.Provider);

        try
        {
            var result = await _scriptService.RewriteShorterAsync(request, ct);
            _log.LogInformation(
                "POST /rewrite-segment — OK: {Old} → {New} words",
                request.OriginalScript.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
                result.WordCount);
            return Ok(result);
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            return StatusCode((int)ex.StatusCode.Value, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "POST /rewrite-segment — UNHANDLED {Type}: {Message}",
                ex.GetType().Name, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Rewrites an under-limit segment script longer to better fill the target duration.
    /// </summary>
    [HttpPost("rewrite-longer")]
    [ProducesResponseType(typeof(RewrittenScriptDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RewriteLongerAsync(
        [FromBody] RewriteShorterRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.OriginalScript))
            return BadRequest(new { error = "original_script is required." });

        if (request.TargetDurationSeconds <= 0)
            return BadRequest(new { error = "target_duration_seconds must be positive." });

        if (string.IsNullOrWhiteSpace(request.AiConfig?.Provider))
            return BadRequest(new { error = "ai_config.provider is required." });

        if (string.IsNullOrWhiteSpace(request.AiConfig?.ApiKey))
            return BadRequest(new { error = "ai_config.api_key is required." });

        _log.LogInformation(
            "POST /rewrite-longer — target={Target:F1}s words={Words} provider={Provider}",
            request.TargetDurationSeconds,
            request.OriginalScript.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
            request.AiConfig.Provider);

        try
        {
            var result = await _scriptService.RewriteLongerAsync(request, ct);
            _log.LogInformation(
                "POST /rewrite-longer — OK: {Old} → {New} words",
                request.OriginalScript.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
                result.WordCount);
            return Ok(result);
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            return StatusCode((int)ex.StatusCode.Value, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "POST /rewrite-longer — UNHANDLED {Type}: {Message}",
                ex.GetType().Name, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Assembles pre-synthesized segment WAVs into a single mixed audio track.
    /// </summary>
    [HttpPost("assemble")]
    [ProducesResponseType(typeof(AssembleResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AssembleAsync(
        [FromBody] AssembleRequest request,
        CancellationToken ct)
    {
        if (request.Segments is not { Count: > 0 })
            return BadRequest(new { error = "segments is empty or missing." });

        _log.LogInformation(
            "POST /assemble — {Count} segments bgMusic='{Track}'",
            request.Segments.Count,
            request.BackgroundMusic?.TrackFileName ?? "none");

        var inputs = new List<SegmentAudioAssemblyInput>(request.Segments.Count);
        for (int i = 0; i < request.Segments.Count; i++)
        {
            var s = request.Segments[i];
            if (string.IsNullOrWhiteSpace(s.WavBase64))
                return BadRequest(new { error = $"segments[{i}].wav_base64 is empty." });

            byte[] wavBytes;
            try { wavBytes = Convert.FromBase64String(s.WavBase64); }
            catch (FormatException ex)
            {
                return BadRequest(new
                {
                    error = $"segments[{i}].wav_base64 is not valid Base64: {ex.Message}",
                });
            }

            inputs.Add(new SegmentAudioAssemblyInput
            {
                SegmentId        = s.SegmentId,
                StartTimeSeconds = s.StartTimeSeconds,
                WavBytes         = wavBytes,
            });
        }

        double totalDuration = request.VideoDurationSeconds is > 0
            ? request.VideoDurationSeconds.Value
            : request.Segments.Max(s => s.EndTimeSeconds);

        _log.LogInformation("POST /assemble — totalDuration={Duration:F2}s", totalDuration);

        try
        {
            var finalMixWav = await _assemblyService.AssembleAsync(
                inputs, request.BackgroundMusic, totalDuration, ct);

            _log.LogInformation("POST /assemble — OK, {Bytes} bytes", finalMixWav.Length);

            return Ok(new AssembleResponse
            {
                FinalMixWavBase64 = Convert.ToBase64String(finalMixWav),
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "POST /assemble — UNHANDLED {Type}: {Message}",
                ex.GetType().Name, ex.Message);
            throw;
        }
    }
}
