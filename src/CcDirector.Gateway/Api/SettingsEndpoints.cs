using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.AgentBrain;
using CcDirector.Core.Configuration;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;
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
///   GET  /gateway/injected-text   -> { use_yours, yours, ours, placeholders[] } (what agents get at launch)
///   PUT  /gateway/injected-text   body { "use_yours": bool, "yours": string|null } -> same shape
///
/// DENIED IN WHOLE ON HOSTED (issue #1863). Every route in this group is refused on a hosted Gateway.
/// These routes operate on PROCESS-GLOBAL configuration with NO TENANT DIMENSION AT ALL: config.json is
/// one file for the whole process, so every write here is a fleet-wide mutation performed by whichever
/// authenticated caller happened to send it, and GET /gateway/injected-text hands back the owner's own
/// custom agent-launch instruction text. On shared hosted infrastructure there is no correct per-tenant
/// answer to serve here - only a leak to close. Self-host is single-tenant and these are legitimate owner
/// function there, so on self-host nothing changes.
///
/// HOW THE DENY IS EXPRESSED - THE SHARED REFUSAL PRIMITIVE, NOT A BESPOKE FILTER. This group is denied
/// through <see cref="HostedRouteDeny.Group"/>, the ONE hosted-refusal boundary every deny family on this
/// Gateway adopts (primitive at <c>src/CcDirector.Gateway/Tenancy/HostedRouteDeny.cs</c>; the key-vault
/// group in <c>VaultEndpoints</c> is the reference adopter). An earlier revision rolled its own
/// <c>AddEndpointFilter</c> deny before the primitive existed; it has been replaced so the release ships
/// ONE refusal boundary, not one per family. What the primitive buys over the old request-time filter: on
/// hosted the handler is NEVER MAPPED - in its place a verb-less refusal is mapped on the route's own
/// pattern, so there is no binding step to get ahead of, no body parameter and no method constraint. EVERY
/// request shape meets the refusal - a valid body, a malformed body, a wrong media type, and a VERB THE
/// ROUTE NEVER MAPPED (which the old filter let endpoint selection answer with a 405, disclosing that a
/// route exists on a Gateway whose refusal says it does not).
///
/// PER-ROUTE MODE (<see cref="HostedRouteDeny.Group"/>), NOT AN EXCLUSIVE PREFIX. The owner-settings routes
/// do not own an exclusive prefix: they are scattered leaves under <c>/gateway</c>, which also carries LIVE
/// routes from other families (<c>/gateway/about</c>, <c>/gateway/governance/*</c>, <c>/gateway/workflows/*</c>,
/// <c>/gateway/wingman/instructions/*</c>, <c>/gateway/missions/notes</c>, <c>/gateway/lists/item-status</c>).
/// An <see cref="HostedRouteDeny.ExclusiveGroup"/> claim over <c>/gateway</c> would take every one of those
/// off the air, and the startup exclusivity check (<see cref="HostedRefusalRouteSpace.ValidateBeforeStart"/>)
/// refuses to boot the Gateway when a live route serves beneath an exclusive prefix - so the exclusive shape
/// is not available here. Per-route mode maps one refusal per route this family declares; the family owes a
/// test that a route added to the group is refused, which is why <c>Map</c> returns the group handle.
/// </summary>
internal static class SettingsEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The single error string the hosted refusal serves. Held here so a test can assert against the
    /// exact string that is served rather than a copy that could drift.</summary>
    internal const string RefusalMessage = "gateway settings are not available on the hosted gateway";

    /// <summary>
    /// The hosted refusal payload for the whole owner-settings group (issue #1863). Validated on
    /// construction, so a blank field fails the Gateway at startup rather than serving a refusal a caller
    /// cannot act on.
    ///
    /// The primitive reads <see cref="GatewayHostedMode.IsHosted"/> DIRECTLY, never an optional argument a
    /// caller can omit - a security branch that depends on an argument fails OPEN the moment somebody forgets
    /// it, which is how the hosted account-status fix nearly shipped a hole. 404 rather than 403: on hosted
    /// "the owner's settings" does not exist as a concept, so "not here" is the truthful answer; 403 would
    /// imply the right credential could reach it, and none can.
    /// </summary>
    private static HostedDenial Denial() => new(
        family: "owner-settings",
        message: RefusalMessage,
        reason: "the owner settings are process-global config.json values with no tenant dimension anywhere - " +
                "not in the file, the store or the routes - and the host-wide auth gate admits any enrolled " +
                "device key from any account, so one subscriber would repoint or read the whole fleet's settings",
        unDenyInstruction: "do NOT simply remove this deny: give each of these settings a per-tenant home " +
                "(config.json has none today), migrate any global value already written, and only then restore " +
                "a tenant-scoped route",
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Maps the owner-settings group through the shared refusal primitive and RETURNS the denied group they
    /// were mapped through, so the refusal can be proved to cover routes that do not exist yet: a test maps a
    /// NEW probe route onto the returned handle and finds it already refused on hosted, with no deny written
    /// for it anywhere. Returning the handle is the only way to state that property from outside this file.
    /// </summary>
    public static HostedDenyGroup Map(IEndpointRouteBuilder outer, GatewayHost host)
    {
        FileLog.Write($"[SettingsEndpoints] mapping the owner settings; hosted={GatewayHostedMode.IsHosted} - on hosted EVERY route in this group is refused via the shared refusal primitive (issue #1863)");

        // The whole group through ONE primitive-created handle, rather than a guard line repeated in every
        // handler. A repeated guard is a thing to forget: the route added to this file next year would be
        // open by default on hosted and nothing would fail. Per-route Group mode maps a refusal for every
        // route mapped through the handle below. The empty prefix keeps every route path written out in full,
        // so the self-host surface is byte-identical to before and the diff stays readable.
        var group = HostedRouteDeny.Group(outer, "", Denial());

        // The telemetry-consent family is mapped onto OUTER, not into this group, deliberately (issue
        // #1863). It carries its OWN hosted refusal, and mapping it here as well would put two boundaries
        // over the same routes - which hides a defect rather than adding safety: deleting either alone would
        // leave the routes still refused, so neither's revert could be attributed to it. One family, one
        // boundary, one thing to revert. It is called HERE, in the only method that still holds `outer`, so
        // that `outer` does not have to stay in scope where the routes are mapped.
        TelemetryConsentEndpoint.Map(outer);

        // THE ROUTES ARE MAPPED WHERE `outer` IS NOT IN SCOPE - deliberately, and this is the only reason
        // MapRoutes exists as a separate method.
        //
        // If the routes were written here beside the group handle, each of the twenty-two could INDIVIDUALLY
        // be mapped onto `outer` instead of onto the denied handle - a one-word edit that bypasses the
        // refusal for that route alone while every other route stays correctly denied. That is twenty-two
        // independently bypassable primitives, each of which would owe its own proof run. Handing the typed
        // handle to a method that never receives the ungrouped builder makes that mistake INEXPRESSIBLE
        // rather than merely unlikely: inside MapRoutes there is nothing to map onto except the denied handle.
        // The bypass count is reduced by design. This is the shape the key-vault deny uses, in VaultEndpoints.
        MapRoutes(group, host);
        return group;
    }

    /// <summary>
    /// The twenty-two owner-settings routes. Takes the denied GROUP HANDLE and nothing else - see the note at
    /// the call site: the ungrouped route builder is deliberately out of scope here so no route can be mapped
    /// around the hosted refusal.
    /// </summary>
    private static void MapRoutes(HostedDenyGroup app, GatewayHost host)
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
                    // ONE derivation rule (owner ruling 2026-07-20): {base}/cockpit via GatewayPublicUrl -
                    // the configured public base in hosted mode, the tailnet front door self-hosted (null
                    // when Tailscale is down). The /gateway/settings cockpit block hands out the SAME URL
                    // as GET /cockpit and /gateway/about, never the raw front-door root it used before.
                    url = GatewayPublicUrl.ResolveCockpit(),
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
                // The lengths every Snooze menu offers beside that default, and the cap on how many
                // there may be, so the Settings page can render the list and disable "Add" when full.
                snoozePresets = Core.Configuration.SnoozePresetsConfig.Get(),
                snoozeMaxPresets = Core.Configuration.SnoozePresetsConfig.MaxPresets,
                // The display time zone (IANA id) the private dashboards' hourly charts read local hours
                // in. Auto-defaults to this Gateway machine's own zone when unset; machineDefault lets the
                // Settings page show what "automatic" resolves to.
                timeZone = Core.Configuration.TimeZoneConfig.Get(),
                timeZoneMachineDefault = Core.Configuration.TimeZoneConfig.MachineDefault(),
            });
        });

        // Restart the warm brain (issue #184): the one recovery verb, and doubles as a manual
        // start. Mirrors the old tray-window Restart Brain button, now reachable from the Cockpit.
        app.MapPost("/gateway/brain/restart", async (CancellationToken ct) =>
        {
            FileLog.Write("[SettingsEndpoints] POST /gateway/brain/restart");
            try
            {
                // Through the host's restart seam rather than host.Brain.RestartAsync directly. In
                // production the seam IS host.Brain.RestartAsync - see GatewayHost.BrainRestartAction -
                // so behaviour here is unchanged; the indirection is what lets a test drive this exact
                // path and verb for a real receipt without starting a coding-agent process.
                await host.BrainRestartAction(ct);
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

        // The fleet-wide richer-usage-telemetry consent (issue #649) lives in its own endpoint class
        // (TelemetryConsentEndpoint) so it can be unit-tested in isolation, and it is mapped from Map above
        // rather than here, because it needs the UNGROUPED builder and this method deliberately does not
        // have one. Default ON; gates only the richer usage telemetry - the always-on login/startup events
        // are never gated.

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
        // snooze with no Gateway restart. This is the length the plain one-click Snooze uses; the other
        // lengths a menu may offer beside it are /gateway/snooze-presets below.
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

                // Goes through SnoozePresetsConfig, not SnoozeDefaultConfig.Set, so the default can never
                // end up being a length the Snooze menu does not offer: a length that is not on the menu
                // is added to it. Throws (fail loud) when the menu is already full - only the user can say
                // which length to drop.
                Core.Configuration.SnoozePresetsConfig.SetDefault(minutes);
                FileLog.Write($"[SettingsEndpoints] snooze_default_minutes set to {minutes}");
                return Results.Json(new { minutes });
            }
            catch (InvalidOperationException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/snooze-default rejected: {ex.Message}");
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/snooze-default bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // The injected text: the whole of what DevThrottle puts in front of an agent at the start of a
        // session, and the user's choice to run their own words instead of ours. Gateway-owned so the
        // choice is the same on every machine the user runs a Director on; each Director downloads and
        // caches it (Sessions.InjectedTextStore) and injects it at launch. The GET carries "ours" (the
        // shipped default, always current) so the Cockpit can show the default even while a custom
        // version is live, and the placeholder tokens so the page can list what stays editable.
        app.MapGet("/gateway/injected-text", () =>
        {
            var s = Core.Configuration.InjectedTextConfig.Get();
            return Results.Json(new
            {
                use_yours = s.UseYours,
                yours = s.Yours,
                ours = Core.Configuration.InjectedTextConfig.Ours,
                placeholders = Core.Sessions.FleetPreamblePlaceholders.All,
            });
        });

        app.MapPut("/gateway/injected-text", async (HttpContext ctx) =>
        {
            try
            {
                var node = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                if (node is not JsonObject obj)
                    return Results.BadRequest(new { error = "body { \"use_yours\": bool, \"yours\": string|null } is required" });

                // Both fields are REQUIRED, and absent carries a different meaning from present-but-null for
                // this feature, so a partial body is rejected rather than defaulted. Defaulting a missing
                // use_yours to false would flip whose text is live; defaulting a missing yours to null would
                // erase the user's saved text. The client always sends the full desired state.
                if (!obj.ContainsKey("use_yours"))
                    return Results.BadRequest(new { error = "use_yours is required (true or false)" });
                if (!obj.ContainsKey("yours"))
                    return Results.BadRequest(new { error = "yours is required (a string, or null to clear it - not absent)" });

                if (obj["use_yours"] is not JsonValue uv
                    || uv.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                    return Results.BadRequest(new { error = "use_yours must be true or false" });
                var useYours = uv.GetValue<bool>();

                string? yours;
                var yoursNode = obj["yours"];
                if (yoursNode is null)
                    yours = null;
                else if (yoursNode is JsonValue yv && yv.GetValueKind() == JsonValueKind.String)
                    yours = yv.GetValue<string>();
                else
                    return Results.BadRequest(new { error = "yours must be a string or null" });

                var settings = new Core.Configuration.InjectedTextSettings(useYours, yours);

                // Validate before writing, and reject rather than store: a template that cannot render
                // must never reach a Director, because the failure would land on agents at launch instead
                // of on the person editing it here.
                var problem = Core.Configuration.InjectedTextConfig.Validate(settings);
                if (problem is not null)
                {
                    FileLog.Write($"[SettingsEndpoints] PUT /gateway/injected-text rejected: {problem}");
                    return Results.BadRequest(new { error = problem });
                }

                Core.Configuration.InjectedTextConfig.Set(settings);
                FileLog.Write($"[SettingsEndpoints] injected_text set: use_yours={settings.UseYours}, has_yours={settings.Yours is not null}");
                return Results.Json(new
                {
                    use_yours = settings.UseYours,
                    yours = settings.Yours,
                    ours = Core.Configuration.InjectedTextConfig.Ours,
                    placeholders = Core.Sessions.FleetPreamblePlaceholders.All,
                });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/injected-text bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // The per-user list of snooze lengths every Snooze menu offers, and which of them is the
        // default. Gateway-owned like the default above, so the same lengths appear on the desktop, the
        // phone, and in the Cockpit. The list and its default are written together in ONE call because
        // they have an invariant between them - the default must be one of the lengths - and separate
        // writes would let a half-applied change break it.
        app.MapGet("/gateway/snooze-presets", () =>
            Results.Json(new
            {
                presets = Core.Configuration.SnoozePresetsConfig.Get(),
                defaultMinutes = Core.Configuration.SnoozeDefaultConfig.Get(),
                maxPresets = Core.Configuration.SnoozePresetsConfig.MaxPresets,
            }));

        app.MapPut("/gateway/snooze-presets", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<SnoozePresetsBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body?.Presets is not { } presets || body.DefaultMinutes is not int defaultMinutes)
                    return Results.BadRequest(new
                    {
                        error = "body { \"presets\": [<whole minutes>], \"defaultMinutes\": <whole minutes> } is required",
                    });

                if (!Core.Configuration.SnoozePresetsConfig.IsValidSet(presets, defaultMinutes, out var invalid))
                    return Results.BadRequest(new { error = invalid });

                Core.Configuration.SnoozePresetsConfig.Set(presets, defaultMinutes);
                FileLog.Write($"[SettingsEndpoints] snooze_presets set to [{string.Join(", ", presets)}], default {defaultMinutes}");
                return Results.Json(new
                {
                    presets = Core.Configuration.SnoozePresetsConfig.Get(),
                    defaultMinutes,
                    maxPresets = Core.Configuration.SnoozePresetsConfig.MaxPresets,
                });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/snooze-presets bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // The display time zone (an IANA id) the private dashboards' hourly charts read local hours in.
        // Auto-defaults to this Gateway machine's own zone; read at render time so a change applies to the
        // next refresh with no restart. GET also reports the machine default so the page can show what
        // "automatic" resolves to.
        app.MapGet("/gateway/time-zone", () =>
            Results.Json(new
            {
                timeZone = Core.Configuration.TimeZoneConfig.Get(),
                machineDefault = Core.Configuration.TimeZoneConfig.MachineDefault(),
            }));

        app.MapPut("/gateway/time-zone", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<TimeZoneBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.TimeZone))
                    return Results.BadRequest(new { error = "body { \"timeZone\": \"America/New_York\" } is required" });
                if (!Core.Configuration.TimeZoneConfig.IsValid(body.TimeZone))
                    return Results.BadRequest(new { error = "timeZone must be a valid IANA time zone id" });

                Core.Configuration.TimeZoneConfig.Set(body.TimeZone);
                var value = body.TimeZone.Trim();
                FileLog.Write($"[SettingsEndpoints] time_zone set to {value}");
                return Results.Json(new { timeZone = value });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/time-zone bad JSON: {ex.Message}");
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
            // Car Mode's hands-free sign-off phrase, a Gateway setting so the Cockpit can set it and the
            // phone (where Car Mode runs) picks it up. Default "over and out".
            carModeEndPhrase = Core.Configuration.CarModeEndPhraseConfig.Get(),
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
    private sealed record SnoozePresetsBody(int[]? Presets, int? DefaultMinutes);
    private sealed record TimeZoneBody(string? TimeZone);
    private sealed record WingmanBody(bool Enabled);
    private sealed record BrainConfigBody(string? AgentId, string? Tool, string? Model);
}
