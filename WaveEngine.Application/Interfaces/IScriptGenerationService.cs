using WaveEngine.Application.DTOs.Script;
using WaveEngine.Domain.Entities;

namespace WaveEngine.Application.Interfaces;

public interface IScriptGenerationService
{
    Task<NarrationScript> GetMasterPlanAsync(GenerateScriptRequest request, CancellationToken ct = default);

    /// <summary>
    /// CASE A: Script produced audio that is too long even after max atempo speed-up.
    /// Asks the LLM to shorten the script to fit within the target duration.
    /// </summary>
    Task<RewrittenScriptDto> RewriteShorterAsync(RewriteShorterRequest request, CancellationToken ct = default);

    /// <summary>
    /// CASE B: Script produced audio that is too short even after max atempo slow-down.
    /// Asks the LLM to expand the script to fill the target duration.
    /// </summary>
    Task<RewrittenScriptDto> RewriteLongerAsync(RewriteLongerRequest request, CancellationToken ct = default);

    /// <summary>
    /// Post-generation edit: revise a segment script according to the user's instruction
    /// while honoring the target duration timing constraint.
    /// </summary>
    Task<RewrittenScriptDto> RefineAsync(RefineScriptRequest request, CancellationToken ct = default);
}
