using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Transcription;

/// <summary>
/// Director-side client for the Gateway-owned transcription endpoint. Production Director code may
/// capture and assemble audio, but audio-to-text must happen in the Gateway so provider routing,
/// keys, chunking, and dictionary correction have one owner.
/// </summary>
public sealed class GatewayTranscriptionClient
{
    private static readonly HttpClient SharedHttp = new(GatewayHttp.Handler()) { Timeout = TimeSpan.FromMinutes(5) };

    private readonly Func<GatewayConfig> _gatewayProvider;
    private readonly HttpClient _http;

    public GatewayTranscriptionClient(Func<GatewayConfig>? gatewayProvider = null, HttpClient? http = null)
    {
        _gatewayProvider = gatewayProvider ?? GatewayConfig.Load;
        _http = http ?? SharedHttp;
    }

    public async Task<GatewayTranscript> TranscribeAsync(
        byte[] audio,
        string fileName,
        string contentType,
        bool applyCorrection = true,
        CancellationToken ct = default)
    {
        if (audio is null) throw new ArgumentNullException(nameof(audio));
        if (audio.Length == 0)
            throw new ArgumentException("audio blob is empty; the Audio Completeness Gate must run before transcription", nameof(audio));

        var gateway = _gatewayProvider();
        if (!gateway.IsEnabled)
            throw new TranscriptionUnavailableException(
                "Transcription requires a configured Gateway. Connect this Director to the Gateway and try again.");

        var url = gateway.Url.TrimEnd('/') + "/transcription" + (applyCorrection ? "?correct=true" : "");
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(gateway.Token))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token);

        var body = new ByteArrayContent(audio);
        body.Headers.ContentType = ParseContentType(contentType, fileName);
        req.Content = body;

        FileLog.Write($"[GatewayTranscriptionClient] POST /transcription: bytes={audio.Length}, contentType={body.Headers.ContentType}, correct={applyCorrection}");

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (resp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            var mode = root.TryGetProperty("mode", out var m) ? m.GetString() : null;
            var model = root.TryGetProperty("model", out var md) ? md.GetString() : null;
            return new GatewayTranscript(text.Trim(), mode, model);
        }

        var error = ReadJsonString(json, "error") ?? $"Gateway transcription failed: HTTP {(int)resp.StatusCode}";

        if (resp.StatusCode == HttpStatusCode.Conflict)
            throw new TranscriptionUnavailableException(error);

        if ((int)resp.StatusCode == 402)
            throw new InsufficientCreditsException(ReadJsonString(json, "code") ?? HostedAiErrorMapper.ParseErrorCode(json), error);

        throw new TranscriptionFailedException((int)resp.StatusCode, error);
    }

    private static MediaTypeHeaderValue ParseContentType(string? contentType, string? fileName)
    {
        var raw = string.IsNullOrWhiteSpace(contentType) ? GuessAudioContentType(fileName) : contentType.Trim();
        return MediaTypeHeaderValue.TryParse(raw, out var parsed)
            ? parsed
            : new MediaTypeHeaderValue(GuessAudioContentType(fileName));
    }

    private static string GuessAudioContentType(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return ext switch
        {
            ".webm" => "audio/webm",
            ".ogg" => "audio/ogg",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".mp4" => "audio/mp4",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            _ => "audio/webm",
        };
    }

    private static string? ReadJsonString(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var value) ? value.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}

public sealed record GatewayTranscript(string Text, string? Mode, string? Model);
