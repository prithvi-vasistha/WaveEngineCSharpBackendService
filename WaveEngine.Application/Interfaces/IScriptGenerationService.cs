using WaveEngine.Application.DTOs.Script;
using WaveEngine.Domain.Entities;

namespace WaveEngine.Application.Interfaces;

public interface IScriptGenerationService
{
    Task<NarrationScript> GetMasterPlanAsync(GenerateScriptRequest request, CancellationToken ct = default);

    Task<RewrittenScriptDto> RewriteShorterAsync(RewriteShorterRequest request, CancellationToken ct = default);

    Task<RewrittenScriptDto> RewriteLongerAsync(RewriteShorterRequest request, CancellationToken ct = default);
}
