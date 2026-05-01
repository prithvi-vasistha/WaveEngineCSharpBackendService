using WaveEngine.Application.DTOs.Synthesize;

namespace WaveEngine.Application.Interfaces;

public interface ITtsSynthesisService
{
    Task<byte[]> GenerateVoiceAsync(SynthesizeRequest request, CancellationToken ct = default);
}
