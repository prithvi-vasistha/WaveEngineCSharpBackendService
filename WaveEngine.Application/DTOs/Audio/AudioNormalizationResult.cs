namespace WaveEngine.Application.DTOs.Audio;

public enum NormalizationAction
{
    None,      // Within tolerance — file returned as-is
    Stretched, // atempo > 1.0 applied to speed up (too long)
    Slowed,    // atempo < 1.0 applied to slow down (too short)
    Fallback   // Best-effort atempo at MaxSpeedFactor after all LLM retries exhausted
}

public class AudioNormalizationResult
{
    public string SegmentId { get; init; } = string.Empty;
    public double ActualDuration { get; init; }
    public double TargetDuration { get; init; }
    public double SpeedFactor { get; init; }
    public NormalizationAction ActionTaken { get; init; }
    public byte[] WavBytes { get; init; } = [];
}
