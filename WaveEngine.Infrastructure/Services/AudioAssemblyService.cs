using System.Diagnostics;
using System.Text;
using FFMpegCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WaveEngine.Application.DTOs.Audio;
using WaveEngine.Application.Interfaces;

namespace WaveEngine.Infrastructure.Services;

/// <summary>
/// Assembles per-segment narration WAVs into a single mixed audio track using FFmpeg.
///
/// Filter graph layout:
///   Input 0  → background music (looped to totalDuration) or anullsrc (silence)
///   Input 1… → each narration segment WAV
///
///   [0:a] volume={bgVolume} → [bg]
///   [N:a] adelay={startMs}|{startMs} → [sN]
///   [bg][s0][s1]… amix=inputs=K → [mix]
///   output: -map [mix] -t {totalDuration}
/// </summary>
public class AudioAssemblyService : IAudioAssemblyService
{
    private readonly string _assetFolder;
    private readonly ILogger<AudioAssemblyService> _log;

    public AudioAssemblyService(IConfiguration config, ILogger<AudioAssemblyService> log)
    {
        var configured = config["Assets:BackgroundMusicFolder"] ?? "assets";
        _assetFolder   = ResolveAssetFolder(configured);
        _log = log;
    }

    /// <summary>
    /// Resolves the assets folder path, trying (in order):
    ///   1. Absolute path — used as-is.
    ///   2. Relative to the current working directory — works when running via `dotnet run`.
    ///   3. Relative to AppContext.BaseDirectory — works after publish / in Docker (/app).
    /// Logs a warning if none of the candidates exist; the FileNotFoundException
    /// on first use will then surface a clear message.
    /// </summary>
    private static string ResolveAssetFolder(string configured)
    {
        if (Path.IsPathRooted(configured))
            return configured;

        var fromCwd = Path.GetFullPath(configured);
        if (Directory.Exists(fromCwd))
            return fromCwd;

        var fromBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
        if (Directory.Exists(fromBase))
            return fromBase;

        // Return the cwd-relative path — FileNotFoundException on first use will be descriptive
        return fromCwd;
    }

    public async Task<byte[]> AssembleAsync(
        IReadOnlyList<SegmentAudioAssemblyInput> segments,
        BackgroundMusicConfig? backgroundMusic,
        double totalDurationSeconds,
        CancellationToken ct = default)
    {
        if (segments.Count == 0)
            throw new ArgumentException("At least one segment is required for assembly.", nameof(segments));

        var jobId = Guid.NewGuid().ToString("N")[..8];
        var tempDir = Path.Combine(Path.GetTempPath(), "waveengine", $"assembly_{jobId}");
        Directory.CreateDirectory(tempDir);

        _log.LogInformation(
            "Assembly [{Job}]: {Count} segments, totalDuration={Total:F2}s, track='{Track}', volume={Vol}%",
            jobId, segments.Count, totalDurationSeconds,
            backgroundMusic?.TrackFileName ?? "none", backgroundMusic?.VolumePercent ?? 0);

        try
        {
            // Write each segment WAV to a temp file
            var segPaths = new List<(string FilePath, double StartSeconds)>(segments.Count);
            for (int i = 0; i < segments.Count; i++)
            {
                var path = Path.Combine(tempDir, $"seg_{i}.wav");
                await File.WriteAllBytesAsync(path, segments[i].WavBytes, ct);
                segPaths.Add((path, segments[i].StartTimeSeconds));
                _log.LogInformation(
                    "Assembly [{Job}]: segment {Id} → {File} (start={Start:F2}s)",
                    jobId, segments[i].SegmentId, path, segments[i].StartTimeSeconds);
            }

            var outputPath = Path.Combine(tempDir, "final_mix.wav");
            await RunFFmpegMixAsync(segPaths, backgroundMusic, totalDurationSeconds, outputPath, jobId, ct);

            var result = await File.ReadAllBytesAsync(outputPath, ct);
            _log.LogInformation(
                "Assembly [{Job}]: complete — {Bytes} bytes, {Duration:F2}s.",
                jobId, result.Length, totalDurationSeconds);
            return result;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task RunFFmpegMixAsync(
        List<(string FilePath, double StartSeconds)> segments,
        BackgroundMusicConfig? bgMusic,
        double totalDurationSeconds,
        string outputPath,
        string jobId,
        CancellationToken ct)
    {
        bool hasBgMusic = !string.IsNullOrWhiteSpace(bgMusic?.TrackFileName);
        string? bgPath = null;

        if (hasBgMusic)
        {
            bgPath = ResolveTrackPath(bgMusic!.TrackFileName!);
            _log.LogInformation("Assembly [{Job}]: background music → {Path}", jobId, bgPath);
        }

        // Build argument list for Process (ArgumentList handles quoting automatically)
        var argList = BuildArgumentList(
            segments, bgMusic, hasBgMusic, bgPath, totalDurationSeconds, outputPath);

        await ExecuteFFmpegAsync(argList, jobId, ct);
    }

    private List<string> BuildArgumentList(
        List<(string FilePath, double StartSeconds)> segments,
        BackgroundMusicConfig? bgMusic,
        bool hasBgMusic,
        string? bgPath,
        double totalDurationSeconds,
        string outputPath)
    {
        var args = new List<string> { "-y" };

        // ── Input 0: background music or silent source ──────────────────────
        if (hasBgMusic)
        {
            // Loop the mp3 and cut at the total video duration so amix ends naturally
            args.AddRange(["-stream_loop", "-1", "-t", $"{totalDurationSeconds:F4}", "-i", bgPath!]);
        }
        else
        {
            // Infinite silence — output will be capped by -t on the output side
            args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo"]);
        }

        // ── Inputs 1…N: segment WAVs ─────────────────────────────────────────
        foreach (var (filePath, _) in segments)
            args.AddRange(["-i", filePath]);

        // ── Filter complex ───────────────────────────────────────────────────
        args.AddRange(["-filter_complex", BuildFilterComplex(segments, bgMusic, hasBgMusic)]);

        // ── Output mapping ───────────────────────────────────────────────────
        args.AddRange(["-map", "[mix]", "-t", $"{totalDurationSeconds:F4}", outputPath]);

        return args;
    }

    private static string BuildFilterComplex(
        List<(string FilePath, double StartSeconds)> segments,
        BackgroundMusicConfig? bgMusic,
        bool hasBgMusic)
    {
        var sb = new StringBuilder();

        // ── Background / silence stream ──────────────────────────────────────
        // Normalise to stereo 44100 Hz so all inputs share the same format
        double bgVol = hasBgMusic ? Math.Clamp((bgMusic?.VolumePercent ?? 30) / 100.0, 0, 1) : 0;

        if (hasBgMusic)
            sb.Append($"[0:a]aformat=sample_rates=44100:channel_layouts=stereo,volume={bgVol:F4}[bg];");
        else
            sb.Append("[0:a]aformat=sample_rates=44100:channel_layouts=stereo[bg];");

        // ── Delay each segment to its timeline start position ────────────────
        for (int i = 0; i < segments.Count; i++)
        {
            long delayMs = (long)Math.Round(segments[i].StartSeconds * 1000);
            // adelay pads both channels (L|R) identically
            sb.Append($"[{i + 1}:a]adelay={delayMs}|{delayMs}[s{i}];");
        }

        // ── Mix all streams ───────────────────────────────────────────────────
        // [bg] + one label per segment
        sb.Append("[bg]");
        for (int i = 0; i < segments.Count; i++)
            sb.Append($"[s{i}]");

        int inputCount = 1 + segments.Count;
        // normalize=0 — each stream keeps its own volume; without this amix divides
        // every input (including narration) by inputCount, making the volume knob
        // affect narration levels as well as background music.
        sb.Append($"amix=inputs={inputCount}:dropout_transition=0:normalize=0[mix]");

        return sb.ToString();
    }

    /// <summary>
    /// Resolves a track filename to a full path, trying exact match then .mp3 extension.
    /// </summary>
    private string ResolveTrackPath(string trackFileName)
    {
        // Try exact filename as provided
        var exactPath = Path.Combine(_assetFolder, trackFileName);
        if (File.Exists(exactPath))
            return exactPath;

        // Try appending .mp3 if no extension was given
        var mp3Path = Path.Combine(_assetFolder, trackFileName + ".mp3");
        if (File.Exists(mp3Path))
            return mp3Path;

        throw new FileNotFoundException(
            $"Background music track '{trackFileName}' not found in assets folder '{_assetFolder}'. " +
            $"Tried: '{exactPath}' and '{mp3Path}'.");
    }

    /// <summary>
    /// Runs the ffmpeg binary with the given argument list.
    /// Uses Process.StartInfo.ArgumentList for correct quoting on all platforms.
    /// </summary>
    private async Task ExecuteFFmpegAsync(
        List<string> argList,
        string jobId,
        CancellationToken ct)
    {
        var binaryFolder = GlobalFFOptions.Current.BinaryFolder;
        var ffmpegExe = string.IsNullOrWhiteSpace(binaryFolder)
            ? "ffmpeg"
            : Path.Combine(binaryFolder, "ffmpeg");

        _log.LogInformation(
            "Assembly [{Job}]: ffmpeg {Args}",
            jobId, string.Join(" ", argList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute  = false,
                CreateNoWindow   = true,
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
                "Assembly [{Job}]: FFmpeg exited {Code}.\n{Stderr}",
                jobId, process.ExitCode, stderr.ToString());

            throw new InvalidOperationException(
                $"Audio assembly failed (FFmpeg exit code {process.ExitCode}). " +
                "Check application logs for FFmpeg stderr output.");
        }

        _log.LogInformation("Assembly [{Job}]: FFmpeg finished successfully.", jobId);
    }
}
