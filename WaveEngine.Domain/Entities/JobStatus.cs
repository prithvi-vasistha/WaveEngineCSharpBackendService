namespace WaveEngine.Domain.Entities;

public enum JobStatus
{
    /// <summary>Job record created; pipeline not yet started.</summary>
    Created,

    /// <summary>LLM script-generation phase is running.</summary>
    Scripting,

    /// <summary>TTS synthesis + audio-assembly phase is running.</summary>
    Synthesizing,

    /// <summary>FFmpeg video-compilation phase is running.</summary>
    Stitching,

    /// <summary>Pipeline finished successfully; video and master-plan are available.</summary>
    Completed,

    /// <summary>Pipeline terminated with a fatal error.</summary>
    Failed,
}
