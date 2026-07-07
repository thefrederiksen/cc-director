using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Transcription;

/// <summary>
/// Director-side client for the Gateway transcription job protocol. This is the only shape a
/// non-Gateway process should use: upload complete audio bytes, let the Gateway transcribe/correct,
/// and consume the returned transcript/provenance.
/// </summary>
public sealed class GatewayTranscriptionJobClient
{
    private const int MaxUploadChunkBytes = 5_000_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Func<GatewayConfig> _gatewayProvider;
    private readonly HttpClient? _http;

    public GatewayTranscriptionJobClient(Func<GatewayConfig>? gatewayProvider = null, HttpClient? http = null)
    {
        _gatewayProvider = gatewayProvider ?? GatewayConfig.Load;
        _http = http;
    }

    public bool IsConfigured => _gatewayProvider().IsEnabled;

    public async Task<GatewayTranscriptionJobResult> TranscribeAsync(
        byte[] audio,
        string fileName,
        string contentType,
        bool applyCorrection = true,
        CancellationToken ct = default)
    {
        if (audio is null) throw new ArgumentNullException(nameof(audio));
        if (audio.Length == 0) throw new ArgumentException("audio is empty", nameof(audio));

        var gateway = _gatewayProvider();
        if (!gateway.IsEnabled)
            throw new TranscriptionUnavailableException(
                "Gateway URL is not configured. Transcription now runs only through the Gateway.");

        var ownsClient = _http is null;
        var http = _http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        try
        {
            http.BaseAddress ??= new Uri(gateway.Url.TrimEnd('/') + "/");
            if (!string.IsNullOrWhiteSpace(gateway.Token))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token);

            var extension = ExtensionFor(fileName, contentType);
            var jobId = Guid.NewGuid().ToString("N");
            using var reg = await http.PostAsync("transcription/upload",
                JsonBody(new
                {
                    JobId = jobId,
                    Action = "return_transcript",
                    ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                    Extension = extension,
                    ApplyCorrection = applyCorrection,
                }), ct);
            var regBody = await reg.Content.ReadAsStringAsync(ct);
            if (!reg.IsSuccessStatusCode)
                ThrowForGatewayFailure((int)reg.StatusCode, regBody, "register");

            var registered = JsonSerializer.Deserialize<RegisterResponse>(regBody, JsonOptions);
            var id = registered?.JobId ?? registered?.UploadId ?? jobId;

            var chunks = PlanChunks(audio.Length).ToArray();
            foreach (var chunk in chunks)
            {
                using var content = new ByteArrayContent(audio, chunk.Start, chunk.Length);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var put = new HttpRequestMessage(HttpMethod.Put, $"transcription/{Uri.EscapeDataString(id)}/chunk/{chunk.Index}")
                {
                    Content = content,
                };
                put.Headers.TryAddWithoutValidation("X-Chunk-Sha256", Sha256Hex(audio, chunk.Start, chunk.Length));
                using var putResp = await http.SendAsync(put, ct);
                if (!putResp.IsSuccessStatusCode)
                    ThrowForGatewayFailure((int)putResp.StatusCode, await putResp.Content.ReadAsStringAsync(ct), $"chunk {chunk.Index}");
            }

            using var complete = await http.PostAsync($"transcription/{Uri.EscapeDataString(id)}/complete",
                JsonBody(new
                {
                    TotalChunks = chunks.Length,
                    Mime = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                    Ext = extension,
                    ApplyCorrection = applyCorrection,
                }), ct);
            var completeBody = await complete.Content.ReadAsStringAsync(ct);
            if (!complete.IsSuccessStatusCode)
                ThrowForGatewayFailure((int)complete.StatusCode, completeBody, "complete");

            var result = JsonSerializer.Deserialize<CompleteResponse>(completeBody, JsonOptions)
                         ?? throw new TranscriptionFailedException(502, "Gateway transcription complete returned no body");
            var raw = result.RawTranscript ?? result.Transcript ?? "";
            var cleaned = result.CleanedTranscript ?? result.Transcript ?? raw;
            FileLog.Write($"[GatewayTranscriptionJobClient] job={result.JobId ?? id} status={result.Status} rawLen={raw.Length} cleanedLen={cleaned.Length}");
            return new GatewayTranscriptionJobResult(
                JobId: result.JobId ?? id,
                ProtocolVersion: result.ProtocolVersion ?? "",
                RawTranscript: raw,
                CleanedTranscript: cleaned,
                DictionaryApplied: result.DictionaryApplied,
                CleanupReason: "gateway-transcription-job-v1",
                Mode: result.Mode,
                Provider: result.Provider,
                Model: result.Model);
        }
        finally
        {
            if (ownsClient) http.Dispose();
        }
    }

    private static void ThrowForGatewayFailure(int statusCode, string body, string phase)
    {
        var error = TryReadError(body) ?? body;
        if (statusCode == 402)
            throw new InsufficientCreditsException(TryReadCode(body) ?? "insufficient_credits", error);
        if ((statusCode is 401 or 403 or 404 or 409) && error.Contains("key", StringComparison.OrdinalIgnoreCase))
            throw new TranscriptionUnavailableException(error);
        throw new TranscriptionFailedException(statusCode, $"Gateway transcription {phase} failed: {error}");
    }

    private static string? TryReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString();
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message))
                    return message.GetString();
            }
        }
        catch { }
        return null;
    }

    private static string? TryReadCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                return code.GetString();
            if (doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var nested)
                && nested.ValueKind == JsonValueKind.String)
                return nested.GetString();
        }
        catch { }
        return null;
    }

    private static StringContent JsonBody(object body)
        => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static IEnumerable<UploadChunk> PlanChunks(int length)
    {
        var index = 0;
        for (var start = 0; start < length; start += MaxUploadChunkBytes)
        {
            var chunkLength = Math.Min(MaxUploadChunkBytes, length - start);
            yield return new UploadChunk(index++, start, chunkLength);
        }
    }

    private static string Sha256Hex(byte[] bytes, int offset, int count)
        => Convert.ToHexString(SHA256.HashData(bytes.AsSpan(offset, count))).ToLowerInvariant();

    private static string ExtensionFor(string fileName, string contentType)
    {
        var ext = Path.GetExtension(fileName ?? "").TrimStart('.').Trim();
        if (!string.IsNullOrWhiteSpace(ext)) return ext;
        var ct = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        return ct switch
        {
            "audio/webm" => "webm",
            "audio/ogg" => "ogg",
            "audio/mpeg" => "mp3",
            "audio/mp4" => "m4a",
            "audio/wav" or "audio/x-wav" => "wav",
            "audio/flac" => "flac",
            _ => "webm",
        };
    }

    private sealed record UploadChunk(int Index, int Start, int Length);

    private sealed class RegisterResponse
    {
        public string? JobId { get; set; }

        [JsonPropertyName("upload_id")]
        public string? UploadId { get; set; }
    }

    private sealed class CompleteResponse
    {
        public string? JobId { get; set; }
        public string? ProtocolVersion { get; set; }
        public string? Status { get; set; }
        public string? Transcript { get; set; }
        public string? RawTranscript { get; set; }
        public string? CleanedTranscript { get; set; }
        public bool DictionaryApplied { get; set; }
        public string? Mode { get; set; }
        public string? Provider { get; set; }
        public string? Model { get; set; }
    }
}

public sealed record GatewayTranscriptionJobResult(
    string JobId,
    string ProtocolVersion,
    string RawTranscript,
    string CleanedTranscript,
    bool DictionaryApplied,
    string CleanupReason,
    string? Mode,
    string? Provider,
    string? Model);
