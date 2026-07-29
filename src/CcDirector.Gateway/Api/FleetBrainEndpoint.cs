using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.CarMode;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.HostedAi;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The FLEET BRAIN's front door: POST /assistant/turn. A surface hands the person's typed or transcribed
/// question here; the <see cref="CarModeBrain"/> runs its tool-calling loop against the fleet and returns the
/// final spoken reply, any actions taken, and whether it is holding a destructive action for confirmation. The
/// browser stays thin: it never sees the model key or the fleet logic.
///
/// The turn response also carries a server-minted turnId and a per-stage server timing block.
///
/// WHAT LEFT THIS FILE. Car Mode was removed from the product (#1028). Its own turn door (/carmode/turn), its
/// model-free Help door and on-screen cheat sheet, and the whole per-device timing-diagnostics surface it wrote
/// to were all reached from the Car Mode screen and nothing else, so they are gone. The BRAIN is not Car
/// Mode's: the Assistant has always run the same loop, the same tools, the same conversation store and the same
/// turn cache, so all of that stays exactly as it was. The keep-warm door stays too, and is now named for what
/// it warms (/brain/warmup) rather than for the surface that first called it.
///
/// Auth: these routes are not on the public allow-list and are not under /m/, so the host-wide auth gate
/// already requires the caller's per-device key (or the shared token), per the Gateway auth rule. THE
/// CREDENTIAL THE GATE ACCEPTED - not this file's own reading of the request - keys the server-side
/// conversation context, so multi-turn works per device without any history crossing the wire and a caller
/// cannot be authenticated as one identity while being partitioned as another. The credential is used only as
/// an opaque key and is never logged (DT-05).
/// </summary>
internal static class FleetBrainEndpoint
{
    public static void Map(IEndpointRouteBuilder app, CarModeBrain assistantBrain,
        CarModeTurnCache turnCache, CarModeWarmup warmup, HostedTenantBoundary tenantBoundary)
    {
        // Keep-warm: a surface calls this the instant it opens, and every few minutes while it stays open, so
        // the hosted model + text-to-speech are hot before the first utterance rather than during it - cold
        // start was the measured dominant latency. Gated on the keep-warm configuration; when off it is a
        // cheap no-op. The warmup runs in the BACKGROUND on CancellationToken.None so opening is never
        // blocked and navigating away does not cancel the warm. Best-effort - a failure never surfaces.
        app.MapPost("/brain/warmup", (HttpContext ctx) =>
        {
            var tenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);
            if (!CarModeKeepWarmConfig.Enabled())
                return Results.Json(new { warmed = false, reason = "keep-warm disabled" });
            _ = Task.Run(() => warmup.WarmAsync(tenant.Value, CancellationToken.None));
            return Results.Json(new { warmed = true });
        });

        // The turn route. It was mapped TWICE over the same loop, stores and cache - once for Car Mode's
        // hands-free door and once for the Assistant's desk door - which is why it is a helper taking a
        // pattern rather than an inline handler. Car Mode was removed from the product (#1028) and its door
        // went with it; the helper stays as it is, because the shape is right and a second surface on this
        // brain would use it again.
        MapTurnRoute(app, "/assistant/turn", "The Assistant", assistantBrain, turnCache, tenantBoundary);
    }

    /// <summary>
    /// One gate per authenticated device, held for the WHOLE brain turn (Codex review of the Assistant
    /// build, finding 5). A turn mutates ordered per-device state - the conversation history, the current
    /// subject, and the armed-destructive-confirmation slot - and the confirmation consume is a get-then-
    /// clear, so two turns entering the brain concurrently for the SAME device (two cockpit tabs, or a
    /// phone and a desk at once) could both observe one armed delete and execute it twice, or interleave
    /// their history writes. The turn cache only single-flights the SAME Idempotency-Key; this gate
    /// serializes DIFFERENT turns of one device. Devices are few (each is an enrolled credential), so the
    /// dictionary stays small; different devices are never queued behind each other.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> TurnGates = new();

    /// <summary>Map one turn route over the given brain. Identical mechanics for every surface: tenant
    ///  resolution, the authenticated-credential conversation key, per-device turn serialization,
    ///  Idempotency-Key single-flight, the shared 402 money refusal, and the loud 502 failure whose
    ///  message names the surface.</summary>
    private static void MapTurnRoute(IEndpointRouteBuilder app, string pattern, string surfaceLabel,
        CarModeBrain brain, CarModeTurnCache turnCache, HostedTenantBoundary tenantBoundary)
    {
        app.MapPost(pattern, async (HttpContext ctx, CarModeTurnRequest? req, CancellationToken ct) =>
        {
            FileLog.Write($"[FleetBrainEndpoint] {pattern} turn: len={req?.Text?.Length ?? 0}");
            var tenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "text is required" }, statusCode: StatusCodes.Status400BadRequest);

            // The per-device conversation key is the SAME authenticated credential the diagnostics partition
            // uses. It was read from the raw headers here too, which had the same flaw: a caller could be
            // authenticated on one credential and given another device's conversation context.
            var deviceKey = AuthenticatedCredential(ctx);
            var text = req.Text.Trim();
            // Offline resilience Phase 4b (issue #1427): the client sends its durable command-audio record id
            // as the Idempotency-Key so an already-sent turn whose result was lost in a dead zone auto-retries
            // and ACTS at most once. When present it also becomes the turnId, so a retry's diagnostics tie back
            // to the original rather than double-counting. When absent (a legacy caller), the old behavior
            // holds: a fresh server turnId and a direct brain run on the request token.
            var idemKey = ExtractIdempotencyKey(ctx);
            var turnId = string.IsNullOrEmpty(idemKey) ? Guid.NewGuid().ToString("N") : idemKey;
            try
            {
                CarModeTurnResponse result;
                // Serialize this device's turns end-to-end (see TurnGates). The wait honors the request
                // token, so a caller that gives up while queued does not hold a slot; the shared "" bucket
                // (auth gate off in local debug) serializes anonymous callers together, matching how the
                // conversation store already buckets them. Known residual window: on the Idempotency-Key
                // path the brain runs detached (CancellationToken.None) so a client that DISCONNECTS
                // mid-turn releases the gate while that detached work finishes - a deliberate trade, since
                // holding the gate from a continuation could double-release on a same-key retry. The gate
                // closes the two-tabs / double-confirm race, which never involves a disconnect.
                var gate = TurnGates.GetOrAdd(deviceKey, static _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct);
                try
                {
                    if (string.IsNullOrEmpty(idemKey))
                    {
                        result = await brain.RunTurnAsync(tenant.Value, deviceKey, text, ct);
                    }
                    else
                    {
                        // Run the brain on CancellationToken.None (NOT the request token) inside the single-flight
                        // cache, so a client that drops mid-turn does NOT abort the work - it completes and caches,
                        // and the client's retry gets the cached result instead of re-acting. Await with the request
                        // token so a disconnected client still returns promptly (the underlying work continues).
                        var work = turnCache.GetOrRunAsync(deviceKey, idemKey, () => brain.RunTurnAsync(tenant.Value, deviceKey, text, CancellationToken.None));
                        result = await work.WaitAsync(ct);
                    }
                }
                finally
                {
                    gate.Release();
                }
                return Results.Json(new
                {
                    turnId,
                    spoken = result.Spoken,
                    actions = result.Actions.Select(a => new { tool = a.Tool, summary = a.Summary }),
                    pendingConfirmation = result.PendingConfirmation,
                    // The per-stage server timing, inline, so the browser posts it back merged with its
                    // own client stamps as one diagnostics record (performance round).
                    timing = result.Timing is null ? null : new
                    {
                        totalMs = result.Timing.TotalMs,
                        modelCallCount = result.Timing.ModelCallCount,
                        modelMsTotal = result.Timing.ModelMsTotal,
                        modelMs = result.Timing.ModelMs,
                        fleetReadCount = result.Timing.FleetReadCount,
                        fleetReadMsTotal = result.Timing.FleetReadMsTotal,
                        rounds = result.Timing.Rounds,
                    },
                });
            }
            catch (CarModeUnavailableException ex)
            {
                // A money refusal (out of credits / cap / no key): the ONE shared 402 state, so the client
                // shows the consistent add-credit / add-key notice instead of a generic error.
                FileLog.Write($"[FleetBrainEndpoint] {pattern} turn unavailable: {ex.State}");
                return HostedAiHttp.PaymentRequiredResult(ex.State);
            }
            catch (OperationCanceledException)
            {
                // The client navigated away / aborted the turn; nothing to report (499 = client closed).
                return Results.Json(new { error = "cancelled" }, statusCode: 499);
            }
            catch (Exception ex)
            {
                // A loud, specific failure the client shows and speaks (decision 8), never a silent stall.
                FileLog.Write($"[FleetBrainEndpoint] {pattern} turn FAILED: {ex.Message}");
                return Results.Json(new { error = surfaceLabel + " could not complete that: " + ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    /// <summary>
    /// THE ONE caller identity in this file: THE EXACT CREDENTIAL THE AUTHENTICATION GATE ACCEPTED, which
    /// <see cref="AuthMiddleware.HasValidToken"/> stashes on the request. It is the storage partition for
    /// diagnostics and the per-device conversation key for a turn.
    ///
    /// This route deliberately does NOT read the Authorization header or the cookies itself. It used to, and
    /// that was the defect: the gate accepts a request if ANY presented credential is valid - it tries the
    /// Bearer value and then EVERY raw cc-gateway-token cookie - while a second reader here always preferred
    /// the Bearer. A caller presenting an attacker-chosen Bearer alongside their valid device cookie would
    /// therefore be authenticated on the cookie and partitioned on the Bearer, letting them read and write a
    /// partition that was not theirs. Duplicate cookies opened the same gap. Two independent readings of one
    /// request are two authentication decisions with different rules, and the disagreement between them IS
    /// the vulnerability. The identity is resolved once, by the gate whose job that is, and passed forward.
    ///
    /// Absent means no credential was authenticated at all (the host-wide gate is off in local debug). That
    /// is not an identity, so it maps to ONE shared anonymous bucket - exactly what a credential-free request
    /// got before - and never to a second reading of the headers. Never logged.
    /// </summary>
    private static string AuthenticatedCredential(HttpContext ctx)
        => ctx.Items.TryGetValue(AuthMiddleware.AuthenticatedCredentialItemKey, out var credential)
            ? credential as string ?? ""
            : "";

    /// <summary>The client's idempotency key for this turn (the durable command-audio record id), from the
    ///  standard <c>Idempotency-Key</c> header. Empty when absent (a legacy caller), which the handler maps
    ///  to the non-deduped legacy path. Trimmed and length-capped so a malformed header cannot bloat the
    ///  cache key.</summary>
    private static string ExtractIdempotencyKey(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("Idempotency-Key", out var header)) return "";
        var raw = header.ToString().Trim();
        return raw.Length > 200 ? raw[..200] : raw;
    }
}
