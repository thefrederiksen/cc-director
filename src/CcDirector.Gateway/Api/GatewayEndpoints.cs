using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using CcDirector.Core.Diagnostics;
using CcDirector.Core.Network;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

internal static class GatewayEndpoints
{
    /// <param name="onSessionState">Issue #186: receives every session-state observation
    /// (doorbell ping or heartbeat snapshot entry) as (directorId, sessionId, newState).
    /// The host feeds these to the turn-end watcher (voice auto-refresh, issue #549).</param>
    /// <param name="voiceAudioReadyFor">Issue #553: whether the Gateway has fetchable, playable
    /// cached audio for a session id (<c>WingmanVoiceService.HasVoice</c>), stamped onto
    /// <see cref="SessionDto.VoiceAudioReady"/>. Null leaves the field false.</param>
    /// <param name="needsYouStampFor">Issue #218: given (sessionId, isRed) where isRed is the
    /// session's final EffectiveColor=="red" this refresh, returns the Gateway-owned UTC
    /// timestamp the session entered red (held while red, null when not red), stamped onto
    /// <see cref="SessionDto.NeedsYouSince"/>. Null (old callers) leaves
    /// the field null.</param>
    /// <param name="interruptedBriefFor">Issue #212 W3: the Gateway's last-known rail line +
    /// headline for a session id, used to enrich the Interrupted sessions list so a dead
    /// session is triageable. Reads the durable brief store, so it works even for a session
    /// whose Director has died.</param>
    /// <param name="briefHistoryFor">Issue #212 W4: the full turn-brief history for a session
    /// id, oldest first - the raw material the restore endpoint builds its continuation
    /// context from. Reads the durable brief store, so it serves dead sessions too.</param>
    /// <param name="directorEvents">Issue #330: the per-director event ring recording the
    /// doorbell event vocabulary (session-created/session-exited/prompt-detected) so the
    /// events are observable at GET /directors/{id}/events. Null (old callers, tests that
    /// don't care) records nothing and the events route serves empty lists.</param>
    /// <param name="turnJobs">Issue #376: the async voice-turn job store (singleton owned by
    /// <see cref="GatewayHost"/>). When present, the submit/poll routes are mapped via
    /// <see cref="GatewayVoiceTurnEndpoint"/>; null (old callers) maps nothing.</param>
    public static void Map(IEndpointRouteBuilder app, DirectorRegistry registry, string version, string token, bool authEnabled = false, Func<bool>? requestShutdown = null,
        Action<string, string, string>? onSessionState = null,
        Func<string, bool>? voiceGeneratingFor = null,
        Func<string, bool>? voiceAudioReadyFor = null,
        Func<string, Core.HostedAi.HostedAiState?>? voiceUnavailableFor = null,
        Func<string, bool, DateTime?>? needsYouStampFor = null,
        Func<string, bool>? transcribingFor = null,
        Func<string, string?>? dictationStatusFor = null,
        Transcription.TranscribingSessions? transcribingSessions = null,
        Func<string, (string? RailLine, string? Headline)>? interruptedBriefFor = null,
        Func<string, List<TurnBriefDto>>? briefHistoryFor = null,
        SessionOwnerCache? owners = null,
        Gateway.Events.DirectorEventLog? directorEvents = null,
        Voice.GatewayTurnJobStore? turnJobs = null,
        Pairing.DeviceRegistry? devices = null,
        // Network Diagnostics mission (P1): the shared hourly quality rollup that POST /diag/result folds
        // client speed-test results into (home/away split on the measured path). The monitor folds into the
        // same instance. Null in tests / when diagnostics are off.
        NetDiagRollupStore? netDiagRollup = null,
        // Issue #1176 (Phase 1a): when non-null, /sessions serves a Director from this push cache instead
        // of pulling it, whenever that Director's stream is connected and its last push is within
        // streamStaleAfter. Null (stream mode off) keeps the pull-only behaviour byte-identical to today.
        Streaming.PushedSessionStore? pushedSessions = null,
        TimeSpan? streamStaleAfter = null,
        // Issue #1177 (Phase 1): when non-null, per-session commands are first tried DOWN the Director's
        // stream via this hook (GatewayHost.SendCommandAsync); a null return means the Director is not
        // stream-connected, so the endpoint falls back to its existing HTTP call. Null here (stream mode
        // off) keeps every command endpoint on the HTTP path, byte-identical to before.
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand = null,
        // Issue #1215 (Cockpit plan phase 6): the last-known-good roster cache. When non-null, a single
        // failed Director poll no longer drops that Director's sessions - the cache serves the last-known-good
        // snapshot marked stale (Wobbly) through a short grace window, and only declares the Director Offline
        // once the grace window is exhausted. Null keeps the old drop-on-first-failure behaviour.
        FleetRosterCache? rosterCache = null,
        // Issue #1292: the fleet-wide session-number authority. When non-null, the Director-facing
        // /session-numbers/* endpoints are mapped and the /sessions aggregation adopts every observed
        // number so the in-use set survives a Gateway restart. Null (old callers, tests) maps nothing
        // and leaves each Director to number locally.
        Discovery.FleetSessionNumberAllocator? sessionNumbers = null,
        // DevThrottle Stats: the always-available input-tally aggregator. Folded from the assembled
        // /sessions roster (the path that carries SessionDto.InputStats whether stream mode is on or off),
        // so "Your Throttle" is fed by the same roster the fleet already reads, not only by the SignalR
        // push path (which is unmapped when stream mode is off). Null (old callers, tests) folds nothing.
        Stats.GatewayInputStatsAggregator? inputStats = null,
        // DevThrottle Stats: the durable fleet concurrency record. Observed from the same assembled roster
        // (live count + actively-working count), so the peak is captured fleet-wide whether stream mode is
        // on or off. Null (old callers, tests) records nothing.
        Stats.GatewaySessionConcurrencyStats? concurrency = null,
        // Snooze Length mission: the Gateway-owned snooze registry. When non-null, POST
        // /sessions/{sid}/hold records/clears a snooze-until for the session, and the /sessions fold
        // overlays an EXPIRED snooze back into "needs you" (OnHold=false) so the session returns on its
        // own even if its Director has died. Null (old callers, tests) leaves hold as a plain forward
        // with no timer, byte-identical to before.
        Snooze.SnoozeRegistry? snoozeRegistry = null,
        // Gateway Cleanup mission (Wave 4b): the Gateway-native mission store. When non-null, the
        // POST/GET /missions routes are mapped and a mission-scoped spawn validates against it. Missions are
        // a fleet-level concept, so the source of truth lives here at the Gateway. Null (old callers, tests)
        // maps nothing, leaving missions to the Director's own /missions routes (unchanged this phase).
        Core.Sessions.MissionStore? missions = null)
    {
        // The old issue #1188 "session lock" (423 Locked on human input while a PENDING dictation record
        // existed) was removed deliberately (issue #1308). This is a single-operator tool: a collision
        // between the operator's own inbound dictation and their own typed send is theirs to make, not
        // the Gateway's to police - and a wedged PENDING marker used to falsely block every send for its
        // whole lifetime. The marker itself stays (it paints the roster's orange "receiving a dictation").

        // Issue #1177 (Phase 4a): the freshness window used both by /sessions (pushed-cache serve) and by
        // LocateSessionAsync (pushed-cache session location). Resolved once here so every session endpoint's
        // owner lookup shares the exact window the roster uses. When stream mode is off pushedSessions is null,
        // so this value is never consulted and location stays on the HTTP pull, byte-identical to today.
        var streamStaleResolved = streamStaleAfter ?? TimeSpan.FromSeconds(Core.Configuration.GatewayConfig.DefaultStreamStaleAfterSeconds);

        // Issue #1229: the Hub's broadcast governance state - the human-issued grant store and the
        // per-sender broadcast rate limiter. One instance per Gateway process, shared by the grant-mint
        // endpoint and the /fanout guard below. The pure scope rule lives in FleetBroadcastPolicy.
        var broadcastGovernor = new BroadcastGovernor();

        // Gateway Cleanup mission, Phase 2 (PR E-B): the async voice-turn submit/poll surface (issue #376)
        // is RETIRED. It drove the Director's SSE /sessions/{sid}/voice-turn endpoint over a raw HTTP dial
        // (a Gateway->Director dial the tunnel-only endgame must remove), and it is CLIENT-DEAD - its only
        // caller was the retired native MAUI phone client; cockpit and mobile both use /wingman/voice-turn,
        // which runs the whole turn Gateway-side. The Gateway endpoint + its two dedicated tests are deleted;
        // the Director SSE endpoint is on the Phase 1 deletion DROP list, removed at the cut.

        // Issue #1292: the fleet-wide session-number authority. A Director asks for a number when it
        // creates a session (so the number is unique across every Director on every machine) and frees
        // it when the session ends. Guarded by the same auth middleware as every other Director-facing
        // route, so the Director's own fleet credential is required.
        if (sessionNumbers is not null)
        {
            app.MapPost("/session-numbers/allocate", (SessionNumberAllocateRequest req) =>
            {
                if (string.IsNullOrWhiteSpace(req.SessionId))
                    return Results.BadRequest(new { error = "sessionId is required" });
                var number = sessionNumbers.Allocate(req.SessionId, req.DirectorId ?? "");
                return Results.Ok(new SessionNumberAllocateResponse { Number = number });
            });

            app.MapDelete("/session-numbers/{sessionId}", (string sessionId) =>
            {
                sessionNumbers.Release(sessionId);
                return Results.NoContent();
            });
        }

        // Gateway Cleanup mission (Wave 4b): the Gateway-native mission surface. Missions are a fleet-level
        // concept (they span Directors and machines and nest), so the source of truth lives here at the
        // Gateway - like fleet messaging and scheduling - and mission-existence VALIDATION lives here now.
        // These routes inherit the host-wide token middleware, exactly like /cron/jobs and /session-numbers.
        // The Director's own /missions routes stay until a later phase; this is the additive equivalent.
        //   POST /missions        body { missionName, parentMissionId? } -> 201 MissionDto | 400
        //   GET  /missions        -> [ MissionDto ]
        //   GET  /missions/{mid}  -> MissionDto | 404
        if (missions is not null)
        {
            app.MapPost("/missions", (NewMissionRequest req) =>
            {
                FileLog.Write($"[GatewayEndpoints] POST /missions: name=\"{req?.MissionName}\"");
                if (req is null || string.IsNullOrWhiteSpace(req.MissionName))
                    return Results.BadRequest(new { error = "missionName is required" });

                var mission = missions.Create(req.MissionName, req.ParentMissionId);
                return Results.Json(ToMissionDto(mission), statusCode: StatusCodes.Status201Created);
            });

            app.MapGet("/missions", () =>
                Results.Json(missions.List().Select(ToMissionDto).ToList()));

            app.MapGet("/missions/{mid}", (string mid) =>
            {
                if (!Guid.TryParse(mid, out var missionId))
                    return Results.BadRequest(new { error = "invalid mission id format" });

                var mission = missions.Get(missionId);
                return mission is null
                    ? Results.NotFound(new { error = "mission not found" })
                    : Results.Json(ToMissionDto(mission));
            });

            FileLog.Write("[GatewayEndpoints] mapped Gateway-native /missions routes");
        }

        // Issue #469 closed the secret-embedding phone-pairing QR endpoints (/pair/qr.png and
        // /pair/payload) that put the shared fleet token directly in a QR/link - a full compromise
        // if leaked. They are removed; a request to them now falls through to a 404 (no secret is
        // exposed anywhere over the network). Device enrollment uses the per-device pairing-code
        // flow (DeviceEnrollmentEndpoint, wired in GatewayHost): the key never travels in a QR or
        // link, only the short-lived code shown on the Gateway host's own local window does.

        // Graceful exit for the self-update helper: answer first (so the caller gets its 200),
        // then hand off to the host's shutdown handler shortly after. 501 when the hosting
        // process wired no handler - this endpoint never half-stops the host on its own.
        app.MapPost("/shutdown", () =>
        {
            FileLog.Write("[GatewayEndpoints] POST /shutdown");
            if (requestShutdown is null)
                return Results.Json(new { error = "shutdown not supported by this host" }, statusCode: StatusCodes.Status501NotImplemented);

            _ = Task.Run(async () =>
            {
                await Task.Delay(250); // let the 200 flush before the host starts tearing down
                if (!requestShutdown())
                    FileLog.Write("[GatewayEndpoints] /shutdown: no handler registered; nothing stopped");
            });
            return Results.Json(new { shuttingDown = true });
        });

        var logoutVisibility = authEnabled ? "" : "style=\"display:none\"";

        // Phone recorder ingest (offline-recorded audio -> transcription -> vault).
        RecordingEndpoints.Map(app);

        // Read-only view of the Communication Manager approval queue (see the phone's
        // pending drafts remotely). Step 1 of centralizing the comm queue on the Gateway.
        CommQueueEndpoints.Map(app);

        // Local-machine exe/slot management (the "Exes" page).
        ExesEndpoints.Map(app, registry, pushedSessions, streamStaleResolved);

        // ===== HTML pages =====
        // The Gateway serves NO UI pages anymore (docs/plans/one-url-cockpit.md): "/" and every
        // other UI path fall through to the Cockpit via the fallback proxy. Only the token
        // login/logout pair remains (it guards the Gateway itself when auth is enabled).
        app.MapGet("/login", (HttpContext ctx) =>
        {
            var next = ctx.Request.Query["next"].ToString();
            if (string.IsNullOrEmpty(next)) next = "/";
            var html = EmbeddedResources.Load("login.html")
                .Replace("__NEXT__", System.Web.HttpUtility.HtmlAttributeEncode(next))
                .Replace("__ERROR__", "");
            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapPost("/login", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            var submitted = (form["token"].ToString() ?? "").Trim();
            var next = form["next"].ToString();
            if (string.IsNullOrEmpty(next)) next = "/";

            if (!string.Equals(submitted, token, StringComparison.Ordinal))
            {
                var html = EmbeddedResources.Load("login.html")
                    .Replace("__NEXT__", System.Web.HttpUtility.HtmlAttributeEncode(next))
                    .Replace("__ERROR__", "Wrong token. Check gateway-token.txt and try again.");
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(html);
                return;
            }

            ctx.Response.Cookies.Append(AuthMiddleware.CookieName, token, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddDays(30),
                IsEssential = true,
            });
            ctx.Response.Redirect(IsSafeRedirect(next) ? next : "/");
        });

        app.MapGet("/logout", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(AuthMiddleware.CookieName);
            return Results.Redirect("/login");
        });

        // ===== REST =====
        app.MapGet("/healthz", () =>
        {
            var directors = registry.ListDirectors();
            // Post-cut: the roster lives ONLY in the push store, so count from there. A Director with no
            // fresh pushed snapshot is not connected to the tunnel and contributes zero.
            int totalSessions = directors.Sum(d =>
            {
                var cached = pushedSessions?.TryGetFresh(d.DirectorId, streamStaleResolved);
                return cached?.Count ?? 0;
            });

            return Results.Json(new HealthDto
            {
                Status = "ok",
                Directors = directors.Count,
                Sessions = totalSessions,
                Version = version,
                ServerTime = DateTime.UtcNow,
            });
        });

        // ===== Network diagnostics (auto-network-switching mission) =====
        // Back the mobile Diagnostics page so the owner can measure the phone-to-Gateway path from the
        // phone itself (a phone cannot run `tailscale ping`). These routes are gated like the rest of the
        // data API - the page calls them with its per-device key - so they are not an open bandwidth tap.

        // GET /diag/echo: report what the Gateway sees about the caller's connection. RemoteIpAddress
        // reflects X-Forwarded-For (UseForwardedHeaders trusts ONLY the loopback tailscale-serve proxy),
        // so it is the phone's tailnet 100.x address through the front door and its 192.168.x LAN address
        // on a direct hit - the one clean signal that says "you are relaying through Tailscale" vs "you are
        // direct on the LAN". Also hands back the Gateway's own LAN IP and tailnet name so the page can
        // show where a direct path would point.
        app.MapGet("/diag/echo", (HttpContext ctx) => Results.Json(new NetDiagEchoDto
        {
            ClientIp = ctx.Connection.RemoteIpAddress?.ToString(),
            ClientPath = NetDiag.ClassifyClientIp(ctx.Connection.RemoteIpAddress),
            ForwardedFor = ctx.Request.Headers["X-Forwarded-For"].ToString(),
            Host = ctx.Request.Host.Value ?? "",
            MachineName = Environment.MachineName,
            GatewayLanIp = LanIdentity.TryGetPrimaryLanIpv4(),
            GatewayTailnetName = TailscaleIdentity.TryGetMagicDnsName(),
            ServerTime = DateTime.UtcNow,
        }));

        // GET /diag/payload?bytes=N streams N bytes of incompressible data so the phone can time a DOWNLOAD
        // and derive throughput. Size is clamped so the endpoint cannot be turned into a bandwidth
        // amplifier, and the response is no-store so a proxy or the service worker never serves a cached
        // copy that would fake the number.
        app.MapGet("/diag/payload", (HttpContext ctx, int? bytes) =>
        {
            int size = Math.Clamp(bytes ?? NetDiag.DefaultPayloadBytes, 0, NetDiag.MaxPayloadBytes);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Bytes(NetDiag.BuildPayload(size), "application/octet-stream");
        });

        // POST /diag/payload reads and discards the request body and returns the byte count, so the phone
        // can time an UPLOAD (the direction that carries dictation audio) and derive throughput.
        app.MapPost("/diag/payload", async (HttpContext ctx) =>
        {
            long received = 0;
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(buffer)) > 0)
                received += read;
            return Results.Json(new { received });
        });

        // GET /diag/network: the SERVER-SIDE diagnostic an agent runs with no phone and no app open. Runs
        // tailscale status/ping/netcheck and reports, per connected device, direct-vs-DERP-relay + latency,
        // plus UDP/NAT health - the one signal the phone speed test cannot see (it cannot tell "warming up
        // on the relay" apart from "genuinely broken"). Ran off the request thread so the CLI shell-outs do
        // not block the Kestrel I/O thread.
        app.MapGet("/diag/network", async () =>
        {
            var diag = await Task.Run(TailscaleDiagnostics.Collect);
            return Results.Json(diag);
        });

        // GET /diag/ping: the featherweight latency endpoint the client's latency loop hits. Unlike
        // /diag/echo it does NO network-interface scan or Tailscale lookup, so its round trip is the wire
        // time, not server work - keeping the reported latency honest.
        app.MapGet("/diag/ping", () => Results.Json(new { t = DateTime.UtcNow }));

        // Result logging: the phone/Cockpit POSTs its completed speed-test result here; the Gateway stamps
        // what IT saw about the connection, writes one greppable log line, and keeps it in a small ring so
        // an agent can read the recent history at GET /diag/results with no phone. This is the "log all of
        // this so the agent can get to it" piece of the mission.
        var netDiagResults = new NetDiagResultStore(Path.Combine(CcStorage.Root(), "diagnostics-results.json"));
        app.MapPost("/diag/result", async (HttpContext ctx, NetDiagResultDto result) =>
        {
            result.ClientIp = ctx.Connection.RemoteIpAddress?.ToString();
            result.ClientPath = NetDiag.ClassifyClientIp(ctx.Connection.RemoteIpAddress);
            result.ReceivedAt = DateTime.UtcNow;
            netDiagResults.Add(result);
            // Fold into the hourly quality rollup by the MEASURED path (Direct/IsLanPath the client tagged
            // from its authoritative self-peer), never the front-door ClientPath.
            netDiagRollup?.Fold(result.ReceivedAt, result.LatencyMedianMs, result.Direct, result.IsLanPath, result.DownloadMbps, result.UploadMbps);
            FileLog.Write(
                $"[NetDiag] result surface={result.Surface} route={result.Route} clientPath={result.ClientPath} " +
                $"client={result.ClientIp} latencyMedian={result.LatencyMedianMs}ms down={result.DownloadMbps}Mbps " +
                $"up={result.UploadMbps}Mbps rating={result.Rating} loadedFrom={result.LoadedFrom}");
            await Task.CompletedTask;
            return Results.Json(new { ok = true });
        });
        app.MapGet("/diag/results", () => Results.Json(netDiagResults.Recent()));

        // GET /diag/rollup: the hourly quality trend (one bucket per UTC hour, oldest first) for the
        // Cockpit dashboard - percent-direct over time, latency trend, and the stored home/away split.
        app.MapGet("/diag/rollup", () => Results.Json(netDiagRollup?.All() ?? new List<NetDiagRollupStore.HourBucket>()));

        // About / diagnostics: product, version, build date, install root, the one Cockpit URL, and
        // the installed component versions (from installed.json on this box). Feeds the Cockpit About
        // page; loopback-reachable like the rest of the read API.
        // Route is /gateway/about so the /about path passes through to the Cockpit's Blazor page.
        app.MapGet("/gateway/about", () => Results.Json(new AboutDto
        {
            Product = AboutInfo.ProductName,
            Version = AboutInfo.VersionFull,
            BuildDate = AboutInfo.BuildDate()?.ToString("yyyy-MM-dd HH:mm:ss"),
            MachineName = Environment.MachineName,
            InstallRoot = AboutInfo.InstallRoot,
            CockpitUrl = TailscaleIdentity.TryGetFrontDoorBaseUrl() is { } fd ? fd + "/" : null,
            InstalledComponents = new Dictionary<string, string>(AboutInfo.InstalledComponents()),
            ServerTime = DateTime.UtcNow,
        }));

        // Where is this machine's Cockpit? ONE URL: the React Cockpit is served in-process by the
        // Gateway itself at the site root (issue #979 retired the separate Blazor Cockpit), so the
        // answer is the front-door base URL. Url is null when Tailscale is unavailable; the caller
        // surfaces that. Port is the Gateway port and Up is true whenever the Gateway is answering.
        app.MapGet("/cockpit", (HttpContext ctx) =>
        {
            return Results.Json(new CockpitInfoDto
            {
                Url = TailscaleIdentity.TryGetFrontDoorBaseUrl() is { } b ? b + "/" : null,
                Port = ctx.Connection.LocalPort,
                Up = true,
            });
        });

        app.MapGet("/directors", () =>
        {
            return Results.Json(registry.ListDirectors());
        });

        // ===== HTTP discovery (Phase 1) =====
        // The Director POSTs /directors/register on startup and heartbeats every 15 s.
        // On graceful shutdown it DELETEs its registration. Same-machine Directors that
        // don't have gateway.url configured continue to be discovered via the filesystem
        // watch path - both paths coexist permanently.

        app.MapPost("/directors/register", (DirectorRegistrationRequest req) =>
        {
            if (req is null || string.IsNullOrEmpty(req.DirectorId))
                return Results.BadRequest(new { error = "directorId is required" });
            // Issue #324: a Director with no resolvable tailnet identity may register FLAGGED -
            // empty endpoint plus its own reason - so the fleet can see the machine exists.
            // An empty endpoint WITHOUT the reason is still the old undialable-entry bug: reject.
            if (string.IsNullOrEmpty(req.TailnetEndpoint) && string.IsNullOrWhiteSpace(req.EndpointUnreachableReason))
                return Results.BadRequest(new { error = "tailnetEndpoint is required (or endpointUnreachableReason for a flagged no-endpoint registration)" });

            FileLog.Write($"[GatewayEndpoints] POST /directors/register: id={req.DirectorId}, endpoint={req.TailnetEndpoint}, machine={req.MachineName}");
            var dto = registry.Upsert(req);
            return Results.Json(dto, statusCode: StatusCodes.Status201Created);
        });

        app.MapPost("/directors/{id}/heartbeat", async (string id, HttpContext ctx) =>
        {
            var ok = registry.Heartbeat(id);
            if (!ok)
            {
                FileLog.Write($"[GatewayEndpoints] POST /directors/{id}/heartbeat: unknown id (caller should re-register)");
                // 410 Gone tells the Director "you're not in the registry anymore" so its
                // client can re-POST /directors/register instead of just retrying heartbeats.
                return Results.StatusCode(StatusCodes.Status410Gone);
            }

            // Issue #186: a new Director's heartbeat carries a per-session state snapshot -
            // the reconcile channel for lost doorbell pings. Old Directors POST no body.
            if (onSessionState is not null && ctx.Request.ContentLength > 0)
            {
                DirectorHeartbeatRequest? body = null;
                try { body = await ctx.Request.ReadFromJsonAsync<DirectorHeartbeatRequest>(ctx.RequestAborted); }
                catch (System.Text.Json.JsonException ex)
                {
                    FileLog.Write($"[GatewayEndpoints] heartbeat body unparsable from {id}: {ex.Message}");
                }
                if (body?.Sessions is { } sessions)
                {
                    // A state-carrying heartbeat (even with zero sessions) proves this
                    // Director pushes its own signals - the reconcile poll skips it.
                    registry.MarkStateReporting(id);
                    foreach (var s in sessions)
                        onSessionState(id, s.SessionId, s.ActivityState);
                }
            }
            return Results.Json(new { ok = true });
        });

        // Issue #186: the turn-end doorbell. The Director announces THAT a session's
        // mechanical state changed; the Gateway pulls the truth afterwards. Always 200 for
        // a known Director (a dropped observation costs nothing - the heartbeat reconciles);
        // 410 tells an unregistered Director to re-register first. Issue #330: the same
        // ping may carry an event-vocabulary tag (session-created/session-exited/
        // prompt-detected) which lands in the per-director event ring; a tag-less ping is
        // the pre-#330 shape and records nothing.
        app.MapPost("/directors/{id}/doorbell", (string id, DoorbellRequest req) =>
        {
            if (registry.Get(id) is null)
                return Results.StatusCode(StatusCodes.Status410Gone);
            if (req is null || string.IsNullOrEmpty(req.SessionId) || string.IsNullOrEmpty(req.NewState))
                return Results.BadRequest(new { error = "sessionId and newState are required" });

            registry.MarkStateReporting(id);
            if (directorEvents is not null && !string.IsNullOrEmpty(req.Event))
                directorEvents.Record(id, req.SessionId, req.Event, req.NewState);
            onSessionState?.Invoke(id, req.SessionId, req.NewState);
            return Results.Json(new { ok = true });
        });

        // Issue #330: the per-director event debug surface - the recent doorbell events
        // (session-created/session-exited/prompt-detected) the Gateway has recorded for a
        // KNOWN director, oldest first. This is the minimal Phase-1 observable sink; the
        // real consumer (the SSE/WS event hub) is Phase 3.
        app.MapGet("/directors/{id}/events", (string id) =>
        {
            if (registry.Get(id) is null)
                return Results.NotFound(new { error = "director not found" });
            var events = directorEvents?.For(id) ?? (IReadOnlyList<DirectorEventDto>)Array.Empty<DirectorEventDto>();
            return Results.Json(new { directorId = id, events });
        });

        // Gateway Cleanup mission (Phase 2/3): the two-way connectivity handshake (POST /directors/{id}/verify
        // + the verify-ws leg) is DELETED. It dialed the Director's HTTP/WebSocket callback endpoints - a
        // Gateway->Director HTTP path the tunnel-only endgame removes - and drove the reachability circuit
        // breaker, which is also gone. Liveness is now the tunnel connection itself.

        app.MapDelete("/directors/{id}/registration", (string id) =>
        {
            FileLog.Write($"[GatewayEndpoints] DELETE /directors/{id}/registration");
            var removed = registry.Remove(id);
            return removed
                ? Results.Json(new { ok = true })
                : Results.NotFound(new { error = "director not found" });
        });

        // Fleet-wide read aggregator. Fans out in parallel to every registered Director,
        // stamps each returned SessionDto with the owning Director's machine name, user,
        // tailnet endpoint, and a full deep-link ViewUrl. Failed Directors do not poison
        // the response: by default they're silently skipped (backward-compat flat list);
        // with ?envelope=true they're surfaced in machineErrors so the UI can render an
        // inline "unreachable" placeholder.
        app.MapGet("/sessions", (HttpContext ctx, string? director, string? agent, string? state,
                                       string? statusColor, string? machine,
                                       bool? includeExited, string? q, bool? envelope) =>
        {
            var directors = registry.ListDirectors()
                .Where(d => string.IsNullOrEmpty(director) || string.Equals(d.DirectorId, director, StringComparison.OrdinalIgnoreCase))
                .Where(d => string.IsNullOrEmpty(machine) || string.Equals(d.MachineName, machine, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var includeExitedActual = includeExited ?? false;
            var streamStale = streamStaleResolved;
            var results = directors.Select(d =>
            {
                // Post-cut: the pushed stream cache is the ONLY roster source. If this Director's stream is
                // connected and its last push is fresh, serve its sessions from the pushed cache. TryGetFresh
                // returns deep copies with recomputed idle clocks, so the enrichment pipeline below stamps them
                // exactly as before and the cache is never contaminated. A Director with no fresh push is not
                // connected to the tunnel and is surfaced as unreachable. (includeExited is not representable in
                // a pushed snapshot, so exited rows are simply absent - there is no HTTP pull to fetch them.)
                if (pushedSessions is not null)
                {
                    var cached = pushedSessions.TryGetFresh(d.DirectorId, streamStale);
                    if (cached is not null)
                    {
                        FileLog.Write($"[GatewayEndpoints] /sessions director={d.DirectorId} served=pushed-cache ({cached.Count} sessions)");
                        return (Director: d, Sessions: (List<SessionDto>?)cached.ToList(), Error: (string?)null);
                    }
                }

                // Issue #324: a flagged registration declared its own endpoint unreachable (no tailnet
                // identity on that machine) - surface the Director's own reason, which names the fix.
                var declared = !string.IsNullOrEmpty(d.EndpointUnreachableReason)
                    ? d.EndpointUnreachableReason!
                    : "director not connected to the tunnel";
                return (Director: d, Sessions: (List<SessionDto>?)null, Error: (string?)declared);
            }).ToList();

            var all = new List<SessionDto>();
            var machineErrors = new List<MachineErrorDto>();
            var reachability = new List<DirectorReachabilityDto>();

            // Issue #1215 (Cockpit plan phase 6): first pass - decide, per Director, WHAT to serve and
            // its reachability state, before any per-session enrichment runs. A successful read is served
            // as Online. A failed read is handed to the last-known-good cache, which either keeps serving
            // that Director's stored snapshot marked stale (Wobbly, inside the grace window) or, once the
            // grace window is exhausted, declares it Offline and drops its sessions. Serving the stale
            // snapshot through the SAME enrichment below is what makes a transient miss change the entries'
            // appearance in place instead of removing them, so the roster never reflows.
            var served = new List<(DirectorDto Director, List<SessionDto> Sessions, bool Stale)>();
            foreach (var (d, sessions, error) in results)
            {
                if (error is null && sessions is not null)
                {
                    if (rosterCache is not null)
                        rosterCache.RecordReachable(d.DirectorId, sessions);
                    reachability.Add(new DirectorReachabilityDto
                    {
                        DirectorId = d.DirectorId,
                        MachineName = d.MachineName ?? "",
                        State = DirectorReachabilityDto.StateOnline,
                        LastSeenUtc = DateTime.UtcNow,
                        LastSeenAgeSeconds = 0,
                        Error = null,
                    });
                    served.Add((d, sessions, Stale: false));
                    continue;
                }

                // A failed read. Without the last-known-good cache, keep the historical behaviour: drop
                // the Director's sessions and surface it as a machine error immediately.
                var reason = error ?? "unreachable";
                if (rosterCache is null)
                {
                    machineErrors.Add(new MachineErrorDto
                    {
                        DirectorId = d.DirectorId,
                        MachineName = d.MachineName,
                        Error = reason,
                    });
                    continue;
                }

                var projection = rosterCache.RecordUnreachable(d.DirectorId, reason);
                if (projection.State == FleetReachabilityState.Wobbly && projection.StaleSessions is not null)
                {
                    reachability.Add(new DirectorReachabilityDto
                    {
                        DirectorId = d.DirectorId,
                        MachineName = d.MachineName ?? "",
                        State = DirectorReachabilityDto.StateWobbly,
                        LastSeenUtc = projection.LastSeenUtc,
                        LastSeenAgeSeconds = projection.LastSeenAgeSeconds,
                        Error = reason,
                    });
                    served.Add((d, projection.StaleSessions.ToList(), Stale: true));
                    continue;
                }

                // Offline: the grace window is exhausted (or the Director was never reachable). Drop its
                // sessions exactly as before, and record the Offline reachability entry.
                reachability.Add(new DirectorReachabilityDto
                {
                    DirectorId = d.DirectorId,
                    MachineName = d.MachineName ?? "",
                    State = DirectorReachabilityDto.StateOffline,
                    LastSeenUtc = projection.LastSeenUtc,
                    LastSeenAgeSeconds = projection.LastSeenAgeSeconds,
                    Error = reason,
                });
                machineErrors.Add(new MachineErrorDto
                {
                    DirectorId = d.DirectorId,
                    MachineName = d.MachineName ?? "",
                    Error = reason,
                });
            }

            foreach (var (d, sessions, stale) in served)
            {
                // Issue #291: a reachable Director's returned list is the authoritative live set for it.
                // Prune any session the cache still attributes to this Director that is no longer live here
                // - it exited or disappeared - so the per-session WS proxy reverts to 404 instead of #288's
                // 503 "owner offline". Computed from the raw returned list (before the per-session view
                // filters below) and excluding Exited rows (a Director may include them when
                // includeExited=true). Owners on OTHER Directors are untouched, so an offline owner's
                // sessions stay cached -> still 503 (#288 unchanged).
                // Issue #1215: SKIP this prune for a Wobbly (stale) serve - the Director did NOT answer, so
                // the stale snapshot is not authoritative and must not evict live ownership records.
                if (!stale)
                {
                    var liveIds = new HashSet<string>(
                        sessions
                            .Where(x => !string.IsNullOrEmpty(x.SessionId)
                                     && !string.Equals(x.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase))
                            .Select(x => x.SessionId),
                        StringComparer.Ordinal);
                    owners?.RetainForDirector(d.DirectorId, liveIds);
                    // Snooze Length mission: a reachable Director's returned list is authoritative, so a
                    // snoozed session that has permanently exited is no longer live here - drop its
                    // snooze entry so the registry does not accumulate stale entries on disk. Runs only
                    // for a Director that actually answered (!stale), so a transient miss never loses a
                    // pending snooze.
                    snoozeRegistry?.PruneNotLive(d.DirectorId, liveIds);
                }

                var baseUrl = DeriveDirectorBaseUrl(ctx, d);
                var gatewayBaseUrl = DeriveGatewayBaseUrl(ctx);
                foreach (var s in sessions)
                {
                    if (!string.IsNullOrEmpty(agent) && !string.Equals(s.Agent, agent, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(state) && !string.Equals(s.ActivityState, state, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(statusColor) && !string.Equals(s.StatusColor, statusColor, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!includeExitedActual && string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!string.IsNullOrEmpty(q))
                    {
                        var needle = q;
                        var nameHit = !string.IsNullOrEmpty(s.Name) && s.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);
                        var repoHit = !string.IsNullOrEmpty(s.RepoPath) && s.RepoPath.Contains(needle, StringComparison.OrdinalIgnoreCase);
                        if (!nameHit && !repoHit) continue;
                    }

                    s.DirectorId = d.DirectorId;
                    // Issue #335: Director-supplied identity fields win over Gateway-derived ones.
                    // A NEW Director (issue #335+) populates MachineName, User, TailnetEndpoint,
                    // and ViewUrl itself; the Gateway must not overwrite them (they carry the
                    // Director's own resolved tailnet identity). An OLD Director sends empty fields;
                    // the Gateway enriches them as before (back-compat for mixed-version fleets).
                    if (string.IsNullOrEmpty(s.MachineName))
                        s.MachineName = d.MachineName;
                    if (string.IsNullOrEmpty(s.User))
                        s.User = d.User;
                    if (string.IsNullOrEmpty(s.TailnetEndpoint))
                        s.TailnetEndpoint = baseUrl;
                    // Issue #288: remember who owns this session so the WS proxy answers 503 (owner
                    // offline) instead of 404 once this Director goes dark.
                    owners?.Remember(s.SessionId, d.DirectorId);
                    // Issue #549: the always-on turn-brief pipeline is retired. The Gateway no
                    // longer stamps the assessed-state refutation (issue #186, Option A) nor the
                    // brief stamping (issue #187 BriefingState/RailLine) - the brief agent that
                    // wrote those is deleted. "Needs you" reverts to the Director's raw mechanical
                    // signal; AssessedState stays null so every UI's "AssessedState ?? ActivityState"
                    // falls through to the raw ActivityState.
                    // Issue #531 voice mode: while the gateway's warm-brain wingman is producing this
                    // session's spoken summary, present it through the yellow "wingman reading" window
                    // (red -> yellow -> red). Gated on raw red so a working (blue) session is
                    // untouched. Independent of any brief agent; never spawns a --print explain.
                    if (voiceGeneratingFor is not null
                        && (s.BriefingState is null or "None" or "Briefed")
                        && string.Equals(s.StatusColor, "red", StringComparison.OrdinalIgnoreCase)
                        && voiceGeneratingFor(s.SessionId))
                    {
                        s.BriefingState = "Briefing";
                    }
                    // Issue #553: surface the two voice readiness booleans the color rule and the /m
                    // client read directly. VoiceGenerating = the wingman is producing this session's
                    // spoken summary now; VoiceAudioReady = the gateway has fetchable, playable audio
                    // (the SINGLE truthful "there is voice you can play right now" signal). VoiceGenerating
                    // is the only "preparing voice" hold; VoiceAudioReady controls playback affordances.
                    if (voiceGeneratingFor is not null)
                        s.VoiceGenerating = voiceGeneratingFor(s.SessionId);
                    if (voiceAudioReadyFor is not null)
                        s.VoiceAudioReady = voiceAudioReadyFor(s.SessionId);
                    // Issue #939: when the gateway could not keep this session's voice because hosted AI
                    // is unavailable (out of credits / cap / no key), stamp the ONE shared message so the
                    // owning UI shows the consistent add-credit / add-key state instead of a silently
                    // missing play triangle. Null (voice fine) leaves the field unset.
                    if (voiceUnavailableFor is not null && voiceUnavailableFor(s.SessionId) is Core.HostedAi.HostedAiState reason)
                        s.VoiceUnavailable = HostedAi.HostedAiHttp.Dto(reason);
                    // Orange "Transcribing..." while a dictated utterance is uploading/transcribing in
                    // the background for this session (mobile Speak -> Send released the screen). Stamped
                    // BEFORE the NeedsYouSince clock below so the EffectiveColor fold already sees orange
                    // (a transcribing session is not "needs you") when the clock reads the final color.
                    if (transcribingFor is not null)
                        s.Transcribing = transcribingFor(s.SessionId);
                    // Issue #1181, Task 4: the honest phase label - "Uploading from phone" (durable PENDING
                    // marker) vs "Transcribing" (active run). Drives the same orange, but the clients render
                    // this string so the user knows whether it is their phone still uploading or the server.
                    if (dictationStatusFor is not null)
                        s.DictationStatus = dictationStatusFor(s.SessionId);
                    // The authoritative presentation fold (EffectiveColor / StateLabel / TriageBucket /
                    // NeedsYouSince) is stamped in ONE post-pass AFTER this loop assembles the whole fleet -
                    // see StampFleetRolesAndFold below. It is deferred because SessionRole (which the fold
                    // now reads to suppress a live Worker's red) needs the full roster, not one session.
                    // Issue #335: ViewUrl - use the Director-supplied value when present (it carries
                    // the correct tailnet endpoint and sessionId); for OLD Directors (empty ViewUrl)
                    // fall back to the Gateway-derived deep link, preserving the gw= parameter so
                    // the session view can link back to the Gateway it came from.
                    if (string.IsNullOrEmpty(s.ViewUrl))
                        s.ViewUrl = $"{baseUrl}/sessions/{s.SessionId}/view?gw={Uri.EscapeDataString(gatewayBaseUrl)}";
                    all.Add(s);
                }
            }

            // The whole fleet is now assembled: compute each session's automatic role from the roster and
            // stamp the presentation fold (which reads the role to suppress a live Worker's red toward the
            // human). Done here, once, because the role needs the full fleet view.
            StampFleetRolesAndFold(all, needsYouStampFor, snoozeRegistry);

            // DevThrottle Stats: fold the assembled roster's per-session input tallies into the always-
            // available aggregate that backs "Your Throttle". This is the ONE path that carries
            // SessionDto.InputStats on the live Gateway regardless of stream mode (the SignalR DirectorHub
            // fold only runs when stream mode is on, which it is not in production). The aggregator's
            // per-session high-water logic makes folding the full roster on every read idempotent - only a
            // genuine increase is added, so repeated /sessions polls never double-count.
            inputStats?.ObserveSnapshot(all, DateTime.UtcNow);

            // DevThrottle Stats: record fleet concurrency and the hourly activity log from the same
            // assembled roster - max concurrent loaded/running (live) and actively working, plus how many
            // distinct sessions/machines/repositories ran each hour. Fleet-wide with no per-Director
            // instrumentation, since the roster already sees every session on every machine. The tracker
            // keeps only the higher value per hour, so folding on every /sessions read never inflates.
            concurrency?.Observe(all, DateTime.UtcNow);

            // Issue #1292: adopt every observed number into the fleet allocator's in-use set. This is how
            // the Gateway learns numbers it did not hand out - a number a Director assigned offline, or any
            // number still live after a Gateway restart - so it never hands the same number to a new
            // session. Adopt only ever marks a number in use (never frees one), so doing it from this
            // possibly-filtered view is safe: a Director that is momentarily absent from the aggregation
            // can never lose its numbers here.
            if (sessionNumbers is not null)
                foreach (var s in all)
                    if (s.Number is int num)
                        sessionNumbers.Adopt(s.SessionId, s.DirectorId, num);

            if (envelope == true)
            {
                // Issue #1215: the envelope also carries the per-Director reachability (Online / Wobbly /
                // Offline with a last-seen age), so the Cockpit renders the three states in place. machineErrors
                // is retained unchanged for back-compat (an Offline Director appears in both).
                return Results.Json(new { sessions = all, machineErrors, directors = reachability });
            }
            return Results.Json(all);
        })
        // Issue #806: advertise the default response shape (a SessionDto array) in the OpenAPI
        // document so the mobile app's openapi-typescript codegen generates a typed roster client.
        .Produces<List<SessionDto>>(StatusCodes.Status200OK);

        // Interrupted sessions (issue #212 W3): fan out to every Director for the crash
        // journals left on its machine by Directors that died abnormally, flatten to one row
        // per recoverable session, and enrich each with the Gateway's last-known brief so the
        // Cockpit Interrupted sessions list is triageable. Directors on one machine share the journal dir, so the
        // same dead journal can be reported by several live Directors - dedupe by directorId+pid.
        app.MapGet("/interrupted", async (CancellationToken ct) =>
        {
            var directors = registry.ListDirectors();
            var fanout = directors.Select(async d =>
            {
                // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (interrupted-list verb, director-level).
                // A non-null stream result is authoritative for this Director - Ok carries its journals, a non-Ok
                // is treated as no journals (skipped), exactly as a failed HTTP read returned null and was
                // skipped below. A null return (no stream) falls back to the existing reachability-gated HTTP read.
                var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, d.DirectorId, "interrupted-list", "", null, ct);
                // Post-cut: tunnel-only. A null result means the Director is not connected, so no journals.
                return (Director: d, Journals: sr is not null && sr.Ok ? DirectorCommandRouter.ReadBody<List<CrashJournalDto>>(sr) : null);
            }).ToList();
            var results = await Task.WhenAll(fanout);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var outList = new List<InterruptedSessionDto>();
            foreach (var (d, journals) in results)
            {
                if (journals is null) continue;
                foreach (var j in journals)
                {
                    if (!seen.Add($"{j.DirectorId}.{j.Pid}")) continue; // already reported by a sibling Director
                    foreach (var s in j.Sessions)
                    {
                        var (railLine, headline) = interruptedBriefFor?.Invoke(s.SessionId) ?? (null, null);
                        outList.Add(new InterruptedSessionDto
                        {
                            SessionId = s.SessionId,
                            Name = s.Name,
                            RepoPath = s.RepoPath,
                            Agent = s.Agent,
                            ClaudeSessionId = s.ClaudeSessionId,
                            CreatedAtUtc = s.CreatedAtUtc,
                            DeadDirectorId = j.DirectorId,
                            DeadPid = j.Pid,
                            MachineName = j.MachineName,
                            User = j.User,
                            DiedAtUtc = j.LastUpdatedUtc,
                            ReportedByDirectorId = d.DirectorId,
                            RailLine = railLine,
                            Headline = headline,
                        });
                    }
                }
            }
            return Results.Json(outList.OrderByDescending(x => x.DiedAtUtc).ToList());
        });

        // Dismiss one interrupted journal once recovered or unwanted. Routed to the live
        // Director that surfaced it (via=reportedByDirectorId), which owns its machine's dir.
        app.MapDelete("/interrupted/{deadDirectorId}/{deadPid:int}", async (string deadDirectorId, int deadPid, string? via, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayEndpoints] DELETE /interrupted/{deadDirectorId}/{deadPid} via={via}");
            if (string.IsNullOrWhiteSpace(via))
                return Results.BadRequest(new { error = "via (reporting director id) is required" });
            var d = registry.Get(via);
            if (d is null)
                return Results.NotFound(new { error = "reporting director not found" });

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (interrupted-dismiss verb on the reporting
            // Director). The HTTP path collapsed any non-success (incl a 404) to false -> 502, so a non-Ok
            // stream result maps to 502 to stay byte-identical.
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, via, "interrupted-dismiss", "",
                new InterruptedDismissRequest { DeadDirectorId = deadDirectorId, DeadPid = deadPid }, ct);
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502 like a failed dismiss.
            return sr is not null && sr.Ok ? Results.Json(new { dismissed = true }) : Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        // Dismiss ONE session from an interrupted journal (issue #212 W4): the rest of the
        // journal stays in the Interrupted sessions list. Routed like the journal-level dismiss above.
        app.MapDelete("/interrupted/{deadDirectorId}/{deadPid:int}/sessions/{sessionId}",
            async (string deadDirectorId, int deadPid, string sessionId, string? via, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayEndpoints] DELETE /interrupted/{deadDirectorId}/{deadPid}/sessions/{sessionId} via={via}");
            if (string.IsNullOrWhiteSpace(via))
                return Results.BadRequest(new { error = "via (reporting director id) is required" });
            var d = registry.Get(via);
            if (d is null)
                return Results.NotFound(new { error = "reporting director not found" });

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (interrupted-remove verb on the reporting
            // Director). Non-Ok -> 502, matching the HTTP path's false -> 502 collapse.
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, via, "interrupted-remove", "",
                new InterruptedRemoveRequest { DeadDirectorId = deadDirectorId, DeadPid = deadPid, SessionId = sessionId }, ct);
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            return sr is not null && sr.Ok ? Results.Json(new { removed = true }) : Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        // Restore one interrupted session (issue #212 W4): create a CONTINUATION session -
        // a fresh session in the dead session's repo, seeded with a context document built
        // from the Gateway's surviving turn-brief history. Never `claude --resume`. The
        // continuation is created on req.ToDirectorId when given, else on the reporting
        // Director (req.Via) - the reporter shares the dead Director's machine, so the repo
        // path is valid there. After a successful create the restored session is removed
        // from the dirty journal so the Interrupted sessions list reflects what is still unrecovered.
        app.MapPost("/interrupted/{deadDirectorId}/{deadPid:int}/restore",
            async (string deadDirectorId, int deadPid, RestoreInterruptedRequest req, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayEndpoints] POST /interrupted/{deadDirectorId}/{deadPid}/restore: sid={req?.SessionId} via={req?.Via} toDir={req?.ToDirectorId}");
            if (req is null || string.IsNullOrWhiteSpace(req.SessionId))
                return Results.BadRequest(new { error = "sessionId is required" });
            if (string.IsNullOrWhiteSpace(req.Via))
                return Results.BadRequest(new { error = "via (reporting director id) is required" });

            var reporter = registry.Get(req.Via);
            if (reporter is null)
                return Results.NotFound(new { error = "reporting director not found" });
            var target = string.IsNullOrWhiteSpace(req.ToDirectorId) ? reporter : registry.Get(req.ToDirectorId);
            if (target is null)
                return Results.NotFound(new { error = "target director not found" });

            // The journal is the source of truth for what is restorable - never trust the
            // caller for repo/name. Re-read it from the reporting Director. Gateway Cleanup Phase 2 (PR D):
            // ride the tunnel first (interrupted-list verb on the reporting Director); a null return falls
            // back to the HTTP read. A non-Ok stream result surfaces as the same 502 the HTTP null produced.
            var journalsSr = await DirectorCommandRouter.TrySendAsync(sendCommand, req.Via, "interrupted-list", "", null, ct);
            // Post-cut: tunnel-only. A null result (reporting Director not connected) yields null journals -> 502 below.
            List<CrashJournalDto>? journals = journalsSr is not null && journalsSr.Ok
                ? DirectorCommandRouter.ReadBody<List<CrashJournalDto>>(journalsSr) : null;
            if (journals is null)
                return Results.Problem("reporting director did not serve its crash journals", statusCode: StatusCodes.Status502BadGateway);
            var journal = journals.FirstOrDefault(j =>
                string.Equals(j.DirectorId, deadDirectorId, StringComparison.OrdinalIgnoreCase) && j.Pid == deadPid);
            var row = journal?.Sessions.FirstOrDefault(s =>
                string.Equals(s.SessionId, req.SessionId, StringComparison.OrdinalIgnoreCase));
            if (journal is null || row is null)
                return Results.NotFound(new { error = "interrupted session not found in that journal (already restored or dismissed?)" });

            var briefs = briefHistoryFor?.Invoke(row.SessionId) ?? new List<TurnBriefDto>();
            var context = Recovery.RestoreContextBuilder.Build(
                row.Name, row.SessionId, row.RepoPath, row.ClaudeSessionId, journal.LastUpdatedUtc, briefs);

            // Create the continuation over the tunnel (create verb, director-level so SessionId is "").
            // The tunnel unary has no 2s aggregate timeout - keep-alive sustains a multi-second spawn - so
            // the orphan risk the old dedicated 20s HttpClient guarded against does not apply.
            var spawnReq = new NewSessionRequest
            {
                RepoPath = row.RepoPath,
                Agent = row.Agent,
                PrePrompt = context,
            };
            var createSr = await DirectorCommandRouter.TrySendAsync(sendCommand, target.DirectorId, "create", "", spawnReq, CancellationToken.None);
            if (createSr is null)
                return Results.Problem("target director is not connected to the tunnel", statusCode: StatusCodes.Status502BadGateway);
            SessionDto? created = createSr.Ok ? DirectorCommandRouter.ReadBody<SessionDto>(createSr) : null;
            if (created is null && createSr.Ok is false)
                return Results.Problem(
                    $"target director failed to create the continuation session: {DirectorCommandRouter.DescribeFailure(createSr)}",
                    statusCode: StatusCodes.Status502BadGateway);
            if (created is null)
                return Results.Problem("target director returned an empty session body", statusCode: StatusCodes.Status502BadGateway);
            created.DirectorId = target.DirectorId;
            FileLog.Write($"[GatewayEndpoints] restore: created continuation {created.SessionId} on {target.DirectorId} for dead {row.SessionId}");

            // Give the continuation the dead session's name. Best-effort: a failed rename
            // does not undo a successful restore.
            var restoredName = string.IsNullOrWhiteSpace(row.Name) ? null : row.Name;
            if (restoredName is not null)
            {
                // Gateway Cleanup Phase 2: rename over the tunnel (patch verb, tunnel-first, HTTP fallback pre-cut).
                var renameReq = new SessionUpdateRequest { Name = restoredName };
                SessionDto? renamed; string? patchErr;
                var patchSr = await DirectorCommandRouter.TrySendAsync(sendCommand, target.DirectorId, "patch", created.SessionId, renameReq, CancellationToken.None);
                // Post-cut: tunnel-only. A null result (Director not connected) leaves the rename un-applied.
                renamed = patchSr is not null && patchSr.Ok ? DirectorCommandRouter.ReadBody<SessionDto>(patchSr) : null;
                patchErr = patchSr is null ? "target director not connected to the tunnel"
                    : (patchSr.Ok ? null : DirectorCommandRouter.DescribeFailure(patchSr));
                if (renamed is not null) { renamed.DirectorId = target.DirectorId; created = renamed; }
                else FileLog.Write($"[GatewayEndpoints] restore: rename failed (continuing): {patchErr}");
            }

            // Pull the restored session out of the Interrupted sessions list. Best-effort too - the
            // user can still Dismiss the row by hand if this leg fails.
            // Gateway Cleanup Phase 2: journal cleanup over the tunnel (interrupted-remove verb on the reporting
            // Director, tunnel-first, HTTP fallback pre-cut).
            var removeReq = new InterruptedRemoveRequest { DeadDirectorId = deadDirectorId, DeadPid = deadPid, SessionId = row.SessionId };
            var removeSr = await DirectorCommandRouter.TrySendAsync(sendCommand, reporter.DirectorId, "interrupted-remove", "", removeReq, CancellationToken.None);
            var cleaned = removeSr is not null && removeSr.Ok;
            if (!cleaned)
                FileLog.Write($"[GatewayEndpoints] restore: journal cleanup failed for {row.SessionId} (row stays in the Interrupted sessions list)");

            return Results.Json(new RestoreInterruptedResponse
            {
                Restored = true,
                TargetSession = created,
                ContextSent = context,
                JournalCleaned = cleaned,
            }, statusCode: StatusCodes.Status201Created);
        });

        app.MapGet("/sessions/{sid}", async (HttpContext ctx, string sid) =>
        {
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var baseUrl = DeriveDirectorBaseUrl(ctx, director);
            session.DirectorId = director.DirectorId;
            session.MachineName = director.MachineName;
            session.User = director.User;
            session.TailnetEndpoint = baseUrl;
            session.ViewUrl = $"{baseUrl}/sessions/{session.SessionId}/view?gw={Uri.EscapeDataString(DeriveGatewayBaseUrl(ctx))}";
            return Results.Json(session);
        });

        // Forward "kill this session" to the owning Director so a remote client (the
        // phone) can shut a session down. Without this, DELETE only worked on the
        // Director's own Control API, never through the Gateway.
        app.MapDelete("/sessions/{sid}", async (string sid) =>
        {
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502 like a failed kill.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "kill", sid, null, CancellationToken.None);
            var ok = streamResult is not null && streamResult.Ok;
            if (!ok)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(new { killed = true });
        });

        // Forward "flag this session for deletion" to the owning Director, so a session on ONE
        // machine (or a remote client) can request the async teardown of a session on another. The
        // owning Director's reaper does the actual removal. Body is optional ({ "reason": "..." }).
        app.MapPost("/sessions/{sid}/request-deletion", async (string sid, SessionDeletionRequest? body, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Gateway Cleanup (Phase 2, PR C): try the owning Director's tunnel first; a null return (stream
            // mode off / Director not stream-connected) falls back to the HTTP dial below, byte-identical. The
            // stream Ok result is success, so it synthesizes the same { pendingDeletion } body the HTTP path returns.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "request-deletion", sid, body, ct);
            return streamResult is not null && streamResult.Ok
                ? Results.Json(new { pendingDeletion = true })
                : Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        // Forward "cancel the pending deletion" to the owning Director (grace-window undo).
        app.MapDelete("/sessions/{sid}/request-deletion", async (string sid, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Gateway Cleanup (Phase 2, PR C): tunnel-first, HTTP fallback on a null return (byte-identical).
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "cancel-deletion", sid, null, ct);
            return streamResult is not null && streamResult.Ok
                ? Results.Json(new { pendingDeletion = false })
                : Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        // Phase 4b: forward wingman observability through the Gateway so the merged
        // Session View on the gateway side can render WHY a dot is the color it is.
        app.MapGet("/sessions/{sid}/wingman", async (string sid, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Gateway Cleanup (Phase 2, PR C): tunnel-first; a null return falls back to the HTTP dial below,
            // byte-identical. The Ok body IS the WingmanViewDto JSON, passed through exactly as the HTTP body.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "wingman-view", sid, null, ct);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        // Phase 5: forward "ask the wingman" calls. Each is one fresh side-call
        // (Haiku for free-text asks; Opus when Mode=="explain"). Body forwards verbatim.
        app.MapPost("/sessions/{sid}/wingman/ask", async (string sid, WingmanAskRequest req, CancellationToken ct) =>
        {
            var explain = string.Equals(req?.Mode, "explain", StringComparison.OrdinalIgnoreCase);
            if (req is null || (!explain && string.IsNullOrWhiteSpace(req.Question)))
                return Results.BadRequest(new WingmanAskResult { Status = "bad_request", Error = "question is required" });
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Gateway Cleanup (Phase 2, PR C): tunnel-first. This is a SLOW LLM call - the request ct threads
            // straight into the SignalR invocation (which has no per-invocation timeout; keep-alive pings sustain
            // the long await), so the synchronous browser contract is byte-identical to the HTTP forward. A null
            // return falls back to the HTTP dial below. The Ok body IS the WingmanAskResult JSON.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "wingman-ask", sid, req, ct);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        // Forward "set the session goal" to the owning Director. Body forwards verbatim.
        app.MapPost("/sessions/{sid}/wingman/goal", async (string sid, WingmanGoalRequest req, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var goalReq = req ?? new WingmanGoalRequest();
            // Post-cut: tunnel-only. The Ok stream body IS the { goal, goalSetAt, goalState } JSON; a null
            // result (Director not connected) or a non-Ok result collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "wingman-goal", sid, goalReq, ct);
            var body = streamResult is not null && streamResult.Ok ? streamResult.BodyJson : null;
            if (body is null)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Content(body, "application/json");
        });

        // Automatic session roles (chunk 2.5): (re)declare a session's sticky explicit role, routed DOWN the
        // stream first (DirectorCommandRouter), HTTP fallback otherwise. The Ok body is the updated SessionDto.
        app.MapPost("/sessions/{sid}/role", async (string sid, SetRoleRequest req, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var roleReq = req ?? new SetRoleRequest();
            // Post-cut: tunnel-only. The Ok stream body is the updated SessionDto JSON; a null or non-Ok result collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "set-role", sid, roleReq, ct);
            var body = streamResult is not null && streamResult.Ok ? streamResult.BodyJson : null;
            if (body is null)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Content(body, "application/json");
        });

        // Forward the FIFO "park / un-park this session" (hold) call to the owning Director, AND
        // record/clear the Gateway-owned snooze timer for it (Snooze Length mission,
        // docs/architecture/snooze-length-mission-2026-07-11.md). Snooze IS the hold, plus a
        // Gateway-owned expiry timestamp: holding a session records a snooze-until so the session is
        // GUARANTEED to return to "needs you" on its own even if its Director later dies; un-holding
        // clears it. The registry mutation happens only AFTER the forward succeeds, so a hold that did
        // not take never arms (or leaves) a timer.
        app.MapPost("/sessions/{sid}/hold", async (string sid, HoldRequest req, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var holdReq = req ?? new HoldRequest();
            // Post-cut: tunnel-only. The Ok result's body IS the { onHold } JSON; a null result (Director not
            // connected) or a non-Ok result collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "hold", sid, holdReq, ct);
            var body = streamResult is not null && streamResult.Ok ? streamResult.BodyJson : null;
            if (body is null)
                return Results.StatusCode(StatusCodes.Status502BadGateway);

            if (snoozeRegistry is not null)
            {
                if (holdReq.OnHold)
                {
                    // One snooze length for everyone (the per-user Gateway default) - read now so a
                    // Settings change applies to the next snooze. No per-snooze duration by design.
                    var minutes = Core.Configuration.SnoozeDefaultConfig.Get();
                    snoozeRegistry.Snooze(sid, DateTime.UtcNow.AddMinutes(minutes), director.DirectorId);
                }
                else
                {
                    // Manual unsnooze: drop the timer (an alarm turned off).
                    snoozeRegistry.Clear(sid);
                }
            }
            return Results.Content(body, "application/json");
        });

        // Mark / clear a session as transcribing a dictated utterance. Unlike hold this is a purely
        // Gateway-owned transient flag - it is NOT forwarded to the Director; it only feeds the
        // orange "Transcribing..." roster color. The mobile Speak flow calls { transcribing: true }
        // the instant the user hits Send (releasing the screen) and { transcribing: false } once the
        // background upload/transcribe/submit finishes or fails. A literal route so it wins over the
        // /sessions/{sid}/{**rest} catch-all Director proxy. Verified the session exists so a stale id
        // cannot pin a phantom mark.
        app.MapPost("/sessions/{sid}/transcribing", async (string sid, TranscribingRequest req) =>
        {
            if (transcribingSessions is null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var transcribing = req?.Transcribing ?? false;
            if (transcribing)
                transcribingSessions.Begin(sid);
            else
                transcribingSessions.End(sid);
            return Results.Json(new { transcribing });
        });

        app.MapPatch("/sessions/{sid}", async (string sid, SessionUpdateRequest req) =>
        {
            if (req is null)
                return Results.BadRequest(new { error = "request body is required" });

            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });

            FileLog.Write($"[GatewayEndpoints] PATCH /sessions/{sid}: name=\"{req.Name}\", director={director.DirectorId}");

            // Post-cut: tunnel-only. A null result means the Director is not connected -> 502.
            SessionDto? body;
            string? err;
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "patch", sid, req, CancellationToken.None);
            if (streamResult is null)
            {
                body = null;
                err = "director not connected to the tunnel";
            }
            else
            {
                body = streamResult.Ok ? DirectorCommandRouter.ReadBody<SessionDto>(streamResult) : null;
                err = streamResult.Ok ? null : DirectorCommandRouter.DescribeFailure(streamResult);
            }
            if (body is null)
                return Results.Problem(err ?? "patch failed", statusCode: StatusCodes.Status502BadGateway);

            body.DirectorId = director.DirectorId;
            return Results.Json(body);
        });

        app.MapGet("/sessions/{sid}/buffer", async (string sid, int? lines, bool? raw, long? since, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null)
                return Results.NotFound(new { error = "session not found across any director" });

            if (director is null)
                return Results.NotFound(new { error = "session not found across any director" });

            // Post-cut: tunnel-only. The query params ride in a BufferRequest payload the Director's buffer
            // verb reads. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "buffer", sid,
                new BufferRequest { Lines = lines, Raw = raw == true, Since = since }, ct);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        app.MapPost("/sessions/{sid}/prompt", async (string sid, PromptRequest req, HttpContext httpCtx) =>
        {
            if (req is null || string.IsNullOrEmpty(req.Text))
                return Results.BadRequest(new { error = "text is required" });

            // DevThrottle Stats: stamp the surface from the VERIFIED device key (stashed by AuthMiddleware),
            // overwriting any client-supplied value so it cannot be forged. Rides both the SignalR command
            // and the HTTP fallback below to the Director's choke-point tally. This IS the operator front
            // door, so it is ALWAYS a real turn: when no device key resolved (a shared-machine-token call) we
            // stamp "unknown" - NOT null - so the Director still counts it, into the honest "unknown" surface
            // bucket the dashboard shows (decision 9: surface excluded volume, never silently drop it).
            // Machine-to-machine traffic (fanout/broadcast) never reaches this handler and never sets Surface,
            // so it stays null and is correctly excluded.
            req.Surface = (httpCtx.Items.TryGetValue(AuthMiddleware.DeviceTypeItemKey, out var dt) ? dt as string : null) ?? "unknown";

            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });

            FileLog.Write($"[GatewayEndpoints] POST prompt: sid={sid}, director={director.DirectorId}, waitForIdle={req.WaitForIdle}");

            // Post-cut: tunnel-only. A null result means the Director is not connected -> 502. The WaitForIdle
            // poll below is unchanged - it observes the session regardless of how the prompt was delivered.
            bool ok; PromptResponse? body; string? err;
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "prompt", sid, req, CancellationToken.None);
            if (streamResult is null)
            {
                ok = false;
                body = null;
                err = "director not connected to the tunnel";
            }
            else
            {
                ok = streamResult.Ok;
                body = streamResult.Ok ? DirectorCommandRouter.ReadBody<PromptResponse>(streamResult) : null;
                err = streamResult.Ok ? null : DirectorCommandRouter.DescribeFailure(streamResult);
            }
            if (!ok || body is null)
                return Results.Json(new PromptResponse
                {
                    Accepted = false,
                    Error = err,
                    ActivityState = session.ActivityState,
                }, statusCode: StatusCodes.Status502BadGateway);

            if (!req.WaitForIdle)
                return Results.Json(body);

            var sw = Stopwatch.StartNew();
            var deadline = DateTime.UtcNow.AddMilliseconds(req.TimeoutMs);
            string finalState = body.ActivityState;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(750);
                // Gateway Cleanup Phase 2: the idle poll rides the tunnel too (snapshot verb, tunnel-first);
                // a null return falls back to the byte-identical HTTP dial. Post-cut the HTTP arm is removed.
                var cur = await SnapshotTunnelFirstAsync(sendCommand, director, sid, CancellationToken.None);
                if (cur is null) { finalState = "Exited"; break; }
                finalState = cur.ActivityState;
                if (finalState is "Idle" or "WaitingForInput" or "Exited" or "Failed") break;
            }
            sw.Stop();

            // Fetch new output since prompt was sent. Gateway Cleanup Phase 2: buffer verb, tunnel-first.
            string output = "";
            var buf = await BufferTunnelFirstAsync(sendCommand, director, sid, 500, body.BufferCursor, CancellationToken.None);
            if (buf is not null) output = buf.Text;

            body.WaitStatus = finalState switch
            {
                "Idle" or "WaitingForInput" => "idle",
                "Exited" or "Failed" => "failed",
                _ => "timeout",
            };
            body.Output = output;
            body.ActivityState = finalState;
            return Results.Json(body);
        });

        app.MapPost("/sessions/{sid}/interrupt", async (string sid) =>
        {
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });

            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "interrupt", sid, null, CancellationToken.None);
            var ok = streamResult is not null && streamResult.Ok;
            return ok
                ? Results.Json(new { accepted = true })
                : Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        app.MapPost("/sessions/{sid}/escape", async (string sid) =>
        {
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });

            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "escape", sid, null, CancellationToken.None);
            var ok = streamResult is not null && streamResult.Ok;
            return ok
                ? Results.Json(new { accepted = true })
                : Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        // Phone image upload: the browser POSTs the image to the Gateway (its origin); we
        // forward the bytes to the owning Director, which files it into its screenshots
        // folder (same machine as the session) and returns the saved absolute path.
        app.MapPost("/sessions/{sid}/upload-image", async (string sid, HttpContext ctx) =>
        {
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });

            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "expected multipart/form-data with an image file field 'file'" });

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "no image uploaded; use form field 'file'" });

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ctx.RequestAborted);

            FileLog.Write($"[GatewayEndpoints] POST upload-image: sid={sid}, director={director.DirectorId}, bytes={ms.Length}");

            var bytes = ms.ToArray();

            // Gateway Cleanup (Phase 2): upload the image DOWN the tunnel in bounded chunks - begin, then a
            // chunk per UploadChunkRawBytes, then complete - so a whole photo never rides as one large unary
            // message that would monopolize the shared tunnel (Architect ruling 2). A null begin means no
            // stream (mode off / Director not stream-connected), so fall through to the HTTP dial below. A
            // non-null-but-failed step is authoritative and collapses to 502 (a retryable upload failure).
            var uploadId = Guid.NewGuid().ToString("N");
            var begin = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "upload-image-begin", sid,
                new UploadImageBeginRequest { UploadId = uploadId, FileName = file.FileName, TotalBytes = bytes.Length }, ctx.RequestAborted);
            if (begin is not null)
            {
                if (!begin.Ok)
                    return Results.Json(new { error = DirectorCommandRouter.DescribeFailure(begin) }, statusCode: StatusCodes.Status502BadGateway);

                for (var off = 0; off < bytes.Length; off += DirectorStreamLimits.UploadChunkRawBytes)
                {
                    var len = Math.Min(DirectorStreamLimits.UploadChunkRawBytes, bytes.Length - off);
                    var chunk = new UploadImageChunkRequest
                    {
                        UploadId = uploadId,
                        Seq = off / DirectorStreamLimits.UploadChunkRawBytes,
                        BytesBase64 = Convert.ToBase64String(bytes, off, len),
                    };
                    var cr = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "upload-image-chunk", sid, chunk, ctx.RequestAborted);
                    if (cr is null || !cr.Ok)
                        return Results.Json(new { error = cr is null ? "tunnel dropped mid-upload" : DirectorCommandRouter.DescribeFailure(cr) }, statusCode: StatusCodes.Status502BadGateway);
                }

                var done = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "upload-image-complete", sid,
                    new UploadImageCompleteRequest { UploadId = uploadId }, ctx.RequestAborted);
                if (done is null || !done.Ok || string.IsNullOrEmpty(done.BodyJson))
                    return Results.Json(new { error = done is null ? "tunnel dropped mid-upload" : DirectorCommandRouter.DescribeFailure(done) }, statusCode: StatusCodes.Status502BadGateway);

                return Results.Content(done.BodyJson, "application/json"); // { path, fileName }
            }

            // Post-cut: tunnel-only. A null begin means the Director is not connected -> 502.
            return Results.Json(new { error = "director not connected to the tunnel" }, statusCode: StatusCodes.Status502BadGateway);
        });

        app.MapGet("/directors/{id}/repos", async (string id, CancellationToken ct) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (repos-list verb, director-level so SessionId
            // is ""); a null return means no stream, so fall back to the byte-identical HTTP dial. A non-Ok stream
            // result collapses to 502, exactly as the HTTP path surfaced a null.
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "repos-list", "", null, ct);
            if (sr is not null)
            {
                if (!sr.Ok) return Results.StatusCode(StatusCodes.Status502BadGateway);
                return Results.Json(DirectorCommandRouter.ReadBody<List<RepositoryDto>>(sr) ?? new List<RepositoryDto>());
            }

            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        // Issue #330: pull a registered Director's machine facts (tool inventory with
        // versions + launcher presence/port) through the existing proxy leg. Pulled on
        // demand rather than pushed in registration/heartbeat: the inventory is large and
        // changes rarely, so riding the 15s heartbeat would bloat the hot path for a fact
        // a consumer reads occasionally.
        app.MapGet("/directors/{id}/facts", async (string id, CancellationToken ct) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (facts verb, director-level).
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "facts", "", null, ct);
            if (sr is not null)
            {
                if (!sr.Ok) return Results.StatusCode(StatusCodes.Status502BadGateway);
                var body = DirectorCommandRouter.ReadBody<DirectorFactsDto>(sr);
                if (body is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
                return Results.Json(body);
            }

            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        app.MapPost("/directors/{id}/sessions", async (string id, NewSessionRequest req) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });
            if (req is null || string.IsNullOrWhiteSpace(req.RepoPath))
                return Results.BadRequest(new { error = "repoPath is required" });

            FileLog.Write($"[GatewayEndpoints] POST /directors/{id}/sessions: repo={req.RepoPath}, agent={req.Agent}");

            // Issue #1177 (Phase 1): try the target Director's stream first; on a null return (no stream)
            // fall back to the HTTP create. A non-Ok stream result (validation/creation failure) collapses
            // to 502, exactly as the HTTP path surfaces a Director 4xx/5xx.
            SessionDto? body;
            string? err;
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "create", "", req, CancellationToken.None);
            if (streamResult is null)
            {
                body = null;
                err = "director not connected to the tunnel";
            }
            else
            {
                body = streamResult.Ok ? DirectorCommandRouter.ReadBody<SessionDto>(streamResult) : null;
                err = streamResult.Ok ? null : DirectorCommandRouter.DescribeFailure(streamResult);
            }
            if (body is null)
                return Results.Problem(err ?? "failed", statusCode: StatusCodes.Status502BadGateway);
            return Results.Json(body, statusCode: 201);
        });

        app.MapDelete("/directors/{id}/repos", async (string id, string? path, CancellationToken ct) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "path is required" });

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (repo-delete verb, director-level). The
            // Director core returns { removed } in its body; a non-Ok stream result collapses to 502.
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "repo-delete", "", new RepoDeleteRequest { Path = path }, ct);
            if (sr is not null)
            {
                if (!sr.Ok) return Results.StatusCode(StatusCodes.Status502BadGateway);
                return Results.Content(sr.BodyJson ?? "{\"removed\":false}", "application/json");
            }

            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        // Gateway Cleanup CUT RESTORATION (SB-4a): register a repository explicitly in the recent list. Rides
        // the repo-add verb (director-level). The Director core validates directory-existence and returns
        // { added, repo }; added selects the old route's 201 (newly added) vs 200 (already present). A typed
        // failure preserves 400; a null result (Director not tunnel-connected) is 502.
        app.MapPost("/directors/{id}/repos", async (string id, RepoAddRequest req, CancellationToken ct) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });
            if (req is null || string.IsNullOrWhiteSpace(req.Path)) return Results.BadRequest(new { error = "path is required" });

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "repo-add", "", req, ct);
            if (sr is null || !sr.Ok) return MapDirectorFailure(sr);
            var body = DirectorCommandRouter.ReadBody<RepoAddResponse>(sr);
            if (body is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(body, statusCode: body.Added ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        });

        // Gateway Cleanup CUT RESTORATION (SB-4a): rename a registered repository (path is the identity). Rides
        // the repo-rename verb (director-level). A path not in the registry is the executor's NotFound -> 404;
        // a null result (Director not tunnel-connected) is 502.
        app.MapPatch("/directors/{id}/repos", async (string id, RepoRenameRequest req, CancellationToken ct) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });
            if (req is null || string.IsNullOrWhiteSpace(req.Path)) return Results.BadRequest(new { error = "path is required" });
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name is required" });

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "repo-rename", "", req, ct);
            if (sr is null || !sr.Ok) return MapDirectorFailure(sr);
            var body = DirectorCommandRouter.ReadBody<RepositoryDto>(sr);
            if (body is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(body);
        });

        // Gateway Cleanup CUT RESTORATION (SB-4a): the enriched per-repo overview the Repositories page reads.
        // Rides the repos-overview verb (director-level). A null result (Director not tunnel-connected) is 502.
        app.MapGet("/directors/{id}/repos/overview", async (string id, CancellationToken ct) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "repos-overview", "", null, ct);
            if (sr is null || !sr.Ok) return MapDirectorFailure(sr);
            return Results.Json(DirectorCommandRouter.ReadBody<List<RepoOverviewDto>>(sr) ?? new List<RepoOverviewDto>());
        });

        app.MapGet("/directors/{id}/coaching/categories", async (string id, CancellationToken ct) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (coaching-categories verb, director-level).
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "coaching-categories", "", null, ct);
            if (sr is not null)
            {
                if (!sr.Ok) return Results.StatusCode(StatusCodes.Status502BadGateway);
                return Results.Json(DirectorCommandRouter.ReadBody<List<CoachingCategoryDto>>(sr) ?? new List<CoachingCategoryDto>());
            }

            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        app.MapGet("/directors/{id}/claude-sessions", async (string id, CancellationToken ct) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (claude-sessions verb, director-level; no
            // repo filter on this route, so the payload is empty).
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "claude-sessions", "", null, ct);
            if (sr is not null)
            {
                if (!sr.Ok) return Results.StatusCode(StatusCodes.Status502BadGateway);
                return Results.Json(DirectorCommandRouter.ReadBody<List<ClaudeSessionDto>>(sr) ?? new List<ClaudeSessionDto>());
            }

            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        app.MapGet("/directors/{id}/handovers", async (string id, CancellationToken ct) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (handovers-list verb, director-level; this
            // route has no repo filter, so the payload is empty).
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "handovers-list", "", null, ct);
            if (sr is not null)
            {
                if (!sr.Ok) return Results.StatusCode(StatusCodes.Status502BadGateway);
                return Results.Json(DirectorCommandRouter.ReadBody<List<HandoverDto>>(sr) ?? new List<HandoverDto>());
            }

            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        app.MapGet("/directors/{id}/handovers/content", async (string id, string? path, CancellationToken ct) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "path is required" });

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (handovers-content verb, director-level; the
            // ?path query rides in the payload). A non-Ok stream result collapses to 502, matching the HTTP null.
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "handovers-content", "", new HandoverContentRequest { Path = path }, ct);
            if (sr is not null)
            {
                if (!sr.Ok) return Results.StatusCode(StatusCodes.Status502BadGateway);
                var body = DirectorCommandRouter.ReadBody<HandoverContentDto>(sr);
                if (body is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
                return Results.Json(body);
            }

            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        // Gateway Cleanup CUT RESTORATION (SB-4a): create a standalone saved-handover document. Rides the
        // handover-create verb (director-level). Success is the old route's 201; a typed failure preserves 400;
        // a null result (Director not tunnel-connected) is 502.
        app.MapPost("/directors/{id}/handovers", async (string id, HandoverCreateRequest req, CancellationToken ct) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });
            if (req is null || string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "title is required" });
            if (string.IsNullOrWhiteSpace(req.Content)) return Results.BadRequest(new { error = "content is required" });

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "handover-create", "", req, ct);
            if (sr is null || !sr.Ok) return MapDirectorFailure(sr);
            var body = DirectorCommandRouter.ReadBody<HandoverDto>(sr);
            if (body is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(body, statusCode: StatusCodes.Status201Created);
        });

        // Gateway Cleanup CUT RESTORATION (SB-4a): delete a saved-handover document. Rides the handover-delete
        // verb (director-level; the ?path query rides in the payload). A path outside the handover folder is the
        // executor's BadRequest -> 400; a missing file its NotFound -> 404; a null result (Director not
        // tunnel-connected) is 502.
        app.MapDelete("/directors/{id}/handovers", async (string id, string? path, CancellationToken ct) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "path is required" });

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "handover-delete", "", new RepoDeleteRequest { Path = path }, ct);
            if (sr is null || !sr.Ok) return MapDirectorFailure(sr);
            return Results.Content(sr.BodyJson ?? "{\"removed\":true}", "application/json");
        });

        app.MapGet("/directors/{id}/fs/list", async (string id, string? path, CancellationToken ct) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (fs-list verb, director-level; the ?path
            // query rides in the payload). A non-Ok stream result (e.g. the Director core's bad-path BadRequest)
            // collapses to 502, exactly as the HTTP path surfaced a null.
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "fs-list", "", new FsListRequest { Path = path }, ct);
            if (sr is not null)
            {
                if (!sr.Ok) return Results.StatusCode(StatusCodes.Status502BadGateway);
                var body = DirectorCommandRouter.ReadBody<DirectoryListingDto>(sr);
                if (body is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
                return Results.Json(body);
            }

            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        app.MapPost("/directors/{id}/sessions/github", async (string id, GitHubSessionRequest req) =>
        {
            var d = registry.Get(id);
            if (d is null) return Results.NotFound(new { error = "director not found" });
            if (req is null || string.IsNullOrWhiteSpace(req.Owner) || string.IsNullOrWhiteSpace(req.Repo))
                return Results.BadRequest(new { error = "owner and repo are required" });

            FileLog.Write($"[GatewayEndpoints] POST /directors/{id}/sessions/github: {req.Owner}/{req.Repo} mode={req.TriggerMode}");

            // Gateway Cleanup Phase 2: ride the target Director's stream first (create-from-github verb,
            // director-level so SessionId is ""); on a null return (no stream) fall back to the HTTP create. A
            // non-Ok stream result collapses to 502, exactly as the HTTP path surfaced a Director 4xx/5xx.
            SessionDto? body;
            string? err;
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "create-from-github", "", req, CancellationToken.None);
            if (streamResult is null)
            {
                body = null;
                err = "director not connected to the tunnel";
            }
            else
            {
                body = streamResult.Ok ? DirectorCommandRouter.ReadBody<SessionDto>(streamResult) : null;
                err = streamResult.Ok ? null : DirectorCommandRouter.DescribeFailure(streamResult);
            }
            if (body is null)
                return Results.Problem(err ?? "failed", statusCode: StatusCodes.Status502BadGateway);
            return Results.Json(body, statusCode: 201);
        });

        // Destructive-call gate (issue #212 W6/L4). A Director shutdown takes down every
        // claude.exe under it, so the request must (a) state a reason, and (b) when the
        // Director is reachable and has live sessions, confirm their count - a caller may
        // not kill sessions it did not know existed. Every branch logs loudly: the 2026-06-06
        // post-mortem found the force-kill path left no trace at all.
        app.MapDelete("/directors/{id}", async (HttpContext ctx, string id) =>
        {
            // Body read by hand instead of [FromBody]: an Accepts(application/json)
            // constraint would bounce body-less DELETEs off route matching, and a
            // body-less DELETE of an unknown id must still 404.
            ShutdownDirectorRequest body;
            try
            {
                body = await ctx.Request.ReadFromJsonAsync<ShutdownDirectorRequest>() ?? new ShutdownDirectorRequest();
            }
            catch (System.Text.Json.JsonException)
            {
                return Results.BadRequest(new { error = "invalid JSON body" });
            }
            catch (InvalidOperationException)
            {
                // Not a JSON request (typically a body-less DELETE): empty request.
                body = new ShutdownDirectorRequest();
            }

            // Identify the caller: the tailnet IP for remote callers (phone), and additionally
            // the owning process for loopback callers like the Cockpit (issue #212 L3).
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            var localPeer = Core.Network.LoopbackPeerResolver.Resolve(ctx.Connection.RemotePort, ctx.Connection.LocalPort);
            var caller = localPeer is null ? ip : $"{ip} [{localPeer}]";
            FileLog.Write($"[GatewayEndpoints] DELETE director: id={id} force={body.Force} " +
                $"confirmSessions={(body.ConfirmSessions?.ToString() ?? "-")} reason=\"{Truncate(body.Reason)}\" client={caller}");

            var director = registry.Get(id);
            if (director is null)
                return Results.NotFound(new { error = "director not found" });

            if (string.IsNullOrWhiteSpace(body.Reason))
            {
                FileLog.Write($"[GatewayEndpoints] DELETE director REJECTED (no reason): id={id} client={caller}");
                return Results.BadRequest(new { error = "reason is required: state why this Director is being shut down" });
            }

            // Post-cut: read the live session list from the push store (it carries the same SessionDto incl.
            // Status). A Director with no fresh push is not connected to the tunnel, so the live count is
            // unknowable and the session gate is skipped below.
            var cachedSessions = pushedSessions?.TryGetFresh(director.DirectorId, streamStaleResolved);
            var sessions = cachedSessions?.ToList();
            if (sessions is not null)
            {
                var live = sessions
                    .Where(s => !string.Equals(s.Status, "Exited", StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(s.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (live.Count > 0 && body.ConfirmSessions != live.Count)
                {
                    FileLog.Write($"[GatewayEndpoints] DELETE director BLOCKED by session gate: id={id} " +
                        $"liveSessions={live.Count} confirmSessions={(body.ConfirmSessions?.ToString() ?? "-")} client={caller}");
                    return Results.Json(new
                    {
                        error = $"director has {live.Count} live session(s); re-send with confirmSessions={live.Count} to proceed",
                        liveSessionCount = live.Count,
                        sessions = live.Select(s => new { s.SessionId, s.Name, s.RepoPath }).ToList(),
                    }, statusCode: StatusCodes.Status409Conflict);
                }
            }
            else
            {
                // Unreachable Director: the live count is unknowable, and an unreachable
                // Director is exactly the one an operator must still be able to stop.
                FileLog.Write($"[GatewayEndpoints] DELETE director: id={id} live-session count UNKNOWN (director unreachable); session gate skipped");
            }

            // Gateway Cleanup Phase 2: the Gateway-initiated REMOTE stop rides the tunnel (shutdown verb,
            // director-level so SessionId is ""), tunnel-first; a null return falls back to the byte-identical
            // HTTP dial. POST /shutdown stays on the Director loopback floor for the local launcher; this tunnel
            // verb triggers the same in-process self-shutdown. Post-cut the HTTP arm is removed.
            var shutdownSr = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "shutdown", "", null, CancellationToken.None);
            var ok = shutdownSr is not null && shutdownSr.Ok;
            if (ok)
            {
                FileLog.Write($"[GatewayEndpoints] DELETE director: id={id} pid={director.Pid} graceful shutdown accepted");
                return Results.Json(new { accepted = true });
            }

            if (body.Force)
            {
                FileLog.Write($"[GatewayEndpoints] DELETE director FORCE-KILL: id={id} pid={director.Pid} " +
                    $"tree=true reason=\"{Truncate(body.Reason)}\" client={caller}");
                try
                {
                    var proc = Process.GetProcessById(director.Pid);
                    proc.Kill(entireProcessTree: true);
                    FileLog.Write($"[GatewayEndpoints] DELETE director FORCE-KILL done: id={id} pid={director.Pid}");
                    return Results.Json(new { accepted = true, killed = true });
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[GatewayEndpoints] DELETE director FORCE-KILL FAILED: id={id} pid={director.Pid} error={ex.Message}");
                    return Results.Problem("could not kill process: " + ex.Message, statusCode: 500);
                }
            }

            FileLog.Write($"[GatewayEndpoints] DELETE director: id={id} graceful shutdown failed and force=false; nothing stopped");
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        app.MapGet("/sessions/{sid}/summary", async (string sid, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Gateway Cleanup (Phase 2, PR C): tunnel-first; a null return falls back to the HTTP dial below,
            // byte-identical. The Director's summary core sets DirectorId in its body, so the pass-through matches.
            if (director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "summary", sid, null, ct);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        // Read-only source-control snapshot proxy (issue #1266): forwards to whichever Director owns the
        // session and returns its GET /sessions/{sid}/git response (branch, ahead/behind, last commit, and
        // the additive per-file staged/unstaged lists) for the Cockpit's Source Control tab. This route is
        // READ-ONLY: it does not proxy any git WRITE route (stage / unstage / discard / commit stay
        // desktop-only). It self-checks HasValidToken(ctx, token, devices) so a phone or browser per-device
        // key is accepted, not only the shared machine token - the same 401-avoidance every browser-facing
        // session route needs (the device-blind check once bit the dictation route, issue #1045) - and so
        // the route stays gated even when the host-wide auth middleware is off.
        app.MapGet("/sessions/{sid}/git", async (string sid, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token, devices))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            // Issue #1240: pass the owner cache so a warm session is resolved with ONE Director probe
            // instead of a full fleet fan-out (the same fast path every other per-session route now uses).
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Gateway Cleanup (Phase 2, PR C): tunnel-first (verb "git-status"); a null return falls back to the
            // HTTP dial below, byte-identical. The Ok body IS the GitSnapshot JSON, passed through unchanged.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "git-status", sid, null, ctx.RequestAborted);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        // Handover info proxy (issue #1214). Forwards to whichever Director owns the session and returns
        // the desktop "Handover info" identity block (name, session id, repo, director id, machine,
        // version) for a browser. Gated by the same Bearer/device-key auth as every other session route
        // (the global AuthMiddleware 401s a credential-less request before it reaches here). The Director
        // address is never leaked: this returns HandoverInfoDto, which carries no Control API endpoint,
        // and the resolved ControlEndpoint stays server-side. 404 when the session is unknown to every
        // Director; 502 when the owning Director is unreachable (never a silent empty body).
        app.MapGet("/sessions/{sid}/handover", async (string sid, CancellationToken ct) =>
        {
            // Issue #1240: resolve the owner through the same cache fast path as every other per-session route.
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Gateway Cleanup (Phase 2, PR C): tunnel-first; a null return falls back to the HTTP dial below,
            // byte-identical. The Director's handover core sets DirectorId in its body, so the pass-through matches.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "handover", sid, null, ct);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        // Recap proxy. Both endpoints transparently forward to whichever Director owns the
        // session. The Director side does the heavy lifting (claude --print + cache); this
        // is just routing.
        app.MapGet("/sessions/{sid}/recap", async (string sid, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Gateway Cleanup (Phase 2, PR C): tunnel-first (read the cached recap); a null return falls back to
            // the HTTP dial below, byte-identical. This is the READ; the slow generate (POST) is handled separately.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "recap", sid, null, ct);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : Results.StatusCode(StatusCodes.Status502BadGateway);
        });

        app.MapPost("/sessions/{sid}/recap", async (string sid, HttpContext ctx) =>
        {
            var (director, session) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var model = ctx.Request.Query["model"].ToString();
            FileLog.Write($"[GatewayEndpoints] POST /recap: sid={sid}, director={director.DirectorId}, model={model ?? "(default)"}");
            // Gateway Cleanup (Phase 2, PR C): tunnel-first. Like wingman-ask this is a SLOW LLM call, so the
            // request ct (ctx.RequestAborted) threads into the SignalR invocation (no per-invocation timeout;
            // keep-alive pings sustain the long await) - synchronous browser contract byte-identical. A null
            // return falls back to the HTTP dial below. The Ok body IS the RecapResponse JSON, returned 201 as before.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "recap-generate", sid,
                new RecapGenerateRequest { Model = model }, ctx.RequestAborted);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json", null, StatusCodes.Status201Created)
                : Results.Problem("recap failed", statusCode: StatusCodes.Status502BadGateway);
        });

        app.MapPost("/handover", async (HandoverRequest req) =>
        {
            // Gateway-side /handover dispatches to whichever Director owns the source
            // session. Same-Director case: proxy the request to that Director. Cross-Director
            // case (toDirectorId set + different from source): read the prose context from
            // source-side, then spawn the target session on the target Director with the
            // context as PrePrompt.

            if (req is null || string.IsNullOrEmpty(req.FromSessionId))
                return Results.BadRequest(new { error = "fromSessionId is required" });
            if (string.IsNullOrEmpty(req.ToSessionId) && string.IsNullOrEmpty(req.ToRepoPath))
                return Results.BadRequest(new { error = "exactly one of toSessionId or toRepoPath is required" });

            FileLog.Write($"[GatewayEndpoints] POST /handover: from={req.FromSessionId} toSid={req.ToSessionId} toRepo={req.ToRepoPath} toDir={req.ToDirectorId}");

            var (sourceDirector, sourceSession) = await LocateSessionAsync(registry, req.FromSessionId, pushedSessions, streamStaleResolved, owners);
            if (sourceSession is null || sourceDirector is null)
                return Results.NotFound(new { error = "source session not found across any director" });

            DirectorDto? targetDirector = null;
            if (!string.IsNullOrEmpty(req.ToDirectorId)
                && !string.Equals(req.ToDirectorId, sourceDirector.DirectorId, StringComparison.OrdinalIgnoreCase))
            {
                targetDirector = registry.Get(req.ToDirectorId);
                if (targetDirector is null)
                    return Results.NotFound(new { error = "target director not found" });
            }

            if (targetDirector is null)
            {
                // Same-Director: proxy the entire request. Gateway Cleanup Phase 2: ride the tunnel first
                // (handover-generate verb, director-level so SessionId is ""); a null return falls back to the
                // byte-identical HTTP proxy. A non-Ok stream result collapses to 502 exactly as the HTTP null did.
                HandoverResponse? body; string? err;
                var hgSr = await DirectorCommandRouter.TrySendAsync(sendCommand, sourceDirector.DirectorId, "handover-generate", "", req, CancellationToken.None);
                if (hgSr is null)
                {
                    body = null; err = "source director not connected to the tunnel";
                }
                else
                {
                    body = hgSr.Ok ? DirectorCommandRouter.ReadBody<HandoverResponse>(hgSr) : null;
                    err = hgSr.Ok ? null : DirectorCommandRouter.DescribeFailure(hgSr);
                }
                if (body is null)
                    return Results.Problem(err ?? "handover failed", statusCode: StatusCodes.Status502BadGateway);
                if (body.TargetSession is not null) body.TargetSession.DirectorId = sourceDirector.DirectorId;
                return Results.Json(body, statusCode: 201);
            }

            // Cross-Director path. Only the "new session in target Director" form is supported here.
            if (!string.IsNullOrEmpty(req.ToSessionId))
                return Results.BadRequest(new { error = "cross-director handover to an existing session is not supported in v1; use toRepoPath instead" });
            if (string.IsNullOrEmpty(req.ToRepoPath))
                return Results.BadRequest(new { error = "toRepoPath is required for cross-director handover" });

            // Gateway Cleanup Phase 2: read the source session's handover context over the tunnel
            // (handover-context verb), falling back to the byte-identical HTTP GET when the source has no stream.
            // Post-cut: tunnel-only. A null result means the source Director is not connected -> 502.
            var ctxSr = await DirectorCommandRouter.TrySendAsync(sendCommand, sourceDirector.DirectorId, "handover-context",
                req.FromSessionId, new HandoverContextRequest { ExtraContext = req.ExtraContext }, CancellationToken.None);
            if (ctxSr is null)
                return Results.Problem("source director is not connected to the tunnel", statusCode: 502);
            if (!ctxSr.Ok)
                return Results.Problem("failed to read handover-context from source director: " + DirectorCommandRouter.DescribeFailure(ctxSr), statusCode: 502);
            string contextText = DirectorCommandRouter.ReadBody<HandoverContextResponse>(ctxSr)?.Text ?? "";

            var spawnReq = new NewSessionRequest
            {
                RepoPath = req.ToRepoPath,
                Agent = req.ToAgent,
                PrePrompt = contextText,
            };
            // Gateway Cleanup Phase 2: create the target over the tunnel (create verb, director-level), tunnel-first;
            // the dedicated 20s HTTP client is the fallback pre-cut (the tunnel unary has no 2s aggregate timeout).
            // Post-cut: tunnel-only. A null result means the target Director is not connected -> 502.
            var spawnSr = await DirectorCommandRouter.TrySendAsync(sendCommand, targetDirector.DirectorId, "create", "", spawnReq, CancellationToken.None);
            if (spawnSr is null)
                return Results.Problem("target director is not connected to the tunnel", statusCode: 502);
            if (!spawnSr.Ok)
                return Results.Problem($"target director returned {DirectorCommandRouter.DescribeFailure(spawnSr)}", statusCode: 502);
            SessionDto? newSession = DirectorCommandRouter.ReadBody<SessionDto>(spawnSr);
            if (newSession is not null) newSession.DirectorId = targetDirector.DirectorId;

            return Results.Json(new HandoverResponse
            {
                Accepted = true,
                TargetSession = newSession,
                ContextSent = contextText,
                ArchivedAt = null, // archive is written only on the source side; cross-director skips
            }, statusCode: 201);
        });

        // Issue #1229: mint a human-issued broadcast grant. Reaching beyond a sender's own team needs
        // one of these. This endpoint sits behind the host-wide auth middleware (the shared token or a
        // per-device key) and has NO Director relay, so an agent - which can only reach its own Director,
        // never the Gateway directly - cannot mint its own grant. A human tool holding the token mints
        // one and hands the id to the broadcaster. (A dedicated human-approval surface can tighten who
        // may mint in a later pass.)
        app.MapPost("/fleet/broadcast-grants", () =>
        {
            var grantId = broadcastGovernor.MintGrant();
            FileLog.Write("[GatewayEndpoints] POST /fleet/broadcast-grants: minted a broadcast grant");
            return Results.Json(new { grantId, expiresInSeconds = (int)TimeSpan.FromMinutes(10).TotalSeconds });
        });

        app.MapPost("/fanout", async (FanoutRequest req) =>
        {
            if (req is null || req.SessionIds is null || req.SessionIds.Count == 0)
                return Results.BadRequest(new { error = "sessionIds is required" });
            if (string.IsNullOrEmpty(req.Text))
                return Results.BadRequest(new { error = "text is required" });

            FileLog.Write($"[GatewayEndpoints] POST fanout: count={req.SessionIds.Count}, len={req.Text.Length}, from={req.FromSessionId}");

            // Resolve all directors once up-front, capturing each target's broadcast scope (issue #1229).
            var directorBySession = new Dictionary<string, DirectorDto>();
            var targetScopes = new List<(string SessionId, BroadcastScope Scope)>();
            foreach (var sid in req.SessionIds)
            {
                var (d, s) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, owners);
                if (d is not null && s is not null)
                {
                    directorBySession[sid] = d;
                    targetScopes.Add((sid, BuildBroadcastScope(d, s)));
                }
            }

            // Issue #1229: the Hub decides whether this broadcast may reach every recipient. A broadcast
            // that stays inside the sender's own team (its group, or - for a solo session - the same repo
            // on the same machine) is free; one that reaches beyond it is refused unless a human grant
            // (plus a reason) authorizes it. The sender's scope is read from the Gateway's OWN fleet view,
            // never trusted from the request body.
            BroadcastScope? senderScope = null;
            if (!string.IsNullOrWhiteSpace(req.FromSessionId))
            {
                var (sd, ss) = await LocateSessionAsync(registry, req.FromSessionId, pushedSessions, streamStaleResolved, owners);
                if (sd is not null && ss is not null) senderScope = BuildBroadcastScope(sd, ss);
            }

            // Only resolve a grant when a recipient is genuinely out of team and a reason accompanies it,
            // so a valid grant is not spent validating a malformed request.
            var anyOutOfScope = senderScope is null
                ? targetScopes.Count > 0
                : targetScopes.Any(t => !senderScope.Value.Includes(t.Scope));
            var hasValidGrant = anyOutOfScope
                && !string.IsNullOrWhiteSpace(req.Reason)
                && broadcastGovernor.IsGrantValid(req.GrantId);

            var decision = FleetBroadcastPolicy.Evaluate(senderScope, targetScopes, hasValidGrant, req.Reason);
            if (!decision.Allowed)
            {
                FileLog.Write($"[GatewayEndpoints] fanout DENIED ({decision.Outcome}): from={req.FromSessionId}, targets={req.SessionIds.Count}, outOfScope={decision.OutOfScopeTargetIds.Count}, reason='{req.Reason}'");
                return Results.Json(new FanoutResponse
                {
                    Denied = true,
                    DeniedReason = decision.DeniedReason,
                    StartedAt = DateTime.UtcNow,
                    FinishedAt = DateTime.UtcNow,
                });
            }

            // Rate-limit even an in-team broadcast so a runaway agent cannot storm the fleet in a loop.
            var rate = broadcastGovernor.TryRecordSend(req.FromSessionId);
            if (!rate.Allowed)
            {
                FileLog.Write($"[GatewayEndpoints] fanout RATE-LIMITED: from={req.FromSessionId}, limit={rate.LimitPerWindow}/{rate.WindowSeconds}s");
                return Results.Json(new FanoutResponse
                {
                    Denied = true,
                    DeniedReason = $"Too many broadcasts in a short time (limit {rate.LimitPerWindow} per {rate.WindowSeconds} seconds). Wait a moment and try again. See issue #1229.",
                    StartedAt = DateTime.UtcNow,
                    FinishedAt = DateTime.UtcNow,
                });
            }

            FileLog.Write($"[GatewayEndpoints] fanout ALLOWED ({decision.Outcome}): from={req.FromSessionId}, inScope={decision.InScopeTargetIds.Count}, outOfScope={decision.OutOfScopeTargetIds.Count}");

            var startedAt = DateTime.UtcNow;

            // Send to all in parallel
            var sendTasks = req.SessionIds.Select(async sid =>
            {
                var sw = Stopwatch.StartNew();
                if (!directorBySession.TryGetValue(sid, out var director))
                {
                    sw.Stop();
                    return new FanoutResult
                    {
                        SessionId = sid,
                        Status = "not_found",
                        Error = "session not found",
                        ElapsedMs = sw.ElapsedMilliseconds,
                    };
                }

                var promptReq = new PromptRequest { Text = req.Text, AppendEnter = req.AppendEnter };
                // Gateway Cleanup Phase 2: fanout delivery rides the tunnel (prompt verb, tunnel-first);
                // a null return falls back to the byte-identical HTTP dial. Post-cut the HTTP arm is removed.
                bool ok; PromptResponse? body; string? err;
                var deliverSr = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "prompt", sid, promptReq, CancellationToken.None);
                if (deliverSr is null)
                {
                    ok = false; body = null; err = "director not connected to the tunnel";
                }
                else
                {
                    ok = deliverSr.Ok;
                    body = deliverSr.Ok ? DirectorCommandRouter.ReadBody<PromptResponse>(deliverSr) : null;
                    err = deliverSr.Ok ? null : DirectorCommandRouter.DescribeFailure(deliverSr);
                }
                if (!ok || body is null)
                {
                    sw.Stop();
                    return new FanoutResult
                    {
                        SessionId = sid,
                        DirectorId = director.DirectorId,
                        Status = "failed",
                        Error = err,
                        ElapsedMs = sw.ElapsedMilliseconds,
                    };
                }

                if (!req.WaitForIdle)
                {
                    sw.Stop();
                    return new FanoutResult
                    {
                        SessionId = sid,
                        DirectorId = director.DirectorId,
                        Status = "idle",
                        Output = "",
                        ElapsedMs = sw.ElapsedMilliseconds,
                    };
                }

                // Poll for idle. Gateway Cleanup Phase 2: snapshot verb, tunnel-first (HTTP fallback pre-cut).
                var deadline = DateTime.UtcNow.AddMilliseconds(req.TimeoutMs);
                string finalState = body.ActivityState;
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(750);
                    var cur = await SnapshotTunnelFirstAsync(sendCommand, director, sid, CancellationToken.None);
                    if (cur is null) { finalState = "Exited"; break; }
                    finalState = cur.ActivityState;
                    if (finalState is "Idle" or "WaitingForInput" or "Exited" or "Failed") break;
                }

                // Get the diff. Gateway Cleanup Phase 2: buffer verb, tunnel-first (HTTP fallback pre-cut).
                var buf = await BufferTunnelFirstAsync(sendCommand, director, sid, 500, body.BufferCursor, CancellationToken.None);
                var output = buf?.Text ?? "";

                sw.Stop();
                return new FanoutResult
                {
                    SessionId = sid,
                    DirectorId = director.DirectorId,
                    Status = finalState switch
                    {
                        "Idle" or "WaitingForInput" => "idle",
                        "Exited" or "Failed" => "failed",
                        _ => "timeout",
                    },
                    Output = output,
                    ElapsedMs = sw.ElapsedMilliseconds,
                };
            }).ToList();

            var results = await Task.WhenAll(sendTasks);

            return Results.Json(new FanoutResponse
            {
                Results = results.ToList(),
                StartedAt = startedAt,
                FinishedAt = DateTime.UtcNow,
            });
        });

        app.MapGet("/events", async (HttpContext ctx) =>
        {
            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["Connection"] = "keep-alive";

            var ct = ctx.RequestAborted;
            var queue = System.Threading.Channels.Channel.CreateUnbounded<GatewayEvent>();

            void OnAdded(DirectorDto d) => queue.Writer.TryWrite(new GatewayEvent("director.added", d.DirectorId));
            void OnRemoved(string id) => queue.Writer.TryWrite(new GatewayEvent("director.removed", id));

            registry.OnDirectorAdded += OnAdded;
            registry.OnDirectorRemoved += OnRemoved;

            // Flush the response start NOW (SSE convention): events are not replayed,
            // so a subscriber must be able to treat "headers received" as "attached".
            // Without this Kestrel holds the headers until the first event is written.
            await ctx.Response.Body.FlushAsync(ct);

            try
            {
                await foreach (var ev in queue.Reader.ReadAllAsync(ct))
                {
                    var line = $"event: {ev.Type}\ndata: {{\"id\":\"{ev.Id}\"}}\n\n";
                    await ctx.Response.WriteAsync(line, ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
            finally
            {
                registry.OnDirectorAdded -= OnAdded;
                registry.OnDirectorRemoved -= OnRemoved;
            }
        });

        app.MapPost("/directors", async (LaunchDirectorRequest? body) =>
        {
            body ??= new LaunchDirectorRequest();
            FileLog.Write($"[GatewayEndpoints] POST director: launch new instance");

            var exePath = ResolveDirectorExe();
            if (exePath is null)
                return Results.Problem("cc-director.exe not found on PATH or in standard install location", statusCode: 500);

            var beforeIds = registry.ListDirectors().Select(d => d.DirectorId).ToHashSet();

            try
            {
                // --skip-workspace-picker so the spawned Director never blocks on the
                // workspace-selection modal at startup (the whole point of a programmatic
                // spawn is to skip user interaction).
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                };
                psi.ArgumentList.Add("--skip-workspace-picker");
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                return Results.Problem("failed to start director: " + ex.Message, statusCode: 500);
            }

            // Poll for new director registration
            var deadline = DateTime.UtcNow.AddMilliseconds(body.TimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
                var newId = registry.ListDirectors().Select(d => d.DirectorId).FirstOrDefault(id => !beforeIds.Contains(id));
                if (newId is not null)
                {
                    var d = registry.Get(newId)!;
                    return Results.Json(new { directorId = d.DirectorId, pid = d.Pid });
                }
            }

            return Results.Problem("director did not register within timeout", statusCode: 504);
        });
    }

    // Automatic session roles (chunk 1): compute each session's role from the assembled fleet, then stamp
    // the presentation fold. Role: a session controlled by a session that is STILL ALIVE in the roster is a
    // Worker (this wins even if it also controls sub-workers - nesting keeps the Worker label); a non-worker
    // that controls at least one LIVE worker is a Manager; everything else is Standalone. The fold
    // (EffectiveColor/StateLabel/TriageBucket) then reads SessionRole to suppress a live Worker's red, so it
    // must run AFTER the role is known. NeedsYouSince keys off the final EffectiveColor, so it is stamped
    // here too (a suppressed Worker is not "red", so it never enters the needs-you clock).
    // Gateway Cleanup mission (Wave 4b): map a stored Mission to the wire MissionDto - the SAME contract the
    // Director's /missions routes return, so a client cannot tell a Gateway-native mission from a Director one.
    private static MissionDto ToMissionDto(Core.Sessions.Mission m) => new()
    {
        MissionId = m.MissionId,
        MissionName = m.MissionName,
        ParentMissionId = m.ParentMissionId,
    };

    private static void StampFleetRolesAndFold(List<SessionDto> all, Func<string, bool, DateTime?>? needsYouStampFor, Snooze.SnoozeRegistry? snoozeRegistry = null)
    {
        // Snooze Length mission: an EXPIRED snooze must read as "needs you" again. The registry is the
        // source of truth for the timer; the cleanest fold (issue #1177 keeps the Gateway the single
        // fold, decision #6) is to override OnHold=false on this aggregated DTO copy BEFORE the color /
        // label / triage are computed, so SessionOrdering.Classify puts the session straight back into
        // NeedsYou with no new classification logic. This is a pure, continuous overlay: while a snooze
        // is expired every read reports the session as un-held, so it never flickers back to "Snoozed"
        // between the moment it expires and the moment its Director confirms the clear. A DEAD Director's
        // session still carries its last-known OnHold=true in the cached roster; this overlay is exactly
        // what surfaces it anyway - the dead-man's-switch.
        var nowUtc = DateTime.UtcNow;
        if (snoozeRegistry is not null)
            foreach (var s in all)
                if (!string.IsNullOrEmpty(s.SessionId) && s.OnHold && snoozeRegistry.IsExpired(s.SessionId, nowUtc))
                {
                    s.OnHold = false;
                    // Phase 2: mark it as a RETURNED-from-snooze item so clients render a distinct
                    // "Snooze ended" badge and the phone push announces it once. Display-only metadata.
                    s.SnoozeExpired = true;
                }

        var liveIds = new HashSet<string>(StringComparer.Ordinal);
        var controllersWithLiveChild = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in all)
        {
            var alive = !string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase);
            if (alive && !string.IsNullOrEmpty(s.SessionId))
                liveIds.Add(s.SessionId);
            if (alive && s.IsControlled && !string.IsNullOrEmpty(s.ControllerSessionId))
                controllersWithLiveChild.Add(s.ControllerSessionId);
        }

        foreach (var s in all)
        {
            // Resolution precedence (chunk 2.5): an EXPLICIT role wins (sticky - auto-derivation never
            // overwrites it), and is the only way to be an Architect. Else Worker (controlled + controller
            // alive), else Manager (controls a live session - and it is a non-worker, non-architect here
            // because both of those were already resolved above), else Standalone.
            var explicitRole = SessionRoles.Normalize(s.ExplicitRole);
            if (explicitRole is not null)
                s.SessionRole = explicitRole;
            else if (s.IsControlled && !string.IsNullOrEmpty(s.ControllerSessionId) && liveIds.Contains(s.ControllerSessionId))
                s.SessionRole = SessionRoles.Worker;
            else if (!string.IsNullOrEmpty(s.SessionId) && controllersWithLiveChild.Contains(s.SessionId))
                s.SessionRole = SessionRoles.Manager;
            else
                s.SessionRole = SessionRoles.Standalone;

            var effectiveColor = SessionOrdering.EffectiveColor(s);
            s.EffectiveColor = effectiveColor;
            s.StateLabel = SessionOrdering.StateLabel(s);
            s.TriageBucket = SessionOrdering.Classify(s) switch
            {
                SessionOrdering.TriageBucket.NeedsYou => "needsYou",
                SessionOrdering.TriageBucket.OnHold => "onHold",
                _ => "active",
            };
            if (needsYouStampFor is not null)
            {
                var isRed = string.Equals(effectiveColor, "red", StringComparison.OrdinalIgnoreCase);
                s.NeedsYouSince = needsYouStampFor(s.SessionId, isRed);
            }
        }
    }

    // Gateway Cleanup CUT RESTORATION (SB-4a): map a tunnel command's null-or-failed result to the faithful
    // HTTP status the old REST route returned. A null result (Director not tunnel-connected) is 502; a typed
    // failure preserves the executor's BadRequest/NotFound as 400/404 so the repos/handover-management contract
    // - which the consumers and the re-added tests assert - is byte-identical to the pre-cut REST surface. Any
    // other status collapses to 502. Callers use this only on the not-Ok path (sr is null OR !sr.Ok).
    private static IResult MapDirectorFailure(DirectorCommandResult? sr)
    {
        if (sr is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
        return sr.Status switch
        {
            DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = sr.Error ?? "bad request" }),
            DirectorCommandStatus.NotFound => Results.NotFound(new { error = sr.Error ?? "not found" }),
            _ => Results.StatusCode(StatusCodes.Status502BadGateway),
        };
    }

    // Locate the Director that owns a session. Every session endpoint calls this first,
    // so it fans out to all Directors in parallel rather than scanning them one-by-one:
    // total latency is bounded by the slowest single lookup (~the client timeout) instead
    // of summing one timeout per Director. Exactly one Director should own a given sid.
    // Issue #1229: build the broadcast scope for a session from the Gateway's aggregated view. The
    // group id and repository come from the session record; the machine comes from the owning Director
    // (a Director-local session record leaves MachineName empty). This is the ground truth the Hub keys
    // its who-may-reach-whom decision on - never a role/mission claim carried in the request body.
    private static BroadcastScope BuildBroadcastScope(DirectorDto director, SessionDto session)
    {
        var machine = string.IsNullOrWhiteSpace(session.MachineName) ? (director.MachineName ?? "") : session.MachineName;
        return new BroadcastScope(session.MissionId?.ToString(), session.GroupId, session.RepoPath, machine);
    }

    // Gateway Cleanup mission, Phase 2: the idle-wait poll (single prompt AND fanout broadcast) reads the
    // owning session snapshot / terminal buffer over the tunnel FIRST (snapshot / buffer verbs), falling back
    // to the byte-identical HTTP dial only when the Director has no stream. Post-cut the HTTP arm is removed.
    // Shared by both poll sites so there is one tunnel-branch to prove.
    private static async Task<SessionDto?> SnapshotTunnelFirstAsync(
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand,
        DirectorDto director, string sid, CancellationToken ct)
    {
        var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "snapshot", sid, null, ct);
        return sr is not null && sr.Ok ? DirectorCommandRouter.ReadBody<SessionDto>(sr) : null;
    }

    private static async Task<BufferResponse?> BufferTunnelFirstAsync(
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand,
        DirectorDto director, string sid, int lines, long? since, CancellationToken ct)
    {
        var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "buffer", sid,
            new BufferRequest { Lines = lines, Raw = false, Since = since }, ct);
        return sr is not null && sr.Ok ? DirectorCommandRouter.ReadBody<BufferResponse>(sr) : null;
    }

    // Gateway Cleanup mission (post-cut): the pushed stream cache is the ONLY session locator. A Director with
    // no fresh push is not connected to the tunnel, so its sessions are unreachable and location returns null -
    // the same not-found the old HTTP-pull fallback produced when a Director was down. Kept Task-returning so
    // the many `await LocateSessionAsync(...)` call sites (and SessionVerbClient.ResolveAsync) are unchanged.
    internal static Task<(DirectorDto? director, SessionDto? session)> LocateSessionAsync(
        DirectorRegistry registry, string sid,
        Streaming.PushedSessionStore? pushedSessions, TimeSpan streamStale,
        SessionOwnerCache? owners = null)
    {
        if (pushedSessions is not null)
        {
            var located = pushedSessions.TryLocate(sid, streamStale);
            if (located is not null)
            {
                var (directorId, pushedSession) = located.Value;
                var owner = registry.Get(directorId);
                if (owner is not null)
                {
                    FileLog.Write($"[GatewayEndpoints] LocateSessionAsync: sid={sid} located=pushed-cache, director={directorId}");
                    owners?.Remember(sid, directorId);
                    return Task.FromResult<(DirectorDto?, SessionDto?)>((owner, pushedSession));
                }
            }
        }

        FileLog.Write($"[GatewayEndpoints] LocateSessionAsync: sid={sid} not found in the pushed cache (owning Director not connected)");
        return Task.FromResult<(DirectorDto?, SessionDto?)>((null, null));
    }

    // Build the externally-reachable base URL for a Director's web UI.
    //
    // Priority:
    //   1. If the Director registered a TailnetEndpoint that is actually routable
    //      for THIS caller, trust it. A same-machine Director registers a loopback
    //      endpoint (http://127.0.0.1:<port>) which IS its control endpoint but is
    //      useless to a remote caller, so a loopback endpoint is honored only when
    //      the caller is itself on loopback.
    //   2. Else if the caller reached the Gateway over a non-loopback host
    //      (e.g. https://<host>.<tailnet>.ts.net/), mirror that host
    //      and the request scheme onto the Director's own Control API port.
    //      Tailscale Serve maps each Director port to the same number under
    //      HTTPS, so https://<tailnet>:<port>/ resolves correctly.
    //   3. Else fall back to the raw ControlEndpoint (loopback case).
    //
    // Without (2), ViewUrl returns http://127.0.0.1:<port>/... which is
    // unreachable from a phone or any non-loopback client.
    internal static string DeriveDirectorBaseUrl(HttpContext ctx, DirectorDto d)
    {
        var requestHost = ctx.Request.Host.Host;
        var callerIsLoopback = string.IsNullOrEmpty(requestHost)
                         || requestHost == "localhost"
                         || requestHost == "127.0.0.1"
                         || requestHost == "::1";

        // 1. Honor an explicitly registered endpoint, but never feed a loopback
        //    endpoint to a non-loopback caller (that is the phone-gets-127.0.0.1 bug).
        if (!string.IsNullOrEmpty(d.TailnetEndpoint)
            && Uri.TryCreate(d.TailnetEndpoint, UriKind.Absolute, out var tailnetUri)
            && (callerIsLoopback || !tailnetUri.IsLoopback))
        {
            return d.TailnetEndpoint.TrimEnd('/');
        }

        // 2. Remote caller: mirror the public host + scheme onto the Director's port.
        if (!callerIsLoopback
            && Uri.TryCreate(d.ControlEndpoint, UriKind.Absolute, out var controlUri)
            && controlUri.Port > 0)
        {
            return $"{ctx.Request.Scheme}://{requestHost}:{controlUri.Port}";
        }

        return (d.ControlEndpoint ?? "").TrimEnd('/');
    }

    // The Gateway's own externally-reachable base URL, exactly as THIS caller reached
    // it (scheme + host + optional port). Stamped onto session deep links so the
    // Director-served session view can link back to the Gateway directory it came from.
    internal static string DeriveGatewayBaseUrl(HttpContext ctx)
    {
        return $"{ctx.Request.Scheme}://{ctx.Request.Host.Value}";
    }


    private static string? ResolveDirectorExe()
    {
        var names = new[] { "cc-director.exe", "cc-director" };

        // 1) Same directory as the running gateway (production: same install dir)
        var gatewayDir = AppContext.BaseDirectory;
        foreach (var name in names)
        {
            var candidate = Path.Combine(gatewayDir, name);
            if (File.Exists(candidate)) return candidate;
        }

        // 2) Dev-build layout: when the gateway is running from
        //    src/CcDirector.Gateway/bin/<config>/<tfm>/, the freshly-built director sits at
        //    src/CcDirector.Avalonia/bin/<config>/<tfm>/cc-director.exe . Walk up four
        //    levels to find a sibling Avalonia/bin/<config>/<tfm>/.
        var dir = new DirectoryInfo(gatewayDir);
        // gatewayDir = .../src/CcDirector.Gateway/bin/<config>/<tfm>/
        // parent[0]  = .../src/CcDirector.Gateway/bin/<config>/
        // parent[1]  = .../src/CcDirector.Gateway/bin/
        // parent[2]  = .../src/CcDirector.Gateway/
        // parent[3]  = .../src/
        if (dir.Parent?.Parent?.Parent?.Parent is { } srcRoot)
        {
            var tfm = dir.Name;
            var cfg = dir.Parent.Name;
            var avaloniaCandidate = Path.Combine(srcRoot.FullName, "CcDirector.Avalonia", "bin", cfg, tfm);
            foreach (var name in names)
            {
                var candidate = Path.Combine(avaloniaCandidate, name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        // 3) Standard install location (only used when nothing better was found)
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var bin = Path.Combine(localAppData, "cc-director", "bin");
        foreach (var name in names)
        {
            var candidate = Path.Combine(bin, name);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    internal sealed record GatewayEvent(string Type, string Id);

    /// <summary>Only allow same-origin path redirects (defense against open-redirect).</summary>
    private static bool IsSafeRedirect(string next)
    {
        return !string.IsNullOrEmpty(next)
            && next.StartsWith("/", StringComparison.Ordinal)
            && !next.StartsWith("//", StringComparison.Ordinal);
    }

    /// <summary>One-line-safe log form of a caller-supplied string (reason fields etc.).</summary>
    private static string Truncate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var oneLine = s.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 200 ? oneLine : oneLine[..200] + "...";
    }
}
