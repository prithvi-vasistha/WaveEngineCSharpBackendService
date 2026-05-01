using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WaveEngine.Application.DTOs.Synthesize;
using WaveEngine.Application.Interfaces;

namespace WaveEngine.Infrastructure.HttpClients;

public class TtsSynthesisClient : ITtsSynthesisService
{
    private readonly HttpClient _http;
    private readonly ILogger<TtsSynthesisClient> _log;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public TtsSynthesisClient(HttpClient http, ILogger<TtsSynthesisClient> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<byte[]> GenerateVoiceAsync(
        SynthesizeRequest request,
        CancellationToken ct = default)
    {
        _log.LogInformation(
            "→ TTS POST {BaseAddress}/synthesize  voice={Voice} speed={Speed} textLen={Len}",
            _http.BaseAddress, request.Voice, request.Speed, request.Text?.Length ?? 0);

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("/synthesize", request, _jsonOptions, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "TTS HTTP call threw {Type} — is the TTS container running at {Base}?",
                ex.GetType().Name, _http.BaseAddress);
            throw;
        }

        _log.LogInformation("← TTS response: HTTP {Status}", (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _log.LogError("TTS error body: {Body}", error);
            throw new HttpRequestException(
                $"TTS synthesis failed ({(int)response.StatusCode}): {error}",
                null,
                response.StatusCode);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        _log.LogInformation("← TTS OK — received {Bytes} bytes of audio.", bytes.Length);
        return bytes;
    }
}
