using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WaveEngine.Application.DTOs.Script;
using WaveEngine.Application.Interfaces;
using WaveEngine.Domain.Entities;

namespace WaveEngine.Infrastructure.HttpClients;

public class ScriptGenerationClient : IScriptGenerationService
{
    private readonly HttpClient _http;
    private readonly ILogger<ScriptGenerationClient> _log;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ScriptGenerationClient(HttpClient http, ILogger<ScriptGenerationClient> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<NarrationScript> GetMasterPlanAsync(
        GenerateScriptRequest request,
        CancellationToken ct = default)
    {
        _log.LogInformation(
            "→ Script POST {BaseAddress}/generate-script  provider={Provider} model={Model}",
            _http.BaseAddress, request.AiConfig?.Provider, request.AiConfig?.Model);

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("/generate-script", request, _jsonOptions, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Script HTTP call threw {Type} — is the script container running at {Base}?",
                ex.GetType().Name, _http.BaseAddress);
            throw;
        }

        _log.LogInformation("← Script /generate-script response: HTTP {Status}", (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _log.LogError("Script /generate-script error body: {Body}", error);
            throw new HttpRequestException(
                $"Script generation failed ({(int)response.StatusCode}): {error}",
                null,
                response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<NarrationScript>(_jsonOptions, ct);
        _log.LogInformation("← Script /generate-script OK — {Count} segments.", result?.Segments?.Count ?? 0);
        return result!;
    }

    public async Task<RewrittenScriptDto> RewriteShorterAsync(
        RewriteShorterRequest request,
        CancellationToken ct = default)
    {
        _log.LogInformation(
            "→ Script POST {BaseAddress}/rewrite-shorter  targetDuration={Target}s originalWords={Words}",
            _http.BaseAddress, request.TargetDurationSeconds,
            request.OriginalScript?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? 0);

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("/rewrite-shorter", request, _jsonOptions, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Script HTTP call threw {Type} — is the script container running at {Base}?",
                ex.GetType().Name, _http.BaseAddress);
            throw;
        }

        _log.LogInformation("← Script /rewrite-shorter response: HTTP {Status}", (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _log.LogError("Script /rewrite-shorter error body: {Body}", error);
            throw new HttpRequestException(
                $"Script rewrite failed ({(int)response.StatusCode}): {error}",
                null,
                response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<RewrittenScriptDto>(_jsonOptions, ct);
        _log.LogInformation("← Script /rewrite-shorter OK — {Words} words (max {Max}).",
            result?.WordCount ?? 0, result?.MaxWordCount ?? 0);
        return result!;
    }

    public async Task<RewrittenScriptDto> RewriteLongerAsync(
        RewriteLongerRequest request,
        CancellationToken ct = default)
    {
        _log.LogInformation(
            "→ Script POST {BaseAddress}/rewrite-longer  targetDuration={Target}s originalWords={Words}",
            _http.BaseAddress, request.TargetDurationSeconds,
            request.OriginalScript?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length ?? 0);

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("/rewrite-longer", request, _jsonOptions, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Script HTTP call threw {Type} — is the script container running at {Base}?",
                ex.GetType().Name, _http.BaseAddress);
            throw;
        }

        _log.LogInformation("← Script /rewrite-longer response: HTTP {Status}", (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _log.LogError("Script /rewrite-longer error body: {Body}", error);
            throw new HttpRequestException(
                $"Script expansion failed ({(int)response.StatusCode}): {error}",
                null,
                response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<RewrittenScriptDto>(_jsonOptions, ct);
        _log.LogInformation("← Script /rewrite-longer OK — {Words} words (target min {Max}).",
            result?.WordCount ?? 0, result?.MaxWordCount ?? 0);
        return result!;
    }
}
