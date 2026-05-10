namespace WaveEngine.Application.DTOs.Pipeline;

/// <summary>
/// Records the outcome of one synthesis attempt for a segment.
/// Used by TtsOrchestrationService to track all attempts and select the best one
/// when all LLM-rewrite retries are exhausted.
/// </summary>
public class SegmentSynthesisAttempt
{
    /// <summary>0 = initial synthesis, 1..N = after each LLM rewrite.</summary>
    public int AttemptNumber { get; set; }

    /// <summary>The script text that was synthesized in this attempt.</summary>
    public string Script { get; set; } = string.Empty;

    /// <summary>Raw WAV bytes returned by the TTS service (pre-normalization).</summary>
    public byte[] WavBytes { get; set; } = [];

    /// <summary>Actual duration of the raw synthesized audio in seconds.</summary>
    public double ActualDuration { get; set; }

    /// <summary>
    /// Signed delta: ActualDuration - TargetDuration.
    /// Positive = too long (CASE A), Negative = too short (CASE B).
    /// </summary>
    public double Delta { get; set; }
}
