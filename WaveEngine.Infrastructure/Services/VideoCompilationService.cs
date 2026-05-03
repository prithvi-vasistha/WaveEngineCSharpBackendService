using System.Diagnostics;
using System.Text;
using FFMpegCore;
using Microsoft.Extensions.Logging;
using WaveEngine.Application.Interfaces;

namespace WaveEngine.Infrastructure.Services;

/// <summary>
/// Compiles the final video by muxing an input video file with the assembled
/// narration WAV using FFmpeg.
///
/// Filter graph (when the video has its own audio and ducking is enabled):
///   [0:a] volume=0.15 [orig_ducked]
///   [orig_ducked][1:a] amix=inputs=2:dropout_transition=0 [audio_out]
///   -map 0:v -map [audio_out] -c:v copy -c:a aac -b:a 192k -shortest
///
/// When the video has no audio stream (or ducking is disabled):
///   -map 0:v -map 1:a -c:v copy -c:a aac -b:a 192k -shortest
/// </summary>
public class VideoCompilationService : IVideoCompilationService
{
    private readonly ILogger<VideoCompilationService> _log;

    public VideoCompilationService(ILogger<VideoCompilationService> log)
    {
        _log = log;
    }

    public async Task CompileAsync(
        string videoPath,
        string narrationWavPath,
        double totalDurationSeconds,
        string outputPath,
        bool duckOriginalAudio,
        CancellationToken ct = default)
    {
        // Probe the video to discover whether it carries an audio stream
        _log.LogInformation("VideoCompile: probing {Path}", videoPath);
        IMediaAnalysis mediaInfo;
        try
        {
            mediaInfo = await FFProbe.AnalyseAsync(videoPath, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "VideoCompile: FFProbe failed for {Path} — is ffmpeg/ffprobe installed?", videoPath);
            throw;
        }

        bool videoHasAudio = mediaInfo.AudioStreams.Count > 0;

        _log.LogInformation(
            "VideoCompile: videoHasAudio={HasAudio} duckOriginal={Duck} totalDuration={Duration:F2}s",
            videoHasAudio, duckOriginalAudio, totalDurationSeconds);

        var argList = BuildArgumentList(
            videoPath, narrationWavPath, outputPath,
            totalDurationSeconds, videoHasAudio, duckOriginalAudio);

        await ExecuteFFmpegAsync(argList, ct);

        _log.LogInformation("VideoCompile: output written to {Path}", outputPath);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Argument construction
    // ─────────────────────────────────────────────────────────────────────────

    private static List<string> BuildArgumentList(
        string videoPath,
        string narrationWavPath,
        string outputPath,
        double totalDurationSeconds,
        bool videoHasAudio,
        bool duckOriginalAudio)
    {
        var args = new List<string> { "-y" };

        // ── Inputs ─────────────────────────────────────────────────────────
        // 0: the source video (may or may not carry an audio track)
        args.AddRange(["-i", videoPath]);
        // 1: the assembled narration WAV (narration segments + optional bg music)
        args.AddRange(["-i", narrationWavPath]);

        // ── Audio routing ──────────────────────────────────────────────────
        if (videoHasAudio && duckOriginalAudio)
        {
            // Duck the video's original audio to 15% and mix it with the narration.
            // This preserves ambient sound / original music beneath the voice-over.
            var filterComplex =
                "[0:a]aformat=sample_rates=44100:channel_layouts=stereo,volume=0.15[orig_ducked];" +
                "[orig_ducked][1:a]amix=inputs=2:dropout_transition=0:normalize=0[audio_out]";

            args.AddRange(["-filter_complex", filterComplex]);
            args.AddRange(["-map", "0:v", "-map", "[audio_out]"]);
        }
        else
        {
            // No original audio stream, or caller requested narration-only output.
            args.AddRange(["-map", "0:v", "-map", "1:a"]);
        }

        // ── Encoding ───────────────────────────────────────────────────────
        // Copy the video bitstream verbatim (no re-encode — fast, lossless)
        args.AddRange(["-c:v", "copy"]);
        // Encode audio as AAC for broad MP4 container compatibility
        args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        // End at the shorter of the two inputs so the output never outlasts the video
        args.Add("-shortest");

        args.Add(outputPath);
        return args;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FFmpeg process runner
    // ─────────────────────────────────────────────────────────────────────────

    private async Task ExecuteFFmpegAsync(List<string> argList, CancellationToken ct)
    {
        var binaryFolder = GlobalFFOptions.Current.BinaryFolder;
        var ffmpegExe = string.IsNullOrWhiteSpace(binaryFolder)
            ? "ffmpeg"
            : Path.Combine(binaryFolder, "ffmpeg");

        _log.LogInformation(
            "VideoCompile: ffmpeg {Args}",
            string.Join(" ", argList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = ffmpegExe,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            },
        };

        foreach (var arg in argList)
            process.StartInfo.ArgumentList.Add(arg);

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderr.AppendLine(e.Data);
        };

        process.Start();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            _log.LogError(
                "VideoCompile: FFmpeg exited {Code}.\n{Stderr}",
                process.ExitCode, stderr.ToString());

            throw new InvalidOperationException(
                $"Video compilation failed (FFmpeg exit code {process.ExitCode}). " +
                "Check application logs for FFmpeg stderr output.");
        }

        _log.LogInformation("VideoCompile: FFmpeg exited successfully.");
    }
}
