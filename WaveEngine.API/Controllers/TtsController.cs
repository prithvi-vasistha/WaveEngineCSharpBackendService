using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<TtsController> _log;

    public TtsController(
        IScriptGenerationService scriptService,
        ITtsSynthesisService synthesisService,
        ITtsOrchestrationService orchestrationService,
        ILogger<TtsController> log)
    {
        _scriptService = scriptService;
        _synthesisService = synthesisService;
        _orchestrationService = orchestrationService;
        _log = log;
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
}
