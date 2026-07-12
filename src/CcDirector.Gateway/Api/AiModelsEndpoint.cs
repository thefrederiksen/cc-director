using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The AI model catalog + test surface for the Settings AI tab. It lets the page populate the wingman
/// and speech model dropdowns from DevThrottle's live catalog (not a hardcoded list), test a
/// chat model with one round-trip, and persist the chosen wingman/speech model. The browser never sees
/// the credential - the Gateway resolves it from the vault and presents it to DevThrottle.
///
///   GET  /gateway/ai/models?kind=chat|speech -> { models:[ {id, description, voices[], defaultVoice} ] }
///   POST /gateway/ai/test-chat  { model }    -> { ok, reply, seconds } | { error }
///   PUT  /gateway/ai/wingman-model      { model } -> { model }
///   PUT  /gateway/ai/wingman-fast-model { model } -> { model }
///   PUT  /gateway/ai/tts-model          { model } -> { model }
///
/// DevThrottle serves a typed catalog (GET /models?type=chat|speech) where each speech model carries
/// its own voices.
/// </summary>
internal static class AiModelsEndpoint
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static void Map(IEndpointRouteBuilder app, KeyVault vault)
    {
        app.MapGet("/gateway/ai/models", async (string? kind, CancellationToken ct) =>
        {
            var k = string.Equals(kind, "speech", StringComparison.OrdinalIgnoreCase) ? "speech" : "chat";
            var mode = TranscriptionModeConfig.Get();
            var ep = TranscriptionEndpointResolver.ResolveTts(mode);   // base URL + key name (same per provider)
            var key = vault.Get(ep.KeyName);
            if (string.IsNullOrWhiteSpace(key))
                return Results.Json(new { error = ProviderKeyMissingMessage(mode) },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            try
            {
                var models = await ListModelsAsync(ep.BaseUrl, key!, k, ct);
                return Results.Json(new { models });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[AiModelsEndpoint] list models FAILED ({mode.ToConfigString()}, {k}): {ex.Message}");
                return Results.Json(new { error = "could not list models: " + ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/gateway/ai/test-chat", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<ModelBody>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Model))
                    return Results.BadRequest(new { error = "body { \"model\": \"<id>\" } is required" });

                var mode = TranscriptionModeConfig.Get();
                var ep = TranscriptionEndpointResolver.ResolveWingman(mode);
                var key = vault.Get(ep.KeyName);
                if (string.IsNullOrWhiteSpace(key))
                    return Results.Json(new { ok = false, error = ProviderKeyMissingMessage(mode) },
                        statusCode: StatusCodes.Status503ServiceUnavailable);

                // One real round-trip to the chosen model - the same path the wingman uses - so the user
                // can prove a newly-picked model actually responds before relying on it.
                var brain = new HostedInferenceBrain(ep.BaseUrl, key!, body.Model.Trim(), log: FileLog.Write);
                var ask = await brain.AskAsync("Reply with exactly the single word: pong. Output nothing else.", ctx.RequestAborted);
                return Results.Json(new { ok = true, reply = ask.Text.Trim(), seconds = Math.Round(ask.ReplySeconds, 1) });
            }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid JSON" }); }
            catch (Exception ex)
            {
                FileLog.Write($"[AiModelsEndpoint] test-chat FAILED: {ex.Message}");
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPut("/gateway/ai/wingman-model", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<ModelBody>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Model))
                    return Results.BadRequest(new { error = "body { \"model\": \"<id>\" } is required" });
                var model = body.Model.Trim();
                WingmanModelConfig.Set(model);
                FileLog.Write($"[AiModelsEndpoint] wingman thinking model set: {model}");
                return Results.Json(new { model });
            }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid JSON" }); }
        });

        app.MapPut("/gateway/ai/wingman-fast-model", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<ModelBody>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Model))
                    return Results.BadRequest(new { error = "body { \"model\": \"<id>\" } is required" });
                var model = body.Model.Trim();
                WingmanModelConfig.SetFast(model);
                FileLog.Write($"[AiModelsEndpoint] wingman fast model set: {model}");
                return Results.Json(new { model });
            }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid JSON" }); }
        });

        app.MapPut("/gateway/ai/car-mode-model", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<ModelBody>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Model))
                    return Results.BadRequest(new { error = "body { \"model\": \"<id>\" } is required" });
                var model = body.Model.Trim();
                CarModeModelConfig.Set(model);
                FileLog.Write($"[AiModelsEndpoint] car mode model set: {model}");
                return Results.Json(new { model });
            }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid JSON" }); }
        });

        app.MapPut("/gateway/ai/car-mode-end-phrase", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<EndPhraseBody>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                // A blank phrase resets to the default (an empty phrase would end every turn); CarModeEndPhraseConfig.Set enforces it.
                CarModeEndPhraseConfig.Set(body?.Phrase ?? string.Empty);
                var phrase = CarModeEndPhraseConfig.Get();
                FileLog.Write($"[AiModelsEndpoint] car mode end phrase set: {phrase}");
                return Results.Json(new { phrase });
            }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid JSON" }); }
        });

        app.MapPut("/gateway/ai/tts-model", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<ModelBody>(ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Model))
                    return Results.BadRequest(new { error = "body { \"model\": \"<id>\" } is required" });
                TtsModelConfig.Set(body.Model);
                var model = TtsModelConfig.Get();
                FileLog.Write($"[AiModelsEndpoint] tts model set: {model}");
                return Results.Json(new { model });
            }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid JSON" }); }
        });
    }

    private static string ProviderKeyMissingMessage(TranscriptionMode mode) =>
        "not signed in to DevThrottle - sign in on the Account tab";

    /// <summary>
    /// List DevThrottle models for a kind. DevThrottle uses GET /models?type=chat|speech; speech
    /// models carry voices.
    /// </summary>
    private static async Task<List<ModelDto>> ListModelsAsync(string baseUrl, string key, string kind, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + "/models?type=" + kind;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var resp = await Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{(int)resp.StatusCode} from /models");

        var list = new List<ModelDto>();
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            var voices = new List<string>();
            if (item.TryGetProperty("voices", out var v) && v.ValueKind == JsonValueKind.Array)
                foreach (var voice in v.EnumerateArray())
                    if (voice.ValueKind == JsonValueKind.String) voices.Add(voice.GetString()!);

            var desc = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : "";
            var defVoice = item.TryGetProperty("defaultVoice", out var dv) && dv.ValueKind == JsonValueKind.String ? dv.GetString() : null;
            list.Add(new ModelDto(id!, desc ?? "", voices, defVoice));
        }
        return list;
    }

    private sealed record ModelBody(string? Model);
    private sealed record EndPhraseBody(string? Phrase);
    private sealed record ModelDto(string Id, string Description, List<string> Voices, string? DefaultVoice);
}
