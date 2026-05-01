using WaveEngine.Domain.Entities;

namespace WaveEngine.Application.DTOs.Audio;

public class SegmentAudioResult
{
    public string SegmentId { get; init; } = string.Empty;
    public double ActualDuration { get; init; }
    public double TargetDuration { get; init; }
    public NormalizationAction ActionTaken { get; init; }
    public double SpeedFactor { get; init; }

    /// <summary>WAV audio encoded as Base64 for JSON transport.</summary>
    public string WavBase64 { get; init; } = string.Empty;
}

public class OrchestrationResult
{
    public NarrationScript Script { get; init; } = new();
    public List<SegmentAudioResult> SegmentAudio { get; init; } = [];
}
