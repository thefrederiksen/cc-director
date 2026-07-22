using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;
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
///
/// DENIED IN WHOLE ON HOSTED (issue #1863). Every route in this group is refused on a hosted Gateway,
/// through the shared refusal primitive (<see cref="HostedRouteDeny.Group"/>) - the same boundary the rest
/// of the owner-settings group and the key-vault group adopt. The setters write PROCESS-GLOBAL config.json
/// keys with no tenant dimension, so on shared hosted infrastructure any authenticated caller would be
/// repointing the wingman, car-mode and speech models for EVERYONE; and the catalog and test-chat routes
/// spend the deployment's own provider credential on whoever asks. Self-host is single-tenant and these are
/// legitimate owner function there, so on self-host nothing changes.
///
/// PER-ROUTE MODE, not an exclusive prefix. Although these seven routes DO sit under <c>/gateway/ai</c>
/// exclusively, the whole owner-settings group is expressed through ONE mechanism in ONE mode: its
/// <c>SettingsEndpoints</c> sibling is scattered across leaves under the shared <c>/gateway</c> prefix and
/// cannot be exclusive, so keeping this family per-route too gives the group one boundary and one revert
/// story rather than mixing modes. The primitive maps a verb-less refusal on each route's own pattern on
/// hosted - the handler is never mapped - so every request shape, including a verb the route never served,
/// meets the refusal rather than a 405.
/// </summary>
internal static class AiModelsEndpoint
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>The single error string the hosted refusal serves, held here so a test asserts the exact
    /// string served rather than a copy that could drift.</summary>
    internal const string RefusalMessage = "the model settings are not available on the hosted gateway";

    /// <summary>
    /// The hosted refusal payload for every model-settings route (issue #1863). Validated on construction,
    /// so a blank field fails the Gateway at startup. The primitive reads <see cref="GatewayHostedMode.IsHosted"/>
    /// DIRECTLY, never an optional argument that fails OPEN when a caller forgets it. 404 rather than 403: on
    /// hosted these routes do not exist as a concept, and 403 would imply some credential could reach them.
    /// </summary>
    private static HostedDenial Denial() => new(
        family: "model-settings",
        message: RefusalMessage,
        reason: "the model settings are process-global config.json values with no tenant dimension, and the " +
                "catalog and test-chat routes spend the deployment's own provider credential - so on shared " +
                "infrastructure one subscriber would repoint every model and bill everyone's credential",
        unDenyInstruction: "do NOT simply remove this deny: give each model setting a per-tenant home " +
                "(config.json has none today), migrate any global value already written, and scope the credential " +
                "the catalog/test-chat routes spend to the caller, before restoring a tenant-scoped route",
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Maps the model-settings group through the shared refusal primitive and RETURNS the denied handle, so a
    /// test can map a BRAND-NEW route onto the handle and find it already refused on hosted with no deny of
    /// its own. Without that return value nothing outside this file can state the future-route property.
    /// </summary>
    public static HostedDenyGroup Map(IEndpointRouteBuilder outer, KeyVault vault)
    {
        FileLog.Write($"[AiModelsEndpoint] mapping the model settings; hosted={GatewayHostedMode.IsHosted} - on hosted EVERY route in this group is refused via the shared refusal primitive (issue #1863)");

        // The whole group through ONE primitive-created handle - see the note in SettingsEndpoints for why a
        // per-route guard rots. The empty prefix leaves every route path written out in full, so self-host is
        // unchanged.
        var group = HostedRouteDeny.Group(outer, "", Denial());

        // THE ROUTES ARE MAPPED WHERE `outer` IS NOT IN SCOPE - see the note in SettingsEndpoints.Map. Each
        // of these seven routes could otherwise be mapped onto `outer` instead of the denied handle by a
        // one-word edit, bypassing the refusal for that route alone while the rest stayed denied. Handing the
        // typed handle to a method that never receives the ungrouped builder makes that INEXPRESSIBLE, and
        // collapses seven independently bypassable primitives into one.
        MapRoutes(group, vault);
        return group;
    }

    /// <summary>
    /// The seven model-settings routes. Takes the denied GROUP HANDLE and nothing else, deliberately, so no
    /// route can be mapped around the hosted refusal.
    /// </summary>
    private static void MapRoutes(HostedDenyGroup app, KeyVault vault)
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
