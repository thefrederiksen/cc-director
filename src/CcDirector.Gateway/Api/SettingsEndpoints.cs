using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core.Configuration;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Gateway settings surface that backs the one Cockpit Settings page
/// (docs/architecture/gateway/SETTINGS_OWNERSHIP.md). One GET assembles the whole snapshot the
/// page renders; two actions mutate Gateway-owned state. Inherits the host-wide token middleware,
/// same as every other endpoint here.
///
///   GET  /gateway/settings        -> { version, state, port, uptimeSeconds, directors, mode,
///                                       cockpit:{port,up,url}, brain:{...}, autostart:{supported,enabled} }
///   POST /gateway/brain/restart   -> { ok, brain:{...} } (restarts the warm brain, issue #184)
///   PUT  /gateway/brain/config    body { "agentId": str, "model": str } -> { agentId, tool, model }
///                                  (issue #510; legacy { "tool": str, ... } still accepted)
///   PUT  /gateway/autostart       body { "enabled": bool } -> { supported, enabled }
///   GET  /gateway/transcription-mode -> { mode } ("byo" | "devthrottle") (issue #497)
///   PUT  /gateway/transcription-mode body { "mode": "byo"|"devthrottle" } -> { mode }
///   GET  /gateway/ai-provider     -> { provider, wingmanModel, transcriptionModel, ttsVoice, voices[] }
///   PUT  /gateway/ai-provider     body { "provider": "devthrottle"|"openai" } (sets mode + wingman model)
///   GET  /gateway/tts-voice       -> { voice, voices[] }
///   PUT  /gateway/tts-voice       body { "voice": "nova"|... } -> { voice }
///   GET  /gateway/telemetry-consent  -> { enabled } (fleet-wide richer-usage consent, default ON, issue #649)
///   PUT  /gateway/telemetry-consent  body { "enabled": bool } -> { enabled }
/// </summary>
internal static class SettingsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void Map(IEndpointRouteBuilder app, GatewayHost host)
    {
        app.MapGet("/gateway/settings", async () =>
        {
            // The Cockpit is served in-process by the Gateway (issue #979 retired the separate Blazor
            // Cockpit process), so its reachability is the Gateway's own and its "port" is the Gateway
            // port - there is no distinct loopback child to probe.
            var cockpitPort = host.Port;
            var cockpitUp = true;
            var up = DateTime.UtcNow - host.StartedAtUtc;

            return Results.Json(new
            {
                version = AppVersion.Full,
                state = "Running",
                port = host.Port,
                uptimeSeconds = (long)up.TotalSeconds,
                directors = host.Registry.ListDirectors().Count,
                mode = host.SettingsHooks?.Mode?.Invoke() ?? "unknown",
                // Issue #457: the fleet network addressing mode ("tailscale" | "lan").
                addressingMode = Core.Configuration.AddressingModeConfig.Get().ToConfigString(),
                cockpit = new
                {
                    port = cockpitPort,
                    up = cockpitUp,
                    url = TailscaleIdentity.TryGetFrontDoorBaseUrl() is { } b ? b + "/" : null,
                },
                brain = await BrainBlockAsync(host),
                autostart = new
                {
                    supported = host.SettingsHooks?.AutostartEnabled is not null,
                    enabled = host.SettingsHooks?.AutostartEnabled?.Invoke(),
                },
                // Issue #531 follow-up: when on, every wingman summary is saved (terminal + response)
                // as training data for improving the wingman.
                wingmanTrainingCapture = Core.Configuration.WingmanTrainingCaptureConfig.Get(),
                // Issue #649: the fleet-wide richer-usage-telemetry consent (opt-out). Default ON.
                // Gates ONLY the richer usage telemetry; the always-on login/startup events are
                // never gated by it.
                telemetryConsent = Core.Configuration.TelemetryConsentConfig.Get(),
            });
        });

        // Restart the warm brain (issue #184): the one recovery verb, and doubles as a manual
        // start. Mirrors the old tray-window Restart Brain button, now reachable from the Cockpit.
        app.MapPost("/gateway/brain/restart", async () =>
        {
            FileLog.Write("[SettingsEndpoints] POST /gateway/brain/restart");
            try
            {
                await host.Brain.RestartAsync();
                FileLog.Write($"[SettingsEndpoints] brain restart OK: pid={host.Brain.ProcessId}, session={host.Brain.SessionId}");
                return Results.Json(new { ok = true, brain = await BrainBlockAsync(host) });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SettingsEndpoints] brain restart FAILED: {ex.Message}");
                return Results.Json(new { ok = false, error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // Persist the brain tool + model choice (issue #393). Both are Gateway-level settings in
        // config.json, the same store the existing brain_model uses, so the choice applies fleet-wide
        // without editing any Director. The running brain is unaffected until the next Gateway restart
        // (the supervisor's driver/options are fixed at host construction) - same as brain_model.
        app.MapPut("/gateway/brain/config", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<BrainConfigBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null)
                    return Results.BadRequest(new { error = "body { \"agentId\": \"<id>\", \"model\": \"<model>\" } is required" });

                // Issue #510: the wingman agent is chosen by its registered-agent id (the same
                // machine list the New Session picker offers), not a hardcoded Claude-only tool
                // name. The legacy "tool" field (an AgentKind name, issue #393) is still accepted
                // so existing callers keep working - it is matched to the first enabled entry of
                // that kind. Either way we resolve to a real registered entry and persist its id,
                // its AgentKind (for the runtime), and the model.
                var agents = LoadMachineAgents();
                Core.Configuration.AgentEntry? entry = null;

                if (!string.IsNullOrWhiteSpace(body.AgentId))
                {
                    var id = body.AgentId.Trim();
                    entry = agents.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
                    if (entry is null)
                        return Results.BadRequest(new { error = "agentId must be a registered, enabled agent on this machine" });
                }
                else if (!string.IsNullOrWhiteSpace(body.Tool))
                {
                    if (!Enum.TryParse<Core.Agents.AgentKind>(body.Tool.Trim(), ignoreCase: true, out var tool))
                        return Results.BadRequest(new { error = "tool must be a recognised agent-kind name" });
                    entry = agents.FirstOrDefault(a => a.Type == tool);
                    if (entry is null)
                        return Results.BadRequest(new { error = $"no registered, enabled agent of kind {tool} on this machine" });
                }
                else
                {
                    return Results.BadRequest(new { error = "agentId is required (a registered agent on this machine)" });
                }

                if (string.IsNullOrWhiteSpace(body.Model))
                    return Results.BadRequest(new { error = "model is required (a model alias or id)" });
                var model = body.Model.Trim();

                Core.Configuration.CcDirectorConfigService.MergePatch(
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["brain_agent_id"] = entry.Id,
                        ["brain_tool"] = entry.Type.ToString(),
                        ["brain_model"] = model,
                    });
                FileLog.Write($"[SettingsEndpoints] brain config set: agentId={entry.Id}, tool={entry.Type}, model={model}");
                return Results.Json(new { agentId = entry.Id, tool = entry.Type.ToString(), model });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/brain/config bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // Read wingman training-data capture state (issue #531 follow-up): when on, every wingman
        // summary saves up to 20,000 chars of the session terminal + the wingman response as a
        // labeled example for improving the wingman.
        app.MapGet("/gateway/wingman/training-capture", () =>
            Results.Json(new { enabled = Core.Configuration.WingmanTrainingCaptureConfig.Get() }));

        // Write the training-data capture toggle. Takes effect immediately (read at capture time) -
        // no restart, unlike wingman_enabled.
        app.MapPut("/gateway/wingman/training-capture", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<WingmanBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null)
                    return Results.BadRequest(new { error = "body { \"enabled\": true|false } is required" });

                Core.Configuration.CcDirectorConfigService.MergePatch(
                    new System.Text.Json.Nodes.JsonObject { ["wingman_training_capture"] = body.Enabled });
                FileLog.Write($"[SettingsEndpoints] wingman_training_capture set to {body.Enabled}");
                return Results.Json(new { enabled = body.Enabled });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/wingman/training-capture bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // The fleet-wide richer-usage-telemetry consent (issue #649). Lives in its own endpoint class
        // (TelemetryConsentEndpoint) so it can be unit-tested in isolation, but it is part of the one
        // Gateway settings surface and is mapped here alongside the other settings routes. Default ON;
        // gates only the richer usage telemetry - the always-on login/startup events are never gated.
        TelemetryConsentEndpoint.Map(app);

        // Network addressing mode (issue #457): "tailscale" (advertise the Tailscale Serve
        // front door) or "lan" (advertise the machine's real LAN IP). Stored as the top-level
        // config.json key addressing_mode. This is a per-machine setting read at process start;
        // it applies to THIS Gateway host's own Directors on the next restart. Remote Directors
        // read their own machine's config (see the docs note on issue #457).
        app.MapGet("/gateway/addressing-mode", () =>
            Results.Json(new { mode = Core.Configuration.AddressingModeConfig.Get().ToConfigString() }));

        app.MapPut("/gateway/addressing-mode", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<AddressingModeBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Mode))
                    return Results.BadRequest(new { error = "body { \"mode\": \"tailscale\"|\"lan\" } is required" });

                if (!Core.Configuration.AddressingModeExtensions.IsValid(body.Mode))
                    return Results.BadRequest(new { error = "mode must be \"tailscale\" or \"lan\"" });

                var mode = Core.Configuration.AddressingModeExtensions.Parse(body.Mode);
                Core.Configuration.CcDirectorConfigService.MergePatch(
                    new System.Text.Json.Nodes.JsonObject { ["addressing_mode"] = mode.ToConfigString() });
                FileLog.Write($"[SettingsEndpoints] addressing_mode set to {mode.ToConfigString()}");
                return Results.Json(new { mode = mode.ToConfigString() });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/addressing-mode bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // Transcription mode (issue #497, #887): "devthrottle" (the signed-in account's hosted
        // transcription -> devthrottle.com, the DEFAULT) or "byo" (the user's own OpenAI key ->
        // api.openai.com). The old in-process "local" option was removed in #887 (we dogfood our own
        // hosted service); a "local" value is accepted as a legacy alias and migrates forward to
        // devthrottle. Stored as the top-level config.json key transcription_mode, the same store
        // addressing_mode uses. The two keys live in the existing vault (OPENAI_API_KEY,
        // DEVTHROTTLE_API_KEY).
        app.MapGet("/gateway/transcription-mode", () =>
            Results.Json(new { mode = Core.Configuration.TranscriptionModeConfig.Get().ToConfigString() }));

        app.MapPut("/gateway/transcription-mode", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<TranscriptionModeBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Mode))
                    return Results.BadRequest(new { error = "body { \"mode\": \"devthrottle\"|\"byo\" } is required" });

                if (!Core.Configuration.TranscriptionModeExtensions.IsValid(body.Mode))
                    return Results.BadRequest(new { error = "mode must be \"devthrottle\" or \"byo\"" });

                // Parse migrates a legacy "local" forward to devthrottle (issue #887); both current
                // modes are selectable - DevThrottle is the default we dogfood.
                var mode = Core.Configuration.TranscriptionModeExtensions.Parse(body.Mode);

                Core.Configuration.TranscriptionModeConfig.Set(mode);
                FileLog.Write($"[SettingsEndpoints] transcription_mode set to {mode.ToConfigString()}");
                return Results.Json(new { mode = mode.ToConfigString() });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/transcription-mode bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // The consolidated AI provider (the one switch that drives transcription + wingman + TTS).
        // A projection of transcription_mode: "devthrottle" (hosted, ours) or "openai" (bring-your-own
        // OpenAI key). GET returns the derived wingman + transcription models, the TTS voice, and the
        // selectable voices. PUT sets transcription_mode AND the provider-default wingman model
        // (brain_model) atomically, so one choice moves all three capabilities to the same provider.
        app.MapGet("/gateway/ai-provider", () => Results.Json(AiProviderSnapshot()));

        app.MapPut("/gateway/ai-provider", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<AiProviderBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Provider))
                    return Results.BadRequest(new { error = "body { \"provider\": \"devthrottle\"|\"openai\" } is required" });
                if (!TryParseProvider(body.Provider, out var mode))
                    return Results.BadRequest(new { error = "provider must be \"devthrottle\" or \"openai\"" });

                // Reset the wingman model, speech model, and voice to the NEW provider's defaults. This
                // is required, not just tidy: the two providers' catalogs do not overlap (a Kokoro voice
                // is not an OpenAI voice; zai-org/GLM-5.2 is not an OpenAI model), so a value saved for one
                // provider would fail against the other. One switch moves all three cleanly.
                var wingmanModel = Core.Configuration.TranscriptionEndpointResolver.ResolveWingman(mode).Model;
                var ttsModel = Core.Configuration.TranscriptionEndpointResolver.DefaultTtsModel(mode);
                var ttsVoice = Core.Configuration.TranscriptionEndpointResolver.DefaultTtsVoice(mode);
                Core.Configuration.CcDirectorConfigService.MergePatch(
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["transcription_mode"] = mode.ToConfigString(),
                        ["brain_model"] = wingmanModel,
                        ["tts_model"] = ttsModel,
                        ["tts_voice"] = ttsVoice,
                    });
                FileLog.Write($"[SettingsEndpoints] ai_provider set: mode={mode.ToConfigString()}, wingmanModel={wingmanModel}, ttsModel={ttsModel}, ttsVoice={ttsVoice}");
                return Results.Json(AiProviderSnapshot());
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/ai-provider bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // The text-to-speech voice for spoken wingman output (consolidated AI settings). One of the
        // OpenAI-compatible voices; applies to whichever provider is selected (both are OpenAI-compatible
        // for speech). Read at synthesis time, so a change is honored on the next spoken summary.
        app.MapGet("/gateway/tts-voice", () =>
        {
            var mode = Core.Configuration.TranscriptionModeConfig.Get();
            return Results.Json(new
            {
                voice = Core.Configuration.TtsVoiceConfig.Resolve(mode),
                voices = Core.Configuration.TtsVoiceConfig.OpenAiVoices,   // fallback set; DevThrottle voices come from /gateway/ai/models
            });
        });

        app.MapPut("/gateway/tts-voice", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<TtsVoiceBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Voice))
                    return Results.BadRequest(new { error = "body { \"voice\": \"<id>\" } is required" });

                // Any non-empty voice id is accepted - the catalog is dynamic and provider-specific, so
                // there is no fixed allow-list to check against.
                Core.Configuration.TtsVoiceConfig.Set(body.Voice);
                var voice = body.Voice.Trim();
                FileLog.Write($"[SettingsEndpoints] tts_voice set to {voice}");
                return Results.Json(new { voice });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/tts-voice bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // Toggle the per-user autostart Run-key. The write itself is GatewayApp-owned (it needs the
        // tray exe path + args), supplied via SettingsHooks; a host with no hook answers unsupported.
        app.MapPut("/gateway/autostart", async (HttpContext ctx) =>
        {
            var set = host.SettingsHooks?.SetAutostart;
            if (set is null)
            {
                FileLog.Write("[SettingsEndpoints] PUT /gateway/autostart: no hook; unsupported on this host");
                return Results.Json(new { supported = false });
            }

            try
            {
                var body = await JsonSerializer.DeserializeAsync<AutostartBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null)
                    return Results.BadRequest(new { error = "body { \"enabled\": true|false } is required" });

                var nowEnabled = set(body.Enabled);
                FileLog.Write($"[SettingsEndpoints] autostart requested={body.Enabled}, now={nowEnabled}");
                return Results.Json(new { supported = true, enabled = nowEnabled });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/autostart bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });
    }

    /// <summary>The brain status block - shared by the snapshot GET and the restart POST.</summary>
    private static async Task<object> BrainBlockAsync(GatewayHost host)
    {
        var brain = host.Brain;
        var health = await brain.GetHealthAsync();

        // Issue #510: the wingman agent picker is filled from the agents registered on this machine
        // (the same enabled agent.entries the New Session dialog offers), not a hardcoded
        // Claude-only list. The Cockpit selects the saved agent by id; "agentId" is the saved
        // choice (brain_agent_id) so the picker round-trips across a page reload.
        var agents = LoadMachineAgents();
        var savedAgentId = Core.Configuration.CcDirectorConfigService.ReadRaw()["brain_agent_id"] is { } idNode
            && idNode.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? idNode.GetValue<string>()
                : null;

        // Issue #510 (QA bounce, criterion 3): the Model field must round-trip across a page reload
        // exactly as the agent does. We surface the SAVED model (config.json "brain_model" via
        // BrainModelConfig.Get) - the same value the PUT writes - NOT host.BrainModel (the running
        // brain's model, fixed at host construction). Sourcing the GET from the running brain meant a
        // freshly-saved model was persisted to disk yet never shown back on reload. The saved value is
        // what the user chose; the running brain still picks it up on the next Gateway restart (the
        // documented "applies on next restart" contract for the live process is unchanged).
        var savedModel = Core.Configuration.BrainModelConfig.Get();

        return new
        {
            tool = host.BrainTool.ToString(),
            // The agents registered on this machine, in list order (issue #510): the wingman can
            // run as any of them (the driver-level hostability work landed in issue #509).
            agents = agents.Select(a => new { id = a.Id, displayName = a.DisplayName, type = a.Type.ToString() }).ToArray(),
            agentId = savedAgentId,
            model = savedModel,
            sessionId = brain.SessionId,
            pid = brain.ProcessId,
            alive = health.IsAlive,
            started = !IsNotStarted(health.Status),
            status = health.Status,
            detail = BrainDetail(health),
        };
    }

    /// <summary>
    /// The agents registered on THIS machine, filtered to the enabled entries - exactly the set the
    /// New Session dialog offers (issue #510). Uses <see cref="Core.Configuration.AgentEntryStore.LoadEntries"/>
    /// with the same default <see cref="Core.Configuration.AgentOptions"/> the New Session dialog
    /// falls back to, so the picker mirrors that list (including the one-time legacy seed when
    /// agent.entries has never been written) rather than showing an empty dropdown.
    /// </summary>
    private static List<Core.Configuration.AgentEntry> LoadMachineAgents()
    {
        return Core.Configuration.AgentEntryStore.LoadEntries(new Core.Configuration.AgentOptions())
            .Where(e => e.Enabled)
            .ToList();
    }

    /// <summary>Human-readable one-liner for the brain state. Pure, for tests.</summary>
    public static string BrainDetail(BrainHealth health)
    {
        if (IsNotStarted(health.Status))
            return "not started (spawns on first use)";
        return health.IsAlive
            ? $"alive - {health.ActivityState}, idle {health.IdleSeconds:F0}s, context {health.ContextTokens:N0} tokens"
            : $"DEAD ({health.Status}) - use Restart Brain";
    }

    private static bool IsNotStarted(string status) =>
        string.Equals(status, "NotStarted", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The consolidated AI-provider snapshot the Cockpit AI page renders: the selected provider plus
    /// the models/voice it resolves to. "provider" is the projection of transcription_mode
    /// ("devthrottle" or "openai"); the wingman + transcription models are the provider-correct
    /// values from the one routing spot; the voice + selectable set come from <see cref="Core.Configuration.TtsVoiceConfig"/>.
    /// </summary>
    private static object AiProviderSnapshot()
    {
        var mode = Core.Configuration.TranscriptionModeConfig.Get();
        return new
        {
            provider = ProviderString(mode),
            // The saved wingman-model choice (falls forward to the provider default for a stale/unset
            // value), so a model picked on the AI tab round-trips across a reload.
            wingmanModel = Core.Configuration.WingmanModelConfig.Resolve(mode),
            transcriptionModel = Core.Configuration.TranscriptionEndpointResolver.Resolve(mode).Model,
            ttsModel = Core.Configuration.TtsModelConfig.Resolve(mode),
            ttsVoice = Core.Configuration.TtsVoiceConfig.Resolve(mode),
            voices = Core.Configuration.TtsVoiceConfig.OpenAiVoices,
        };
    }

    /// <summary>The UI provider string for a mode: DevThrottle -> "devthrottle", Byo -> "openai".</summary>
    private static string ProviderString(Core.Configuration.TranscriptionMode mode) =>
        mode == Core.Configuration.TranscriptionMode.DevThrottle ? "devthrottle" : "openai";

    /// <summary>Parse the UI provider string to a transcription mode. False (no-fallback) on anything else.</summary>
    private static bool TryParseProvider(string? value, out Core.Configuration.TranscriptionMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "devthrottle": mode = Core.Configuration.TranscriptionMode.DevThrottle; return true;
            case "openai": mode = Core.Configuration.TranscriptionMode.Byo; return true;
            default: mode = Core.Configuration.TranscriptionMode.DevThrottle; return false;
        }
    }

    private sealed record AiProviderBody(string? Provider);
    private sealed record TtsVoiceBody(string? Voice);
    private sealed record AddressingModeBody(string? Mode);
    private sealed record TranscriptionModeBody(string? Mode);
    private sealed record AutostartBody(bool Enabled);
    private sealed record WingmanBody(bool Enabled);
    private sealed record BrainConfigBody(string? AgentId, string? Tool, string? Model);
}
