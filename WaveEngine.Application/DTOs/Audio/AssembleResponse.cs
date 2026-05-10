namespace WaveEngine.Application.DTOs.Audio;

public class AssembleResponse
{
    /// <summary>
    /// Final mixed audio track (all narration segments + optional background music),
    /// encoded as Base64 WAV. Pass this to /compile-video as part of the masterPlan.
    /// </summary>
    public string FinalMixWavBase64 { get; init; } = string.Empty;
}
