using System.Text.Json.Serialization;

namespace WaveEngine.Application.DTOs.Jobs;

/// <summary>
/// Single Server-Sent Event payload. Null fields are omitted from JSON output.
/// Use the static factory methods for type-safe construction.
/// </summary>
public class JobEventDto
{
    // ── Discriminator ────────────────────────────────────────────────────────
    public string Type { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // ── job_started ──────────────────────────────────────────────────────────
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TotalSegments { get; set; }

    // ── segment_* events ─────────────────────────────────────────────────────
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SegmentId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Attempt { get; set; }

    /// <summary>"over_limit" | "under_limit"</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ActualDuration { get; set; }

    /// <summary>Silence gap in seconds (under_limit needs_review only).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Gap { get; set; }

    // ── job_complete ─────────────────────────────────────────────────────────
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResultToken { get; set; }

    // ── job_error / segment_failed ───────────────────────────────────────────
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    // ─── Factory methods ─────────────────────────────────────────────────────

    public static JobEventDto JobStarted(int totalSegments) => new()
        { Type = "job_started", TotalSegments = totalSegments };

    public static JobEventDto ScriptGenerated() => new()
        { Type = "script_generated" };

    public static JobEventDto SegmentSynthesizing(string segId, int attempt) => new()
        { Type = "segment_synthesizing", SegmentId = segId, Attempt = attempt };

    public static JobEventDto SegmentRewriting(string segId, string reason, int attempt) => new()
        { Type = "segment_rewriting", SegmentId = segId, Reason = reason, Attempt = attempt };

    public static JobEventDto SegmentOk(string segId, double actualDuration) => new()
        { Type = "segment_ok", SegmentId = segId, ActualDuration = actualDuration };

    public static JobEventDto SegmentNeedsReview(string segId, string reason, double? gap) => new()
        { Type = "segment_needs_review", SegmentId = segId, Reason = reason, Gap = gap };

    public static JobEventDto Assembling() => new() { Type = "assembling" };

    public static JobEventDto Compiling() => new() { Type = "compiling" };

    public static JobEventDto JobComplete(string resultToken) => new()
        { Type = "job_complete", ResultToken = resultToken };

    public static JobEventDto JobError(string message) => new()
        { Type = "job_error", Message = message };
}
