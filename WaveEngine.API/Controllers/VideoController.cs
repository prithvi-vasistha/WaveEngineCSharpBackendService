using Microsoft.AspNetCore.Mvc;
using WaveEngine.Application.DTOs.Pipeline;
using WaveEngine.Application.Interfaces;

namespace WaveEngine.API.Controllers;

/// <summary>
/// POST /api/video/regenerate-segment  — re-synthesise (TTS) + reassemble + recompile
/// POST /api/video/move-segment        — shift timing only, no TTS, reassemble + recompile
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class VideoController : ControllerBase
{
    private readonly ISegmentRegenerationService _regeneration;
    private readonly ISegmentMoveService _move;
    private readonly ILogger<VideoController> _log;

    public VideoController(
        ISegmentRegenerationService regeneration,
        ISegmentMoveService move,
        ILogger<VideoController> log)
    {
        _regeneration = regeneration;
        _move         = move;
        _log          = log;
    }

    [HttpPost("regenerate-segment")]
    [ProducesResponseType(typeof(RegenerateSegmentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RegenerateSegmentAsync(
        [FromBody] RegenerateSegmentRequest request,
        CancellationToken ct)
    {
        if (request.JobId == Guid.Empty)
            return BadRequest(new { error = "job_id is required." });
        if (string.IsNullOrWhiteSpace(request.SegmentId))
            return BadRequest(new { error = "segment_id is required." });
        if (string.IsNullOrWhiteSpace(request.NewScript))
            return BadRequest(new { error = "new_script is required." });

        try
        {
            _log.LogInformation(
                "RegenerateSegment — job={Job} seg={Seg} hasAi={HasAi}",
                request.JobId, request.SegmentId, !string.IsNullOrWhiteSpace(request.AiInstruction));

            return Ok(await _regeneration.RegenerateAsync(request, ct));
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("move-segment")]
    [ProducesResponseType(typeof(MoveSegmentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MoveSegmentAsync(
        [FromBody] MoveSegmentRequest request,
        CancellationToken ct)
    {
        if (request.JobId == Guid.Empty)
            return BadRequest(new { error = "job_id is required." });
        if (string.IsNullOrWhiteSpace(request.SegmentId))
            return BadRequest(new { error = "segment_id is required." });
        if (request.NewStartTimeSeconds < 0)
            return BadRequest(new { error = "new_start_time_seconds must be ≥ 0." });

        try
        {
            _log.LogInformation(
                "MoveSegment — job={Job} seg={Seg} → {Start}s",
                request.JobId, request.SegmentId, request.NewStartTimeSeconds);

            return Ok(await _move.MoveAsync(request, ct));
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (FileNotFoundException ex) { return BadRequest(new { error = ex.Message }); }
    }
}
