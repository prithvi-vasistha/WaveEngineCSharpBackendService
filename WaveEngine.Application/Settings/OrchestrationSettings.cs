namespace WaveEngine.Application.Settings;

/// <summary>
/// Configurable knobs for the TTS orchestration pipeline.
/// Bind from appsettings.json under the "OrchestrationSettings" key.
/// </summary>
public class OrchestrationSettings
{
    /// <summary>
    /// Maximum number of LLM-rewrite retries per segment (CASE A or CASE B).
    /// After this many attempts the best result so far is used with a fallback atempo.
    /// Default: 2
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>
    /// Acceptable drift between synthesized duration and target duration (seconds).
    /// If |actual - target| &lt;= ToleranceSeconds no speed adjustment is applied.
    /// Default: 0.2
    /// </summary>
    public double ToleranceSeconds { get; set; } = 0.2;

    /// <summary>
    /// Maximum atempo factor for speeding up (too-long audio) or slowing down (too-short audio).
    /// Speed-up cap  = MaxSpeedFactor       (e.g. 1.15 = 15% faster).
    /// Slow-down cap = 1 / MaxSpeedFactor   (e.g. ~0.87 = 13% slower).
    /// If the required factor exceeds either cap, an LLM rewrite is triggered.
    /// Default: 1.15
    /// </summary>
    public double MaxSpeedFactor { get; set; } = 1.15;

    /// <summary>
    /// Maximum number of TTS synthesis tasks that may run concurrently.
    /// Limits simultaneous calls to the Python TTS microservice to protect RAM
    /// (especially important when running Kokoro inside Docker).
    /// Default: 2
    /// </summary>
    public int MaxTtsParallelism { get; set; } = 2;
}
