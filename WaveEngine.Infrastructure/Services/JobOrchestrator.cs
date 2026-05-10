using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WaveEngine.Application.DTOs.Audio;
using WaveEngine.Application.DTOs.Jobs;
using WaveEngine.Application.DTOs.Script;
using WaveEngine.Application.DTOs.Synthesize;
using WaveEngine.Application.Interfaces;
using WaveEngine.Application.Models;
using WaveEngine.Domain.Entities;

namespace WaveEngine.Infrastructure.Services;

/// <summary>
/// Drives the full orchestration pipeline for a job:
///   1. Script generation
///   2. Parallel segment synthesis with automatic rewrite-retry
///   3. Audio assembly
///   4. Video compilation
///
/// Each step publishes progress events to the job's SSE channel via <see cref="IJobStore"/>.
///
/// Concurrency is bounded by <c>Orchestration:MaxSegmentConcurrency</c> (default 2)
/// so that resource-constrained environments (e.g. Docker with limited RAM) are protected.
/// The TTS synthesis semaphore inside <see cref="ISegmentWorkflowService"/> additionally
/// serialises actual ML inference to one call at a time.
/// </summary>
public sealed class JobOrchestrator : IJobOrchestrator
{
    private const int MaxRetries = 2; // 3 total attempts: initial + 2 retries

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IJobStore _store;
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly ILogger<JobOrchestrator> _log;

    public JobOrchestrator(
        IServiceScopeFactory scopeFactory,
        IJobStore store,
        IConfiguration cfg,
        ILogger<JobOrchestrator> log)
    {
        _scopeFactory = scopeFactory;
        _store        = store;
        _log          = log;

        var maxConcurrency = cfg.GetValue<int>("Orchestration:MaxSegmentConcurrency", 2);
        _concurrencySemaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    // ── StartJob ──────────────────────────────────────────────────────────────

    public void StartJob(OrchestratorJob job)
    {
        // Fire-and-forget background task.
        // A new DI scope ensures scoped services (e.g. HttpClient wrappers) are valid.
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var sp          = scope.ServiceProvider;

            await RunJobAsync(
                job,
                sp.GetRequiredService<IScriptGenerationService>(),
                sp.GetRequiredService<ISegmentWorkflowService>(),
                sp.GetRequiredService<IAudioAssemblyService>(),
                sp.GetRequiredService<IVideoCompilationService>());
        });
    }

    // ── RerunSegmentAsync ─────────────────────────────────────────────────────

    public async Task<SynthesizeSegmentResponse> RerunSegmentAsync(
        string jobId, string segmentId, string script,
        string? voiceOverride, CancellationToken ct)
    {
        var job = await _store.GetJobAsync(jobId)
            ?? throw new InvalidOperationException($"Job '{jobId}' not found.");

        var segment = job.Script?.Segments.FirstOrDefault(s => s.Id == segmentId)
            ?? throw new InvalidOperationException($"Segment '{segmentId}' not found in job '{jobId}'.");

        var segWithScript = CloneWithScript(segment, script);

        using var scope        = _scopeFactory.CreateScope();
        var segmentWorkflow    = scope.ServiceProvider.GetRequiredService<ISegmentWorkflowService>();

        var result = await segmentWorkflow.SynthesizeAsync(
            new SynthesizeSegmentRequest
            {
                Segment       = segWithScript,
                VoiceOverride = voiceOverride ?? job.Request.VoiceOverride,
            }, ct);

        // Save WAV and update job record only when synthesis was successful
        if (result.Status == SegmentSynthesisStatus.Ok)
        {
            var wav = Convert.FromBase64String(result.WavBase64);
            await _store.SaveSegmentWavAsync(jobId, segmentId, wav, ct);

            var segResult = job.SegmentResults.FirstOrDefault(r => r.SegmentId == segmentId);
            if (segResult is not null)
            {
                segResult.FinalScript    = script;
                segResult.ActualDuration = result.ActualDuration;
                segResult.NeedsReview    = false;
                segResult.NeedsReviewReason = null;
                segResult.Gap            = null;
            }

            await _store.UpdateJobAsync(job);
        }

        return result;
    }

    // ── RecompileAsync ────────────────────────────────────────────────────────

    public async Task<string> RecompileAsync(string jobId, CancellationToken ct)
    {
        var job = await _store.GetJobAsync(jobId)
            ?? throw new InvalidOperationException($"Job '{jobId}' not found.");

        if (job.Script is null || job.SegmentResults.Count == 0)
            throw new InvalidOperationException("Job has no processed segments to recompile.");

        using var scope      = _scopeFactory.CreateScope();
        var assemblyService  = scope.ServiceProvider.GetRequiredService<IAudioAssemblyService>();
        var videoService     = scope.ServiceProvider.GetRequiredService<IVideoCompilationService>();

        // ── 1. Load all segment WAVs ──────────────────────────────────────────
        var assemblyInputs = new List<SegmentAudioAssemblyInput>();
        foreach (var segResult in job.SegmentResults)
        {
            var segment = job.Script.Segments.First(s => s.Id == segResult.SegmentId);
            var wav     = await _store.LoadSegmentWavAsync(jobId, segResult.SegmentId);
            assemblyInputs.Add(new SegmentAudioAssemblyInput
            {
                SegmentId        = segResult.SegmentId,
                StartTimeSeconds = segment.StartTimeSeconds,
                WavBytes         = wav,
            });
        }

        // ── 2. Assemble ───────────────────────────────────────────────────────
        double totalDuration = TotalDuration(job);
        var mixWav = await assemblyService.AssembleAsync(
            assemblyInputs, job.Request.BackgroundMusic, totalDuration, ct);

        // ── 3. Compile video ──────────────────────────────────────────────────
        var workDir      = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            var narPath  = Path.Combine(workDir, "narration.wav");
            var videoExt = job.VideoFileExtension;
            var vidPath  = Path.Combine(workDir, $"input{videoExt}");
            var outPath  = Path.Combine(workDir, "output.mp4");

            await File.WriteAllBytesAsync(narPath, mixWav, ct);

            await using (var src = await _store.OpenVideoAsync(jobId))
            await using (var dst = File.Create(vidPath))
                await src.CopyToAsync(dst, ct);

            await videoService.CompileAsync(
                vidPath, narPath, totalDuration, outPath,
                job.Request.DuckOriginalAudio, ct);

            // ── 4. Save new result ────────────────────────────────────────────
            await using var resultStream = File.OpenRead(outPath);
            var token = await _store.SaveResultAsync(jobId, resultStream, ct);

            job.ResultToken = token;
            await _store.UpdateJobAsync(job);

            return token;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Core pipeline ─────────────────────────────────────────────────────────

    private async Task RunJobAsync(
        OrchestratorJob job,
        IScriptGenerationService scriptService,
        ISegmentWorkflowService segmentWorkflow,
        IAudioAssemblyService assemblyService,
        IVideoCompilationService videoService)
    {
        var writer = _store.GetOrCreateEventWriter(job.JobId);

        try
        {
            // Mark Running
            job.Status    = JobStatus.Running;
            job.StartedAt = DateTime.UtcNow;
            await _store.UpdateJobAsync(job);

            // ── Phase 1: Generate script ──────────────────────────────────────
            _log.LogInformation("Job {Id}: generating script…", job.JobId);

            var generateRequest = new GenerateScriptRequest
            {
                ProjectName    = job.Request.ProjectName,
                GlobalContext  = job.Request.GlobalContext,
                AiConfig       = job.Request.AiConfig,
                NarrativeGoals = job.Request.NarrativeGoals,
            };

            var script = await scriptService.GetMasterPlanAsync(generateRequest);

            job.Script = script;
            await _store.UpdateJobAsync(job);

            await writer.WriteAsync(JobEventDto.JobStarted(script.Segments.Count));
            await writer.WriteAsync(JobEventDto.ScriptGenerated());

            _log.LogInformation("Job {Id}: script generated — {N} segments.", job.JobId, script.Segments.Count);

            // ── Phase 2: Synthesise segments in parallel (bounded concurrency) ─
            var tasks = script.Segments.Select((seg, idx) =>
                ProcessSegmentAsync(job, seg, idx, segmentWorkflow, writer, CancellationToken.None));

            await Task.WhenAll(tasks);

            // ── Phase 3: Assemble ─────────────────────────────────────────────
            _log.LogInformation("Job {Id}: assembling…", job.JobId);
            await writer.WriteAsync(JobEventDto.Assembling(), CancellationToken.None);

            var assemblyInputs = job.SegmentResults.Select(sr =>
            {
                var segment = script.Segments.First(s => s.Id == sr.SegmentId);
                return new SegmentAudioAssemblyInput
                {
                    SegmentId        = sr.SegmentId,
                    StartTimeSeconds = segment.StartTimeSeconds,
                    WavBytes         = _store.LoadSegmentWavAsync(job.JobId, sr.SegmentId)
                                             .GetAwaiter().GetResult(),
                };
            }).ToList();

            double totalDuration = TotalDuration(job);
            var mixWav = await assemblyService.AssembleAsync(
                assemblyInputs, job.Request.BackgroundMusic, totalDuration);

            // ── Phase 4: Compile video ─────────────────────────────────────────
            _log.LogInformation("Job {Id}: compiling…", job.JobId);
            await writer.WriteAsync(JobEventDto.Compiling());

            var workDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            try
            {
                var narPath  = Path.Combine(workDir, "narration.wav");
                var videoExt = job.VideoFileExtension;
                var vidPath  = Path.Combine(workDir, $"input{videoExt}");
                var outPath  = Path.Combine(workDir, "output.mp4");

                await File.WriteAllBytesAsync(narPath, mixWav);

                await using (var src = await _store.OpenVideoAsync(job.JobId))
                await using (var dst = File.Create(vidPath))
                    await src.CopyToAsync(dst);

                await videoService.CompileAsync(
                    vidPath, narPath, totalDuration, outPath,
                    job.Request.DuckOriginalAudio);

                // ── Phase 5: Save result ──────────────────────────────────────
                await using var resultStream = File.OpenRead(outPath);
                var token = await _store.SaveResultAsync(job.JobId, resultStream, CancellationToken.None);

                job.ResultToken  = token;
                job.Status       = JobStatus.Complete;
                job.CompletedAt  = DateTime.UtcNow;
                await _store.UpdateJobAsync(job);

                await writer.WriteAsync(JobEventDto.JobComplete(token));
                _log.LogInformation("Job {Id}: complete — token={Token}", job.JobId, token);
            }
            finally
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort */ }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Job {Id}: FAILED — {Type}: {Message}",
                job.JobId, ex.GetType().Name, ex.Message);

            job.Status       = JobStatus.Failed;
            job.ErrorMessage = ex.Message;
            job.CompletedAt  = DateTime.UtcNow;

            try { await _store.UpdateJobAsync(job); } catch { /* best-effort */ }
            await writer.WriteAsync(JobEventDto.JobError(ex.Message));
        }
        finally
        {
            _store.CompleteEventChannel(job.JobId);
        }
    }

    // ── Per-segment retry loop ────────────────────────────────────────────────

    private async Task ProcessSegmentAsync(
        OrchestratorJob job,
        Segment segment,
        int index,
        ISegmentWorkflowService segmentWorkflow,
        System.Threading.Channels.ChannelWriter<JobEventDto> writer,
        CancellationToken ct = default)
    {
        await _concurrencySemaphore.WaitAsync(ct);

        try
        {
            var currentScript = segment.Script;

            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                _log.LogInformation(
                    "Job {Id} | Segment {SegId} attempt {A}/{Max}: synthesizing…",
                    job.JobId, segment.Id, attempt + 1, MaxRetries + 1);

                await writer.WriteAsync(
                    JobEventDto.SegmentSynthesizing(segment.Id, attempt + 1), ct);

                var result = await segmentWorkflow.SynthesizeAsync(
                    new SynthesizeSegmentRequest
                    {
                        Segment       = CloneWithScript(segment, currentScript),
                        VoiceOverride = job.Request.VoiceOverride,
                    }, ct);

                if (result.Status == SegmentSynthesisStatus.Ok)
                {
                    // Save WAV and record result
                    var wav = Convert.FromBase64String(result.WavBase64);
                    await _store.SaveSegmentWavAsync(job.JobId, segment.Id, wav, CancellationToken.None);

                    var segResult = new SegmentJobResult
                    {
                        SegmentId      = segment.Id,
                        FinalScript    = currentScript,
                        ActualDuration = result.ActualDuration,
                        TargetDuration = result.TargetDuration,
                        NeedsReview    = false,
                        WavFileName    = $"segments/{segment.Id}.wav",
                    };

                    lock (job.SegmentResults)
                        job.SegmentResults.Add(segResult);

                    await _store.UpdateJobAsync(job);
                    await writer.WriteAsync(JobEventDto.SegmentOk(segment.Id, result.ActualDuration));
                    return;
                }

                // Retries exhausted — accept as-is with needs_review flag
                if (attempt == MaxRetries)
                {
                    var wav = Convert.FromBase64String(result.WavBase64);
                    await _store.SaveSegmentWavAsync(job.JobId, segment.Id, wav, CancellationToken.None);

                    var reason = result.Status == SegmentSynthesisStatus.OverLimit
                        ? "over_limit" : "under_limit";
                    double? gap = result.Status == SegmentSynthesisStatus.UnderLimit
                        ? result.UnderLimitGap : null;

                    var segResult = new SegmentJobResult
                    {
                        SegmentId          = segment.Id,
                        FinalScript        = currentScript,
                        ActualDuration     = result.ActualDuration,
                        TargetDuration     = result.TargetDuration,
                        NeedsReview        = true,
                        NeedsReviewReason  = reason,
                        Gap                = gap,
                        WavFileName        = $"segments/{segment.Id}.wav",
                    };

                    lock (job.SegmentResults)
                        job.SegmentResults.Add(segResult);

                    await _store.UpdateJobAsync(job);
                    await writer.WriteAsync(
                        JobEventDto.SegmentNeedsReview(segment.Id, reason, gap));

                    _log.LogWarning(
                        "Job {Id} | Segment {SegId}: needs_review after {N} attempts, reason={R}",
                        job.JobId, segment.Id, MaxRetries + 1, reason);
                    return;
                }

                // Rewrite and retry
                var rewriteReason = result.Status == SegmentSynthesisStatus.OverLimit
                    ? "over_limit" : "under_limit";

                await writer.WriteAsync(
                    JobEventDto.SegmentRewriting(segment.Id, rewriteReason, attempt + 1));

                using var scope  = _scopeFactory.CreateScope();
                var scriptService = scope.ServiceProvider.GetRequiredService<IScriptGenerationService>();

                var rewriteReq = new RewriteShorterRequest
                {
                    OriginalScript        = currentScript,
                    TargetDurationSeconds = segment.TargetDuration,
                    AiConfig              = job.Request.AiConfig,
                    Keywords              = [],
                };

                var rewritten = result.Status == SegmentSynthesisStatus.OverLimit
                    ? await scriptService.RewriteShorterAsync(rewriteReq)
                    : await scriptService.RewriteLongerAsync(rewriteReq);

                currentScript = rewritten.RewrittenScript;

                _log.LogInformation(
                    "Job {Id} | Segment {SegId}: rewritten ({R}) → {Words} words.",
                    job.JobId, segment.Id, rewriteReason, rewritten.WordCount);
            }
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Segment CloneWithScript(Segment seg, string script) => new()
    {
        Id               = seg.Id,
        StartTimeSeconds = seg.StartTimeSeconds,
        EndTimeSeconds   = seg.EndTimeSeconds,
        TargetDuration   = seg.TargetDuration,
        Script           = script,
        TtsConfig        = seg.TtsConfig,
    };

    private static double TotalDuration(OrchestratorJob job) =>
        job.Request.VideoDurationSeconds is > 0
            ? job.Request.VideoDurationSeconds.Value
            : job.Script?.Segments.Max(s => (double)s.EndTimeSeconds) ?? 0;
}
