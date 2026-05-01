namespace WaveEngine.Application.DTOs.Audio;

public enum NormalizationAction
{
    None,       // Within tolerance — file returned as-is
    Stretched,  // atempo applied to speed up
    Padded      // Silence appended to fill target duration
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
