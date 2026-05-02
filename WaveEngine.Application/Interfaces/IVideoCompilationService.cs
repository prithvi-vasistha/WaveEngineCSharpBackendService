namespace WaveEngine.Application.Interfaces;

/// <summary>
/// Compiles a final video by muxing the input video stream with the assembled
/// narration WAV, optionally ducking the video's original audio underneath.
/// </summary>
public interface IVideoCompilationService
{
    /// <summary>
    /// Runs FFmpeg to combine the video and the narration WAV into an MP4.
    /// </summary>
    /// <param name="videoPath">Absolute path to the input video file.</param>
    /// <param name="narrationWavPath">Absolute path to the fully-assembled narration WAV.</param>
    /// <param name="totalDurationSeconds">
    ///   Duration of the narration track (used to cap output length when the video is longer).
    ///   Pass 0 to let -shortest determine the output length automatically.
    /// </param>
    /// <param name="outputPath">Absolute path where the output MP4 should be written.</param>
    /// <param name="duckOriginalAudio">
    ///   When true and the video has an audio stream, that stream is ducked to 15% volume
    ///   and mixed with the narration. When false (or no audio stream exists) the narration
    ///   replaces the video audio entirely.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task CompileAsync(
        string videoPath,
        string narrationWavPath,
        double totalDurationSeconds,
        string outputPath,
        bool duckOriginalAudio,
        CancellationToken ct = default);
}
