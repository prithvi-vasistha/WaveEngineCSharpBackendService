namespace WaveEngine.API;

/// <summary>
/// A short-lived, Guid-isolated temporary directory for a single video compilation job.
/// Implements IAsyncDisposable so it can be used in an `await using` block.
/// The directory (and all its contents) is deleted when disposed.
/// </summary>
public sealed class Workspace : IAsyncDisposable
{
    public string Directory { get; }

    public Workspace()
    {
        Directory = Path.Combine(
            Path.GetTempPath(), "waveengine", "video", Guid.NewGuid().ToString("N"));

        System.IO.Directory.CreateDirectory(Directory);
    }

    /// <summary>Returns the absolute path of a file inside this workspace.</summary>
    public string GetPath(string filename) => Path.Combine(Directory, filename);

    /// <summary>
    /// Streams the source directly to disk without loading it entirely into memory.
    /// </summary>
    public async Task<string> SaveStreamAsync(Stream source, string filename, CancellationToken ct)
    {
        var path = GetPath(filename);
        await using var dst = File.Create(path);
        await source.CopyToAsync(dst, ct);
        return path;
    }

    public ValueTask DisposeAsync()
    {
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A read-only FileStream that deletes a specified directory tree when it is disposed.
/// Used to stream the compiled MP4 back to the client without buffering it in memory,
/// while ensuring the temp workspace is cleaned up after the response finishes.
/// </summary>
public sealed class DeferredDeleteFileStream : FileStream
{
    private readonly string _directoryToDelete;

    public DeferredDeleteFileStream(string filePath, string directoryToDelete)
        : base(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
               bufferSize: 81_920, useAsync: true)
    {
        _directoryToDelete = directoryToDelete;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try { Directory.Delete(_directoryToDelete, recursive: true); }
            catch { /* best-effort */ }
        }
    }
}
