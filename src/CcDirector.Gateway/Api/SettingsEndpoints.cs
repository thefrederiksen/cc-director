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
///   GET  /gateway/transcription-mode -> { mode } ("devthrottle")
///   PUT  /gateway/transcription-mode body { "mode": "devthrottle" } -> { mode }
///   GET  /gateway/ai-provider     -> { provider, wingmanModel, transcriptionModel, ttsVoice, voices[] }
///   PUT  /gateway/ai-provider     body { "provider": "devthrottle" } (resets hosted model defaults)
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
                // Snooze Length mission: the per-user default snooze length in minutes (default 60),
                // so the one Settings page can render and edit it in Phase 3.
                snoozeDefaultMinutes = Core.Configuration.SnoozeDefaultConfig.Get(),
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

        // The per-user default snooze length in minutes (Snooze Length mission,
        // docs/architecture/snooze-length-mission-2026-07-11.md). One value for the whole account:
        // because every device talks to this one Gateway, this Gateway-owned setting IS "the same
        // snooze length across all my devices". Read at snooze time, so a change applies to the next
        // snooze with no Gateway restart. There is no per-snooze duration by design - this is the one
        // length every Snooze button uses.
        app.MapGet("/gateway/snooze-default", () =>
            Results.Json(new { minutes = Core.Configuration.SnoozeDefaultConfig.Get() }));

        app.MapPut("/gateway/snooze-default", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<SnoozeDefaultBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body?.Minutes is not int minutes)
                    return Results.BadRequest(new { error = "body { \"minutes\": <whole minutes> } is required" });
                if (!Core.Configuration.SnoozeDefaultConfig.IsValid(minutes))
                    return Results.BadRequest(new { error = $"minutes must be between {Core.Configuration.SnoozeDefaultConfig.MinMinutes} and {Core.Configuration.SnoozeDefaultConfig.MaxMinutes}" });

                Core.Configuration.SnoozeDefaultConfig.Set(minutes);
                FileLog.Write($"[SettingsEndpoints] snooze_default_minutes set to {minutes}");
                return Results.Json(new { minutes });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/snooze-default bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // Transcription mode: DevThrottle hosted transcription is the only supported production
        // capability. Legacy "local", "byo", and "openai" values are migrated by the config parser
        // when read, but this API only accepts the current value.
        app.MapGet("/gateway/transcription-mode", () =>
            Results.Json(new { mode = Core.Configuration.TranscriptionModeConfig.Get().ToConfigString() }));

        app.MapPut("/gateway/transcription-mode", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<TranscriptionModeBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Mode))
                    return Results.BadRequest(new { error = "body { \"mode\": \"devthrottle\" } is required" });

                if (!string.Equals(body.Mode.Trim(), Core.Configuration.TranscriptionMode.DevThrottle.ToConfigString(), StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "mode must be \"devthrottle\"" });

                var mode = Core.Configuration.TranscriptionMode.DevThrottle;

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

        // The consolidated AI provider. DevThrottle is the only selectable provider; GET returns the
        // derived wingman + transcription models, the TTS voice, and the selectable fallback voices.
        // PUT keeps the endpoint for older clients but accepts only "devthrottle" and resets hosted
        // model defaults atomically.
        app.MapGet("/gateway/ai-provider", () => Results.Json(AiProviderSnapshot()));

        app.MapPut("/gateway/ai-provider", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<AiProviderBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Provider))
                    return Results.BadRequest(new { error = "body { \"provider\": \"devthrottle\" } is required" });
                if (!string.Equals(body.Provider.Trim(), "devthrottle", StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "provider must be \"devthrottle\"" });

                // Reset the wingman model, speech model, and voice to the hosted defaults.
                var mode = Core.Configuration.TranscriptionMode.DevThrottle;
                var wingmanModel = Core.Configuration.TranscriptionEndpointResolver.ResolveWingman(mode).Model;
                var wingmanFastModel = Core.Configuration.TranscriptionEndpointResolver.ResolveWingmanFast(mode).Model;
                var ttsModel = Core.Configuration.TranscriptionEndpointResolver.DefaultTtsModel(mode);
                var ttsVoice = Core.Configuration.TranscriptionEndpointResolver.DefaultTtsVoice(mode);
                Core.Configuration.CcDirectorConfigService.MergePatch(
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["transcription_mode"] = mode.ToConfigString(),
                        ["brain_model"] = wingmanModel,
                        ["brain_model_fast"] = wingmanFastModel,
                        ["tts_model"] = ttsModel,
                        ["tts_voice"] = ttsVoice,
                    });
                FileLog.Write($"[SettingsEndpoints] ai_provider set: mode={mode.ToConfigString()}, wingmanModel={wingmanModel}, wingmanFastModel={wingmanFastModel}, ttsModel={ttsModel}, ttsVoice={ttsVoice}");
                return Results.Json(AiProviderSnapshot());
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/ai-provider bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // The text-to-speech voice for spoken wingman output (consolidated AI settings). Read at
        // synthesis time, so a change is honored on the next spoken summary.
        app.MapGet("/gateway/tts-voice", () =>
        {
            var mode = Core.Configuration.TranscriptionModeConfig.Get();
            return Results.Json(new
            {
                voice = Core.Configuration.TtsVoiceConfig.Resolve(mode),
                voices = Core.Configuration.TtsVoiceConfig.FallbackVoices,
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
    /// the models/voice it resolves to. "provider" is always "devthrottle"; the wingman +
    /// transcription models come from the one routing spot; the voice + fallback selectable set come
    /// from <see cref="Core.Configuration.TtsVoiceConfig"/>.
    /// </summary>
    private static object AiProviderSnapshot()
    {
        var mode = Core.Configuration.TranscriptionModeConfig.Get();
        return new
        {
            provider = "devthrottle",
            // The saved wingman-model choices fall forward to provider defaults for stale/unset values,
            // so models picked on the AI tab round-trip across a reload.
            wingmanModel = Core.Configuration.WingmanModelConfig.Resolve(mode),
            wingmanFastModel = Core.Configuration.WingmanModelConfig.ResolveFast(mode),
            // Car Mode runs its OWN model, separate from the Wingman (a fast tier + tool_choice=required).
            // The snapshot shows the user's saved setting (or the Qwen2.5-72B default); the env override
            // is not reflected here because it is a per-install debug switch, not the user's choice.
            carModeModel = Core.Configuration.CarModeModelConfig.Get(),
            transcriptionModel = Core.Configuration.TranscriptionEndpointResolver.Resolve(mode).Model,
            ttsModel = Core.Configuration.TtsModelConfig.Resolve(mode),
            ttsVoice = Core.Configuration.TtsVoiceConfig.Resolve(mode),
            voices = Core.Configuration.TtsVoiceConfig.FallbackVoices,
        };
    }

    private sealed record AiProviderBody(string? Provider);
    private sealed record TtsVoiceBody(string? Voice);
    private sealed record AddressingModeBody(string? Mode);
    private sealed record TranscriptionModeBody(string? Mode);
    private sealed record AutostartBody(bool Enabled);
    private sealed record SnoozeDefaultBody(int? Minutes);
    private sealed record WingmanBody(bool Enabled);
    private sealed record BrainConfigBody(string? AgentId, string? Tool, string? Model);
}
