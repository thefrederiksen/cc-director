using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.AgentBrain;
using CcDirector.Core.Configuration;
using CcDirector.Core.Network;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Gateway settings surface that backs the one Cockpit Settings page
/// (docs/architecture/gateway/SETTINGS_OWNERSHIP.md). One GET assembles the per-account snapshot the
/// page renders; the actions mutate Gateway-owned state. Inherits the host-wide token middleware,
/// same as every other endpoint here.
///
///   SERVE ON HOSTED (per-account, resolve the caller tenant, 403 if unresolved):
///   GET  /gateway/settings        -> { snoozeDefaultMinutes, snoozePresets, snoozeMaxPresets,
///                                       timeZone, timeZoneMachineDefault }
///   GET+PUT /gateway/snooze-default, /gateway/snooze-presets, /gateway/time-zone
///   GET  /gateway/ai-provider     -> { provider, wingmanModel, wingmanFastModel, carModeModel,
///                                       carModeEndPhrase, transcriptionModel, ttsModel, ttsVoice, voices[],
///                                       catalogAvailable }
///   PUT  /gateway/ai-provider     body { "provider": "devthrottle" } (resets this tenant's model defaults)
///   GET+PUT /gateway/tts-voice
///   GET+PUT /gateway/injected-text              (per-account agent-launch text; issue #2057)
///
///   DENIED ON HOSTED (process-global, no tenant dimension):
///   GET+PUT /gateway/transcription-mode         (single-valued process-global provider fact)
///
/// ISSUE #2022 - THE MACHINE SETTINGS LEFT THIS SURFACE, and the per-account deny was RETIRED. The "This
/// machine" tab was retired (diagnostics + address + version to the About page; autostart to the installer +
/// the `cc-devthrottle autostart` command; addressing dropped; brain restart/config removed). AND, with the
/// runtime consumers now tenant-threaded (issue #2017 runtime threading), the per-account routes above no
/// longer refuse on hosted: they SERVE, each resolving the caller's tenant and answering 403 on an unresolved
/// identity - never the Local partition. So self-host IS the hosted Gateway with one tenant on this surface.
///
/// STILL DENIED ON HOSTED (issue #1863). The remaining process-global route has NO TENANT DIMENSION AT ALL:
/// config.json is one file for the whole process, so a write to transcription mode is a fleet-wide mutation
/// performed by whichever authenticated caller sent it, and there is one single-valued provider fact with no
/// correct per-tenant answer to serve - only a leak to close, so it refuses. (Injected text WAS here; it moved
/// to a per-account home in issue #2057 and now serves per-tenant.) Self-host is single-tenant and this is a
/// legitimate owner function there, so on self-host nothing changes.
/// The AI model CATALOG and test-chat (AiModelsEndpoint) also stay denied on hosted - they spend the shared
/// deployment provider credential with no per-caller scoping (see that file); the AI tab reads the Gateway-owned
/// catalogAvailable flag on the ai-provider snapshot to disable browsing/Test on hosted rather than fail.
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

    /// <summary>The 403 a per-tenant settings route answers when the caller's tenant cannot be resolved (issue
    /// #2017). On the hosted Gateway an authenticated request whose device key has no bound tenant is refused,
    /// NEVER served the Local partition (that would be a wrong-tenant read). Self-host always resolves to Local,
    /// so this never fires there.</summary>
    private static IResult TenantRequired()
        => Results.Json(new { error = "a tenant could not be resolved for this request" },
            statusCode: StatusCodes.Status403Forbidden);

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
    /// Maps the owner-settings surface, split into two groups by issue #2022's deny retirement, and RETURNS
    /// the still-denied group handle so the refusal can be proved to cover routes that do not exist yet: a test
    /// maps a NEW probe route onto the returned handle and finds it already refused on hosted, with no deny
    /// written for it anywhere.
    ///
    /// TWO PARTITIONS, ONE PER SAFETY PROPERTY:
    ///  - The PER-ACCOUNT routes serve on hosted, so they are mapped onto <paramref name="outer"/> in
    ///    <see cref="MapServedRoutes"/>. Each resolves the CALLER's tenant with <c>ResolveReadTenant</c> and
    ///    answers 403 when none resolves - NEVER the Local partition - so they are safe on shared infrastructure
    ///    without a deny: a request that cannot be attributed to a tenant is refused, not served a wrong one.
    ///  - The remaining process-global route (transcription mode) has no per-tenant home and STAYS denied on
    ///    hosted, mapped onto the group handle in <see cref="MapDeniedRoutes"/>.
    ///    That method receives ONLY the handle - <paramref name="outer"/> is out of scope there - so a denied
    ///    route cannot be moved onto the ungrouped builder by a one-word edit; changing its group means changing
    ///    the method signature, which moves them all. That is the un-bypassability the deny primitive buys.
    /// </summary>
    public static HostedDenyGroup Map(IEndpointRouteBuilder outer, GatewayHost host)
    {
        FileLog.Write($"[SettingsEndpoints] mapping the owner settings; hosted={GatewayHostedMode.IsHosted} - per-account routes serve; machine/global routes are refused via the shared refusal primitive (issues #1863, #2022)");

        var group = HostedRouteDeny.Group(outer, "", Denial());

        // The per-account routes serve on hosted (issue #2022). They take the ungrouped builder because they
        // are NOT denied; their fail-closed is ResolveReadTenant (403 on an unresolved tenant), not a deny.
        MapServedRoutes(outer, host);

        // The machine/process-global routes stay denied. They take ONLY the group handle - `outer` is out of
        // scope inside MapDeniedRoutes - so none of them can be mapped around the refusal by an individual edit.
        MapDeniedRoutes(group, host);
        return group;
    }

    /// <summary>
    /// The per-account owner-settings routes, which SERVE on hosted (issue #2022). Takes the ungrouped builder:
    /// these are safe on shared infrastructure because every one resolves the caller's tenant and answers 403
    /// when none resolves, never falling back to Local. Adding a route here that is NOT per-account is the
    /// mistake to avoid - it would then serve a machine/global value to any tenant; such a route belongs in
    /// <see cref="MapDeniedRoutes"/> instead.
    /// </summary>
    private static void MapServedRoutes(IEndpointRouteBuilder app, GatewayHost host)
    {
        app.MapGet("/gateway/settings", (HttpContext ctx) =>
        {
            // Per-account settings (issue #2017/#2022): snooze and time zone are read for the CALLER's tenant,
            // never a global value, and this route SERVES on hosted (the deny retirement landed). On self-host
            // the tenant is Local (behaviour unchanged). A request with no resolvable tenant is refused (403),
            // never served the Local partition.
            //
            // Issue #2022: the machine settings LEFT the Cockpit Settings page, so this snapshot no longer
            // carries process diagnostics (version, state, port, uptime, directors, mode), the cockpit block,
            // the brain block, the autostart state, or the network addressing mode. Those read-only facts now
            // live on the About page (GET /gateway/about); the removed machine ENDPOINTS are gone. What remains
            // here is exactly the per-account settings the collapsed page renders.
            var t = GatewayEndpoints.ResolveReadTenant(ctx, host.TenantBoundary);
            if (t is null) return TenantRequired();

            return Results.Json(new
            {
                // Snooze Length mission: the per-user default snooze length in minutes (default 60), read for
                // this tenant (issue #2017) - the tenant override else the operator global default.
                snoozeDefaultMinutes = host.TenantSettingsResolver.SnoozeDefaultMinutes(t.Value),
                // The lengths every Snooze menu offers beside that default, and the cap on how many
                // there may be, so the Settings page can render the list and disable "Add" when full.
                snoozePresets = host.TenantSettingsResolver.SnoozePresets(t.Value),
                snoozeMaxPresets = Core.Configuration.SnoozePresetsConfig.MaxPresets,
                // The display time zone (IANA id) the private dashboards' hourly charts read local hours
                // in, read for this tenant (issue #2017). Auto-defaults to this Gateway machine's own zone
                // when unset; machineDefault (a machine fact, not per-tenant) lets the page show what
                // "automatic" resolves to.
                timeZone = host.TenantSettingsResolver.TimeZone(t.Value),
                timeZoneMachineDefault = Core.Configuration.TimeZoneConfig.MachineDefault(),
            });
        });

        // The per-user default snooze length in minutes (Snooze Length mission,
        // docs/architecture/snooze-length-mission-2026-07-11.md). One value for the whole account:
        // because every device talks to this one Gateway, this Gateway-owned setting IS "the same
        // snooze length across all my devices". Read at snooze time, so a change applies to the next
        // snooze with no Gateway restart. This is the length the plain one-click Snooze uses; the other
        // lengths a menu may offer beside it are /gateway/snooze-presets below.
        app.MapGet("/gateway/snooze-default", (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, host.TenantBoundary);
            if (t is null) return TenantRequired();
            return Results.Json(new { minutes = host.TenantSettingsResolver.SnoozeDefaultMinutes(t.Value) });
        });

        app.MapPut("/gateway/snooze-default", async (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, host.TenantBoundary);
            if (t is null) return TenantRequired();
            try
            {
                var body = await JsonSerializer.DeserializeAsync<SnoozeDefaultBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body?.Minutes is not int minutes)
                    return Results.BadRequest(new { error = "body { \"minutes\": <whole minutes> } is required" });
                if (!Core.Configuration.SnoozeDefaultConfig.IsValid(minutes))
                    return Results.BadRequest(new { error = $"minutes must be between {Core.Configuration.SnoozeDefaultConfig.MinMinutes} and {Core.Configuration.SnoozeDefaultConfig.MaxMinutes}" });

                // Set the default for THIS tenant (issue #2017), holding the same invariant the global setter
                // holds: the default is written together with a presets list that includes it, so the default
                // can never be a length the Snooze menu does not offer. The resolver adds it to the tenant's
                // presets when missing (fail loud when the tenant's menu is already full - only the user can
                // say which length to drop).
                var presets = host.TenantSettingsResolver.SnoozePresets(t.Value).ToList();
                if (!presets.Contains(minutes))
                {
                    if (presets.Count >= Core.Configuration.SnoozePresetsConfig.MaxPresets)
                        return Results.BadRequest(new { error = $"the snooze menu already has its maximum {Core.Configuration.SnoozePresetsConfig.MaxPresets} lengths; remove one before setting a new default not already on it" });
                    presets.Add(minutes);
                }
                host.TenantSettingsResolver.SetSnoozePresets(t.Value, presets, minutes, DateTime.UtcNow);
                FileLog.Write($"[SettingsEndpoints] snooze_default_minutes set to {minutes} for tenant={t.Value.ToLogString()}");
                return Results.Json(new { minutes });
            }
            catch (ArgumentException ex)
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

        // The per-user list of snooze lengths every Snooze menu offers, and which of them is the
        // default. Gateway-owned like the default above, so the same lengths appear on the desktop, the
        // phone, and in the Cockpit. The list and its default are written together in ONE call because
        // they have an invariant between them - the default must be one of the lengths - and separate
        // writes would let a half-applied change break it.
        app.MapGet("/gateway/snooze-presets", (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, host.TenantBoundary);
            if (t is null) return TenantRequired();
            return Results.Json(new
            {
                presets = host.TenantSettingsResolver.SnoozePresets(t.Value),
                defaultMinutes = host.TenantSettingsResolver.SnoozeDefaultMinutes(t.Value),
                maxPresets = Core.Configuration.SnoozePresetsConfig.MaxPresets,
            });
        });

        app.MapPut("/gateway/snooze-presets", async (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, host.TenantBoundary);
            if (t is null) return TenantRequired();
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

                // Persist BOTH for this tenant (issue #2017), holding the default-is-on-the-menu invariant.
                host.TenantSettingsResolver.SetSnoozePresets(t.Value, presets, defaultMinutes, DateTime.UtcNow);
                FileLog.Write($"[SettingsEndpoints] snooze_presets set to [{string.Join(", ", presets)}], default {defaultMinutes} for tenant={t.Value.ToLogString()}");
                return Results.Json(new
                {
                    presets = host.TenantSettingsResolver.SnoozePresets(t.Value),
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
        app.MapGet("/gateway/time-zone", (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, host.TenantBoundary);
            if (t is null) return TenantRequired();
            return Results.Json(new
            {
                timeZone = host.TenantSettingsResolver.TimeZone(t.Value),
                // machineDefault is a machine fact, not per-tenant - what "automatic" resolves to.
                machineDefault = Core.Configuration.TimeZoneConfig.MachineDefault(),
            });
        });

        app.MapPut("/gateway/time-zone", async (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, host.TenantBoundary);
            if (t is null) return TenantRequired();
            try
            {
                var body = await JsonSerializer.DeserializeAsync<TimeZoneBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.TimeZone))
                    return Results.BadRequest(new { error = "body { \"timeZone\": \"America/New_York\" } is required" });
                if (!Core.Configuration.TimeZoneConfig.IsValid(body.TimeZone))
                    return Results.BadRequest(new { error = "timeZone must be a valid IANA time zone id" });

                var value = body.TimeZone.Trim();
                host.TenantSettingsResolver.SetTimeZone(t.Value, value, DateTime.UtcNow);
                FileLog.Write($"[SettingsEndpoints] time_zone set to {value} for tenant={t.Value.ToLogString()}");
                return Results.Json(new { timeZone = value });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/time-zone bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // The consolidated AI provider. DevThrottle is the only selectable provider; GET returns the
        // derived wingman + transcription models, the TTS voice, and the selectable fallback voices.
        // PUT keeps the endpoint for older clients but accepts only "devthrottle" and resets hosted
        // model defaults atomically.
        app.MapGet("/gateway/ai-provider", (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, host.TenantBoundary);
            if (t is null) return TenantRequired();
            return Results.Json(AiProviderSnapshot(host, t.Value));
        });

        app.MapPut("/gateway/ai-provider", async (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, host.TenantBoundary);
            if (t is null) return TenantRequired();
            try
            {
                var body = await JsonSerializer.DeserializeAsync<AiProviderBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Provider))
                    return Results.BadRequest(new { error = "body { \"provider\": \"devthrottle\" } is required" });
                if (!string.Equals(body.Provider.Trim(), "devthrottle", StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "provider must be \"devthrottle\"" });

                // Reset THIS tenant's wingman/speech model and voice to the provider defaults by clearing the
                // tenant overrides (issue #2017). transcription_mode is a global, single-valued provider fact
                // (one hosted option), so it is still reset in the operator config where it lives.
                Core.Configuration.CcDirectorConfigService.MergePatch(
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["transcription_mode"] = Core.Configuration.TranscriptionMode.DevThrottle.ToConfigString(),
                    });
                host.TenantSettingsResolver.ClearAiProviderOverrides(t.Value);
                FileLog.Write($"[SettingsEndpoints] ai_provider reset to defaults for tenant={t.Value.ToLogString()}");
                return Results.Json(AiProviderSnapshot(host, t.Value));
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/ai-provider bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // The text-to-speech voice for spoken wingman output (consolidated AI settings). Read at
        // synthesis time, so a change is honored on the next spoken summary.
        app.MapGet("/gateway/tts-voice", (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, host.TenantBoundary);
            if (t is null) return TenantRequired();
            var mode = Core.Configuration.TranscriptionModeConfig.Get();
            return Results.Json(new
            {
                voice = host.TenantSettingsResolver.TtsVoice(t.Value, mode),
                voices = Core.Configuration.TtsVoiceConfig.FallbackVoices,
            });
        });

        app.MapPut("/gateway/tts-voice", async (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, host.TenantBoundary);
            if (t is null) return TenantRequired();
            try
            {
                var body = await JsonSerializer.DeserializeAsync<TtsVoiceBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null || string.IsNullOrWhiteSpace(body.Voice))
                    return Results.BadRequest(new { error = "body { \"voice\": \"<id>\" } is required" });

                // Any non-empty voice id is accepted - the catalog is dynamic and provider-specific, so
                // there is no fixed allow-list to check against. Stored for THIS tenant (issue #2017).
                host.TenantSettingsResolver.SetTtsVoice(t.Value, body.Voice, DateTime.UtcNow);
                var voice = body.Voice.Trim();
                FileLog.Write($"[SettingsEndpoints] tts_voice set to {voice} for tenant={t.Value.ToLogString()}");
                return Results.Json(new { voice });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[SettingsEndpoints] PUT /gateway/tts-voice bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        // The injected text: what DevThrottle puts in front of an agent at the start of a session, and the
        // user's choice to run their own words instead of ours. PER-ACCOUNT on hosted (issue #2057): each
        // tenant has its own injected text, stored in the per-tenant settings store and served here for the
        // CALLER's tenant - the same text the caller's own Directors download from this route to inject at
        // launch, so a per-tenant read here is a per-tenant launch. A request with no resolvable tenant is
        // refused (403), never served the Local partition. On self-host the tenant is Local and the resolver
        // falls back to the existing config.json value, so nothing changes there.
        app.MapGet("/gateway/injected-text", (HttpContext ctx) =>
        {
            var t = GatewayEndpoints.ResolveReadTenant(ctx, host.TenantBoundary);
            if (t is null) return TenantRequired();
            var s = host.TenantSettingsResolver.InjectedText(t.Value);
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
            var t = GatewayEndpoints.ResolveReadTenant(ctx, host.TenantBoundary);
            if (t is null) return TenantRequired();
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

                // Validate before writing, and reject rather than store: a template that cannot render must
                // never reach a Director, because the failure would land on agents at launch instead of on the
                // person editing it here.
                var problem = Core.Configuration.InjectedTextConfig.Validate(settings);
                if (problem is not null)
                {
                    FileLog.Write($"[SettingsEndpoints] PUT /gateway/injected-text rejected: {problem}");
                    return Results.BadRequest(new { error = problem });
                }

                host.TenantSettingsResolver.SetInjectedText(t.Value, settings, DateTime.UtcNow);
                FileLog.Write($"[SettingsEndpoints] injected_text set for tenant={t.Value.ToLogString()}: use_yours={settings.UseYours}, has_yours={settings.Yours is not null}");
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

        // Issue #2022: the per-user autostart Run-key toggle (PUT /gateway/autostart) left this surface
        // entirely. Start-at-login is now the installer default plus the `cc-devthrottle autostart
        // on|off|status` command (the one home that works on a headless Linux server too, where a settings
        // page cannot); the tray/menu-bar toggle stays as an optional desktop convenience. There is no
        // machine-scoped autostart endpoint left here for a settings page to call.
    }

    /// <summary>
    /// The process-global owner-settings route that STAYS denied on hosted (issue #2022). Takes the denied
    /// GROUP HANDLE and nothing else - the ungrouped builder is deliberately out of scope here so no route can
    /// be mapped around the hosted refusal. Transcription mode has NO per-tenant home: it is a single-valued
    /// process-global provider fact, so on shared hosted infrastructure there is no correct per-tenant answer
    /// to serve and it refuses rather than leak a fleet-wide value. (Injected text used to be denied here too;
    /// it moved to a per-account home and now serves in <see cref="MapServedRoutes"/> - issue #2057.)
    /// </summary>
    private static void MapDeniedRoutes(HostedDenyGroup app, GatewayHost host)
    {
        _ = host; // the denied handler reads process-global config, not per-tenant host state; kept for symmetry.

        // Transcription mode: DevThrottle hosted transcription is the only supported production capability.
        // A single-valued process-global provider fact with no tenant dimension - denied on hosted; the AI tab
        // reads the resolved transcription MODEL through the per-account ai-provider snapshot, never this route.
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
    }

    /// <summary>
    /// The consolidated AI-provider snapshot the Cockpit AI page renders: the selected provider plus
    /// the models/voice it resolves to. "provider" is always "devthrottle"; the wingman +
    /// transcription models come from the one routing spot; the voice + fallback selectable set come
    /// from <see cref="Core.Configuration.TtsVoiceConfig"/>.
    /// </summary>
    private static object AiProviderSnapshot(GatewayHost host, TenantId tenant)
    {
        var mode = Core.Configuration.TranscriptionModeConfig.Get();
        var resolver = host.TenantSettingsResolver;
        return new
        {
            provider = "devthrottle",
            // The per-tenant model/voice choices (issue #2017), each the tenant's override else the operator
            // default, so models picked on the AI tab round-trip across a reload for THIS tenant.
            wingmanModel = resolver.WingmanModel(tenant, mode, Core.Configuration.WingmanModelRole.Thinking),
            wingmanFastModel = resolver.WingmanModel(tenant, mode, Core.Configuration.WingmanModelRole.Fast),
            // Car Mode runs its OWN model, separate from the Wingman (a fast tier + tool_choice=required).
            carModeModel = resolver.CarModeModel(tenant),
            // Car Mode's hands-free sign-off phrase, per tenant. Default "over and out".
            carModeEndPhrase = resolver.CarModeEndPhrase(tenant),
            // transcriptionModel + voices are provider-level facts (one hosted option), not per-tenant.
            transcriptionModel = Core.Configuration.TranscriptionEndpointResolver.Resolve(mode).Model,
            ttsModel = resolver.TtsModel(tenant, mode),
            ttsVoice = resolver.TtsVoice(tenant, mode),
            voices = Core.Configuration.TtsVoiceConfig.FallbackVoices,
            // Issue #2022: whether the live model CATALOG and the Test button are available. The catalog
            // (/gateway/ai/models) and test-chat (/gateway/ai/test-chat) spend the shared deployment provider
            // credential with no per-caller scoping, so they STAY denied on hosted until that credential is
            // scoped per account. The AI/Car Mode tabs read this Gateway-owned flag (never guess from the
            // surface) to disable model browsing + Test on hosted and show a concise explanation, rather than
            // offer a control that would fail. On self-host the catalog is available.
            catalogAvailable = !GatewayHostedMode.IsHosted,
        };
    }

    private sealed record AiProviderBody(string? Provider);
    private sealed record TtsVoiceBody(string? Voice);
    private sealed record TranscriptionModeBody(string? Mode);
    private sealed record SnoozeDefaultBody(int? Minutes);
    private sealed record SnoozePresetsBody(int[]? Presets, int? DefaultMinutes);
    private sealed record TimeZoneBody(string? TimeZone);
}
