namespace WaveEngine.Application.DTOs.Audio;

/// <summary>
/// Controls background music mixing during audio assembly.
/// </summary>
public class BackgroundMusicConfig
{
    /// <summary>
    /// Filename of the background music track to use (e.g. "Bensound-Scifi-Tech-Sound.mp3").
    /// Must exist in the server's configured assets folder.
    /// Pass null or omit for no background music.
    /// </summary>
    public string? TrackFileName { get; set; }

    /// <summary>
    /// Background music volume as a percentage (0–100). Default: 30.
    /// The narration WAVs always play at 100% volume.
    /// </summary>
    public double VolumePercent { get; set; } = 30;
}
