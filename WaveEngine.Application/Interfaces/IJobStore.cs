using System.Threading.Channels;
using WaveEngine.Application.DTOs.Jobs;
using WaveEngine.Application.Models;

namespace WaveEngine.Application.Interfaces;

/// <summary>
/// Persists job state to disk and manages per-job in-memory SSE event channels.
/// Implementations must be thread-safe (registered as Singleton).
/// </summary>
public interface IJobStore
{
    // ── Job CRUD ──────────────────────────────────────────────────────────────

    /// <summary>Creates and persists a new job record. Does NOT save the video bytes.</summary>
    Task<OrchestratorJob> CreateJobAsync(string jobId, CreateJobRequest request, string videoFileExtension);

    Task<OrchestratorJob?> GetJobAsync(string jobId);

    /// <summary>Persists the full job state. Call after any mutation.</summary>
    Task UpdateJobAsync(OrchestratorJob job);

    Task<IReadOnlyList<OrchestratorJob>> GetRecentJobsAsync(int count = 20);

    // ── Video file ────────────────────────────────────────────────────────────

    /// <summary>Saves the uploaded video stream and returns the on-disk path.</summary>
    Task<string> SaveVideoAsync(string jobId, Stream videoStream, string fileExtension, CancellationToken ct);

    /// <summary>Opens the input video file for reading.</summary>
    Task<Stream> OpenVideoAsync(string jobId);

    // ── Segment WAVs ──────────────────────────────────────────────────────────

    Task SaveSegmentWavAsync(string jobId, string segmentId, byte[] wavBytes, CancellationToken ct);
    Task<byte[]> LoadSegmentWavAsync(string jobId, string segmentId);

    // ── Compiled result ───────────────────────────────────────────────────────

    /// <summary>Saves the compiled MP4, registers a result token, and returns it.</summary>
    Task<string> SaveResultAsync(string jobId, Stream mp4Stream, CancellationToken ct);

    /// <summary>
    /// Returns a stream for the compiled MP4 identified by the opaque result token,
    /// or null if the token is unknown.
    /// </summary>
    Task<(FileStream stream, string filename)?> OpenResultAsync(string resultToken);

    // ── SSE event channels (in-memory, not persisted) ─────────────────────────

    /// <summary>Returns the write end of the job's event channel, creating it if needed.</summary>
    ChannelWriter<JobEventDto> GetOrCreateEventWriter(string jobId);

    /// <summary>Returns the read end of the job's event channel, or null if no channel exists.</summary>
    ChannelReader<JobEventDto>? GetEventReader(string jobId);

    /// <summary>Signals that no more events will be written (closes the SSE stream).</summary>
    void CompleteEventChannel(string jobId);
}
