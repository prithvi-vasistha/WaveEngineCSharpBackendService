using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WaveEngine.Application.DTOs.Jobs;
using WaveEngine.Application.Interfaces;
using WaveEngine.Application.Models;

namespace WaveEngine.Infrastructure.Persistence;

/// <summary>
/// Persists jobs as JSON files under a configurable directory tree:
///
///   {jobsRoot}/
///     {jobId}/
///       job.json         – serialised OrchestratorJob (minus video bytes)
///       input{ext}       – original uploaded video
///       segments/
///         {segId}.wav    – synthesised WAV per segment
///       result_{token}.mp4
///     tokens.json        – opaque token → relative result path
///
/// In-memory SSE channels are kept in a ConcurrentDictionary; they are
/// NOT persisted but are recreated when a new stream listener connects.
/// </summary>
public sealed class JsonFileJobStore : IJobStore, IDisposable
{
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented             = true,
        PropertyNamingPolicy      = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters                = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition    = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;
    private readonly ILogger<JsonFileJobStore> _log;

    // token → relative result path (e.g. "job123/result_abc.mp4")
    private readonly ConcurrentDictionary<string, string> _tokenMap = new();
    private readonly SemaphoreSlim _tokenFileLock = new(1, 1);

    // in-memory SSE channels keyed by jobId
    private readonly ConcurrentDictionary<string, Channel<JobEventDto>> _channels = new();

    public JsonFileJobStore(IConfiguration cfg, ILogger<JsonFileJobStore> log)
    {
        _log  = log;
        var configuredDir = cfg["Orchestration:JobsDirectory"];
        _root = string.IsNullOrWhiteSpace(configuredDir)
            ? Path.Combine(AppContext.BaseDirectory, "jobs")
            : configuredDir;

        Directory.CreateDirectory(_root);
        LoadTokenMapAsync().GetAwaiter().GetResult();
        ResetStuckJobsAsync().GetAwaiter().GetResult();
    }

    // ── Job CRUD ──────────────────────────────────────────────────────────────

    public async Task<OrchestratorJob> CreateJobAsync(
        string jobId, CreateJobRequest request, string videoFileExtension)
    {
        var job = new OrchestratorJob
        {
            JobId              = jobId,
            Status             = JobStatus.Created,
            CreatedAt          = DateTime.UtcNow,
            Request            = request,
            VideoFileExtension = videoFileExtension,
        };

        EnsureJobDir(jobId);
        await WriteJobAsync(job);
        return job;
    }

    public async Task<OrchestratorJob?> GetJobAsync(string jobId)
    {
        var path = JobFilePath(jobId);
        if (!File.Exists(path)) return null;

        await using var fs   = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<OrchestratorJob>(fs, _json);
    }

    public async Task UpdateJobAsync(OrchestratorJob job) =>
        await WriteJobAsync(job);

    public async Task<IReadOnlyList<OrchestratorJob>> GetRecentJobsAsync(int count = 20)
    {
        var dirs  = Directory.GetDirectories(_root)
                             .OrderByDescending(Directory.GetCreationTimeUtc)
                             .Take(count);
        var jobs  = new List<OrchestratorJob>(count);

        foreach (var dir in dirs)
        {
            var jobId = Path.GetFileName(dir);
            var job   = await GetJobAsync(jobId);
            if (job is not null) jobs.Add(job);
        }

        return jobs;
    }

    // ── Video file ────────────────────────────────────────────────────────────

    public async Task<string> SaveVideoAsync(
        string jobId, Stream videoStream, string fileExtension, CancellationToken ct)
    {
        var path = Path.Combine(JobDir(jobId), $"input{fileExtension}");
        await using var fs = File.Create(path);
        await videoStream.CopyToAsync(fs, ct);
        return path;
    }

    public Task<Stream> OpenVideoAsync(string jobId)
    {
        var dir = JobDir(jobId);

        // Find whichever input file exists (extension varies)
        var file = Directory.GetFiles(dir, "input.*").FirstOrDefault()
            ?? throw new FileNotFoundException($"No input video found for job {jobId}.");

        return Task.FromResult<Stream>(File.OpenRead(file));
    }

    // ── Segment WAVs ──────────────────────────────────────────────────────────

    public async Task SaveSegmentWavAsync(
        string jobId, string segmentId, byte[] wavBytes, CancellationToken ct)
    {
        var dir  = Path.Combine(JobDir(jobId), "segments");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, $"{segmentId}.wav"), wavBytes, ct);
    }

    public async Task<byte[]> LoadSegmentWavAsync(string jobId, string segmentId)
    {
        var path = Path.Combine(JobDir(jobId), "segments", $"{segmentId}.wav");
        return await File.ReadAllBytesAsync(path);
    }

    // ── Compiled result ───────────────────────────────────────────────────────

    public async Task<string> SaveResultAsync(
        string jobId, Stream mp4Stream, CancellationToken ct)
    {
        var token    = Guid.NewGuid().ToString("N");
        var filename = $"result_{token}.mp4";
        var path     = Path.Combine(JobDir(jobId), filename);

        await using var fs = File.Create(path);
        await mp4Stream.CopyToAsync(fs, ct);

        var relPath = Path.Combine(jobId, filename);
        _tokenMap[token] = relPath;
        await PersistTokenMapAsync();

        return token;
    }

    public Task<(FileStream stream, string filename)?> OpenResultAsync(string resultToken)
    {
        if (!_tokenMap.TryGetValue(resultToken, out var relPath))
            return Task.FromResult<(FileStream, string)?>(null);

        var fullPath = Path.Combine(_root, relPath);
        if (!File.Exists(fullPath))
            return Task.FromResult<(FileStream, string)?>(null);

        var filename = Path.GetFileName(relPath);
        return Task.FromResult<(FileStream, string)?>((File.OpenRead(fullPath), filename));
    }

    // ── SSE event channels ────────────────────────────────────────────────────

    public ChannelWriter<JobEventDto> GetOrCreateEventWriter(string jobId)
    {
        var ch = _channels.GetOrAdd(
            jobId,
            _ => Channel.CreateUnbounded<JobEventDto>(
                new UnboundedChannelOptions { SingleWriter = false, SingleReader = false }));

        return ch.Writer;
    }

    public ChannelReader<JobEventDto>? GetEventReader(string jobId) =>
        _channels.TryGetValue(jobId, out var ch) ? ch.Reader : null;

    public void CompleteEventChannel(string jobId)
    {
        if (_channels.TryGetValue(jobId, out var ch))
            ch.Writer.TryComplete();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private string JobDir(string jobId) => Path.Combine(_root, jobId);

    private string JobFilePath(string jobId) =>
        Path.Combine(JobDir(jobId), "job.json");

    private void EnsureJobDir(string jobId)
    {
        Directory.CreateDirectory(JobDir(jobId));
        Directory.CreateDirectory(Path.Combine(JobDir(jobId), "segments"));
    }

    private async Task WriteJobAsync(OrchestratorJob job)
    {
        var path = JobFilePath(job.JobId);
        var tmp  = path + ".tmp";

        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, job, _json);

        // Atomic rename to avoid partial writes corrupting the file
        File.Move(tmp, path, overwrite: true);
    }

    private string TokensFilePath() => Path.Combine(_root, "tokens.json");

    private async Task LoadTokenMapAsync()
    {
        var path = TokensFilePath();
        if (!File.Exists(path)) return;

        try
        {
            await using var fs = File.OpenRead(path);
            var map = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(fs, _json);
            if (map is not null)
                foreach (var (k, v) in map)
                    _tokenMap[k] = v;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to load token map from {Path}; starting fresh.", path);
        }
    }

    private async Task PersistTokenMapAsync()
    {
        await _tokenFileLock.WaitAsync();
        try
        {
            var path = TokensFilePath();
            var tmp  = path + ".tmp";
            await using (var fs = File.Create(tmp))
                await JsonSerializer.SerializeAsync(fs, _tokenMap, _json);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            _tokenFileLock.Release();
        }
    }

    /// <summary>
    /// On startup, mark any jobs that were still Running as Failed.
    /// They cannot be resumed because the in-memory channel and video pipeline state
    /// were lost when the process exited.
    /// </summary>
    private async Task ResetStuckJobsAsync()
    {
        if (!Directory.Exists(_root)) return;

        foreach (var dir in Directory.GetDirectories(_root))
        {
            var jobId = Path.GetFileName(dir);
            var job   = await GetJobAsync(jobId);

            if (job is null) continue;

            if (job.Status is JobStatus.Running or JobStatus.Created)
            {
                _log.LogWarning(
                    "Job {Id} was in status {Status} when the server restarted — marking as Failed.",
                    job.JobId, job.Status);

                job.Status       = JobStatus.Failed;
                job.ErrorMessage = "Server restarted while the job was in progress. Please submit a new job.";
                job.CompletedAt  = DateTime.UtcNow;
                await WriteJobAsync(job);
            }
        }
    }

    public void Dispose() => _tokenFileLock.Dispose();
}
