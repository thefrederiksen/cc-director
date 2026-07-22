using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Diagnostics;
using CcDirector.Core.Network;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
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
        Func<TenantId, string, bool>? voiceGeneratingFor = null,
        Func<TenantId, string, bool>? voiceAudioReadyFor = null,
        Func<TenantId, string, Core.HostedAi.HostedAiState?>? voiceUnavailableFor = null,
        Func<TenantId, string, bool>? nothingToNarrateFor = null,
        Func<TenantId, string, bool>? servedViaFallbackFor = null,
        Func<string, bool, DateTime?>? needsYouStampFor = null,
        Func<TenantId, string, bool>? transcribingFor = null,
        Func<TenantId, string, string?>? dictationStatusFor = null,
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
        // stream-connected, which the endpoint surfaces as a 502 - there is no HTTP call to fall back to.
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
        // Snooze Length mission: the Gateway-owned snooze registry. POST /sessions/{sid}/hold REQUIRES it -
        // it records/clears a snooze-until here (the authoritative hold) and the /sessions fold reads it to
        // return an EXPIRED snooze to "needs you" (OnHold=false) on its own even if its Director has died.
        // When null the hold endpoint returns 503: there is no plain-forward fallback - the Gateway owns hold.
        Snooze.SnoozeRegistry? snoozeRegistry = null,
        // Gateway Cleanup mission (Wave 4b): the Gateway-native mission store. When non-null, the
        // POST/GET /missions routes are mapped and a mission-scoped spawn validates against it. Missions are
        // a fleet-level concept, so the source of truth lives here at the Gateway. Null (old callers, tests)
        // maps nothing, leaving missions to the Director's own /missions routes (unchanged this phase).
        Core.Sessions.MissionStore? missions = null,
        // Workflows mission (phase 4, issue #1771): when non-null, creating a mission also opens a
        // workflow RUN of the built-in "mission" workflow, pinned to its published version, and the
        // created mission's DTO carries the additive workflowRunId. Null (old callers, tests) leaves
        // mission creation byte-identical to before.
        Workflows.WorkflowRunStore? workflowRuns = null,
        // Store injection points: the host owns a single key vault, transcription telemetry log, and audio
        // archive and passes them here so the phone-recorder ingest transcriber (RecordingEndpoints) uses
        // the host's instances rather than newing its own. Null (old callers, tests) leaves RecordingEndpoints
        // to build its own defaults, byte-identical to before.
        Core.KeyVault? recordingKeyVault = null,
        Transcription.TranscriptionTelemetryLog? transcriptionTelemetry = null,
        Transcription.TranscriptionAudioArchive? transcriptionAudioArchive = null,
        // Round 4 finding 1: the reliable display-state channel, so the hold endpoint can TRIGGER a prompt
        // push of the folded HoldState after a snooze / unsnooze instead of sending its own second hold
        // command. This makes FleetDisplayStateObserver the single writer of the Director's raw hold. Null
        // (old callers, tests) leaves the endpoint to record the registry only, and the periodic sweep
        // reconciles the desktop.
        Fleet.FleetDisplayStateObserver? fleetDisplayState = null,
        // Hosted Multi-Tenancy (session-serving PR1): the auth-boundary tenant binder. When non-null on the
        // hosted Gateway, the request-scoped session reads (/sessions, /sessions/{sid}) resolve the caller's
        // tenant from its authenticated device key and DENY (403) when it has none - never falling back to
        // Local. Null (self-host, older callers, tests) keeps the single-tenant Local behavior.
        Tenancy.HostedTenantBoundary? tenantBoundary = null)
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

                // Workflows mission (phase 4, issue #1771): a mission IS a run of the built-in
                // "mission" workflow. The EXPECTED failure (mission workflow unrunnable) is checked
                // BEFORE the Mission record is written, so it cannot leave a mission behind with no
                // governance run. The Mission store and the run store are two different stores (JSON
                // and EF), so a process death exactly between the two writes can still orphan a
                // mission - a transition-era window that closes when the JSON mission store retires
                // onto the EF layer; the pre-check removes every failure mode short of that.
                // The owner's switch (register redesign ruling): a mission whose workflow the
                // owner EXPLICITLY turned off still gets created - it runs UNGOVERNED (no run
                // record) until the switch flips back. Three-valued on purpose: only an explicit
                // FALSE is the owner's choice; a MISSING mission workflow (null - a broken or
                // unseeded store) keeps the fail-loud path below, because silently ungoverned
                // missions are exactly the gap the outcome spine exists to close.
                var missionWorkflowEnabled = workflowRuns?.GetWorkflowEnabled("mission") ?? true;
                if (workflowRuns is not null && missionWorkflowEnabled != false)
                {
                    try
                    {
                        workflowRuns.EnsureRunnable("mission");
                    }
                    catch (Workflows.WorkflowValidationException ex)
                    {
                        FileLog.Write($"[GatewayEndpoints] POST /missions refused: {ex.Message}");
                        return Results.BadRequest(new { error = ex.Message });
                    }
                }

                var mission = missions.Create(req.MissionName, req.ParentMissionId);
                var dto = ToMissionDto(mission);
                if (workflowRuns is not null && missionWorkflowEnabled != false)
                {
                    try
                    {
                        var run = workflowRuns.Create(
                            "mission", mission.MissionName, missionId: mission.MissionId);
                        dto.WorkflowRunId = run.Id;
                    }
                    catch (Workflows.WorkflowValidationException)
                        when (workflowRuns.GetWorkflowEnabled("mission") == false)
                    {
                        // The owner flipped the switch between the pre-check and the run create.
                        // The mission record already exists, and the ruling says an explicit OFF
                        // makes an UNGOVERNED mission - so honor the flip instead of returning an
                        // error for a mission that was in fact created.
                        FileLog.Write($"[GatewayEndpoints] POST /missions: the mission workflow was " +
                                      $"turned OFF mid-create - mission {mission.MissionId} is UNGOVERNED");
                    }
                }
                else if (workflowRuns is not null)
                {
                    FileLog.Write($"[GatewayEndpoints] POST /missions: the mission workflow is OFF - " +
                                  $"mission {mission.MissionId} created UNGOVERNED (no run record)");
                }
                return Results.Json(dto, statusCode: StatusCodes.Status201Created);
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
        RecordingEndpoints.Map(app, recordingKeyVault, transcriptionTelemetry, transcriptionAudioArchive);

        // Read-only view of the Communication Manager approval queue (see the phone's
        // pending drafts remotely). Step 1 of centralizing the comm queue on the Gateway.
        CommQueueEndpoints.Map(app);

        // Local-machine exe/slot management (the "Exes" page). Defect 6: it gets the snooze registry so its
        // fleet pass applies the SAME expired-snooze override the roster applies - without it the page says
        // "Snoozed" while the roster says "Needs you".
        // Windows-only: the whole surface builds developer slot exes by shelling out to
        // powershell.exe scripts/local-build-avalonia.ps1 against a local_builds directory, which
        // exists only on a Windows dev box. Off Windows the routes are simply not mapped.
        if (OperatingSystem.IsWindows())
            ExesEndpoints.Map(app, registry, pushedSessions, streamStaleResolved, snoozeRegistry);

        // ===== HTML pages =====
        // The Gateway serves NO UI pages anymore (docs/plans/one-url-cockpit.md): "/" and every
        // other UI path fall through to the Cockpit via the fallback proxy. Only the token
        // login/logout pair remains (it guards the Gateway itself when auth is enabled). It lives in
        // GatewayLoginEndpoint, which bind-breaks the whole /login surface on hosted (MH-2) and routes the
        // self-host cookie write through the single GatewayTokenCookie helper.
        GatewayLoginEndpoint.Map(app, token);

        // ===== REST =====
        app.MapGet("/healthz", () =>
        {
            // Hosted Multi-Tenancy (session-serving PR2): /healthz is PUBLIC - it is the unauthenticated
            // liveness probe every Director and endpoint selector dials, so it carries no credential and
            // therefore has NO TENANT. On the hosted Gateway the fleet counts below are fleet-GLOBAL: an
            // anonymous caller reading "directors: 2" is reading an aggregate over every account's Directors.
            // That is a cross-tenant leak, and it cannot be fixed by making the count tenant-aware, because a
            // request with no tenant has no correct number to print. So on hosted the aggregate is not
            // computed at all - deny-by-default applies to metrics exactly as it applies to data. Liveness
            // (status, version, server time) is what a probe actually needs and stays public.
            //
            // Self-host is untouched: one tenant, one owner, and the counts are what the Director's own
            // connectivity self-test and the settings gateway probe read.
            if (tenantBoundary?.IsHosted == true)
            {
                // Directors/Sessions left NULL, which OMITS them from the JSON (HealthDto). Leaving them to
                // serialize as 0 would state a fleet of zero to every probe on hosted - false rather than
                // merely absent, and this is the endpoint the Director's connectivity self-test reads.
                return Results.Json(new HealthDto
                {
                    Status = "ok",
                    Version = version,
                    ServerTime = DateTime.UtcNow,
                });
            }

            var directors = registry.ListDirectors();
            // Post-cut: the roster lives ONLY in the push store, so count from there. A Director with no
            // fresh pushed snapshot is not connected to the tunnel and contributes zero.
            int totalSessions = directors.Sum(d =>
            {
                // Self-host only (see above): the single tenant is Local.
                var cached = pushedSessions?.TryGetFresh(TenantId.Local, d.DirectorId, streamStaleResolved);
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
        //
        // TENANT-PARTITIONED (Hosted Multi-Tenancy; unsafe-collection census rows 21 and 22). All three of
        // these routes carry the SAME obligation, and it has two halves that must both hold: the WRITE
        // stamps the caller's authenticated tenant onto what it stores, and BOTH READS serve only that
        // tenant's partition. A write-only fix still leaks on the reads; a read-only filter is a DEFERRED
        // leak - cross-tenant data would keep accumulating behind it, so the day the filter is lifted it
        // exposes a contaminated history. Neither half is worth anything without the other.
        //
        // The tenant comes from the caller's authenticated device key (ResolveReadTenant), never from the
        // posted body. A null is a DENY (403): on hosted, an authenticated key with no bound tenant is
        // refused, never served or credited to the Local partition.
        var netDiagResults = new NetDiagResultStore(Path.Combine(CcStorage.Root(), "diagnostics-results.json"));
        app.MapPost("/diag/result", async (HttpContext ctx, NetDiagResultDto result) =>
        {
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
            {
                FileLog.Write("[NetDiag] POST /diag/result DENIED - the authenticated device key resolves to no tenant, so there is no partition to credit this result to (never Local)");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            result.ClientIp = ctx.Connection.RemoteIpAddress?.ToString();
            result.ClientPath = NetDiag.ClassifyClientIp(ctx.Connection.RemoteIpAddress);
            result.ReceivedAt = DateTime.UtcNow;
            netDiagResults.Add(reqTenant.Value, result);
            // Fold into the hourly quality rollup by the MEASURED path (Direct/IsLanPath the client tagged
            // from its authoritative self-peer), never the front-door ClientPath. Keyed by tenant AND hour:
            // the hour alone is server time, which every tenant shares, so an unkeyed fold is an addition
            // into a shared aggregate that nobody can attribute or undo afterwards.
            netDiagRollup?.Fold(reqTenant.Value, result.ReceivedAt, result.LatencyMedianMs, result.Direct, result.IsLanPath, result.DownloadMbps, result.UploadMbps);
            FileLog.Write(
                $"[NetDiag] result tenant={reqTenant.Value.ToLogString()} surface={result.Surface} route={result.Route} clientPath={result.ClientPath} " +
                $"client={result.ClientIp} latencyMedian={result.LatencyMedianMs}ms down={result.DownloadMbps}Mbps " +
                $"up={result.UploadMbps}Mbps rating={result.Rating} loadedFrom={result.LoadedFrom}");
            await Task.CompletedTask;
            return Results.Json(new { ok = true });
        });
        app.MapGet("/diag/results", (HttpContext ctx) =>
        {
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
            {
                FileLog.Write("[NetDiag] GET /diag/results DENIED - the authenticated device key resolves to no tenant, so it owns no results (never the Local partition)");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            return Results.Json(netDiagResults.Recent(reqTenant.Value));
        });

        // GET /diag/rollup: the hourly quality trend (one bucket per UTC hour, oldest first) for the
        // Cockpit dashboard - percent-direct over time, latency trend, and the stored home/away split.
        // Served from the caller's own tenant partition only.
        app.MapGet("/diag/rollup", (HttpContext ctx) =>
        {
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
            {
                FileLog.Write("[NetDiag] GET /diag/rollup DENIED - the authenticated device key resolves to no tenant, so it owns no rollup (never the Local partition)");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            return Results.Json(netDiagRollup?.All(reqTenant.Value) ?? new List<NetDiagRollupStore.HourBucket>());
        });

        // About / diagnostics: product, version, build date, install root, the one Cockpit URL, and
        // the installed component versions (from installed.json on this box). Feeds the Cockpit About
        // page; loopback-reachable like the rest of the read API.
        // Route is /gateway/about so the /about path passes through to the Cockpit's Blazor page.
        // CockpitUrl comes from GatewayPublicUrl.ResolveCockpit(): {base}/cockpit, where base is the
        // configured public URL in hosted mode and the tailnet front door (null when Tailscale is down)
        // self-hosted. One derivation rule, both modes (owner ruling 2026-07-20).
        app.MapGet("/gateway/about", () => Results.Json(new AboutDto
        {
            Product = AboutInfo.ProductName,
            Version = AboutInfo.VersionFull,
            BuildDate = AboutInfo.BuildDate()?.ToString("yyyy-MM-dd HH:mm:ss"),
            MachineName = Environment.MachineName,
            InstallRoot = AboutInfo.InstallRoot,
            CockpitUrl = GatewayPublicUrl.ResolveCockpit(),
            InstalledComponents = new Dictionary<string, string>(AboutInfo.InstalledComponents()),
            ServerTime = DateTime.UtcNow,
        }));

        // Where is this machine's Cockpit? Url is resolved on the Gateway by GatewayPublicUrl from the ONE
        // public base: Url = {base}/cockpit. In hosted mode (CC_GATEWAY_HOSTED=1) the base is the configured
        // public base; self-hosted it is the tailnet front door (Url null when Tailscale is unavailable, and
        // the caller surfaces that). The desktop Cockpit button opens Url verbatim - a dumb client never
        // composes a path onto Url (the Gateway owns the URL - CLAUDE.md rule 7). Port is the Gateway port
        // and Up is true whenever answering.
        app.MapGet("/cockpit", (HttpContext ctx) =>
        {
            return Results.Json(new CockpitInfoDto
            {
                Url = GatewayPublicUrl.ResolveCockpit(),
                Port = ctx.Connection.LocalPort,
                Up = true,
            });
        });

        // Issue #1847: serve THIS request's tenant's Directors, resolved from its authenticated device key -
        // the same seam the session read path uses. The list used to be fleet-global while the by-id legs
        // were gated, which made it the ENUMERATION surface: any authenticated account could read back every
        // other account's Director id, machine name, operating system user, process id, client version and
        // liveness. A request with no bound tenant is DENIED (403), never served the Local partition.
        app.MapGet("/directors", (HttpContext ctx) =>
        {
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            return Results.Json(registry.ListDirectors(reqTenant.Value));
        });

        // ===== HTTP discovery (Phase 1) =====
        // The Director POSTs /directors/register on startup and heartbeats every 15 s.
        // On graceful shutdown it DELETEs its registration. Same-machine Directors that
        // don't have gateway.url configured continue to be discovered via the filesystem
        // watch path - both paths coexist permanently.

        app.MapPost("/directors/register", (DirectorRegistrationRequest req) =>
        {
            // MTR-01 (Codex round 1): the HTTP register/heartbeat/doorbell/unregister legs are the legacy
            // SAME-MACHINE discovery plane - a self-host-only concept. A hosted Director reaches the Gateway
            // over the tunnel, never these HTTP legs, and every entry this plane writes is keyed to the Local
            // tenant. Left open on hosted, POST /directors/register is exactly the Local-shadow path: a hosted
            // caller could fabricate a Local registration for an arbitrary director id and then read that id's
            // Local event ring. Make the whole plane explicitly UNAVAILABLE on hosted (403) so the shadow can
            // never be created; self-host is unchanged.
            if (tenantBoundary?.IsHosted == true)
                return LegacyDiscoveryPlaneUnavailable();
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
            // MTR-01 (Codex round 1): part of the legacy same-machine discovery plane - unavailable on hosted
            // (see /directors/register). This also replaces the pre-fix 410 that an unbound hosted request got
            // here with the correct 403 for a plane that does not serve hosted accounts.
            if (tenantBoundary?.IsHosted == true)
                return LegacyDiscoveryPlaneUnavailable();
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
            // MTR-01 (Codex round 1): the doorbell is a leg of the legacy same-machine HTTP discovery plane -
            // unavailable on hosted (see /directors/register), where leaving it open would let a hosted caller
            // inject into a bare-id event ring. On self-host its entries are always keyed to the Local tenant
            // (see DirectorRegistry.Upsert), so it resolves within Local and records under Local.
            if (tenantBoundary?.IsHosted == true)
                return LegacyDiscoveryPlaneUnavailable();
            if (registry.Get(TenantId.Local, id) is null)
                return Results.StatusCode(StatusCodes.Status410Gone);
            if (req is null || string.IsNullOrEmpty(req.SessionId) || string.IsNullOrEmpty(req.NewState))
                return Results.BadRequest(new { error = "sessionId and newState are required" });

            registry.MarkStateReporting(id);
            if (directorEvents is not null && !string.IsNullOrEmpty(req.Event))
                directorEvents.Record(TenantId.Local, id, req.SessionId, req.Event, req.NewState);
            onSessionState?.Invoke(id, req.SessionId, req.NewState);
            return Results.Json(new { ok = true });
        });

        // Issue #330: the per-director event debug surface - the recent doorbell events
        // (session-created/session-exited/prompt-detected) the Gateway has recorded for a
        // KNOWN director, oldest first. This is the minimal Phase-1 observable sink; the
        // real consumer (the SSE/WS event hub) is Phase 3.
        app.MapGet("/directors/{id}/events", (string id, HttpContext ctx) =>
        {
            // MTR-01 (Codex round 1): this is a CLIENT-serving read, so it resolves the request's OWN tenant
            // and reads only that tenant's ring for this id. 403 when no tenant is bound (deny-by-default,
            // never the Local partition), 404 when the id is not the caller's Director. Because the ring is now
            // keyed by (tenant, id), a hosted account can never read another account's ring - even for the same
            // id, and even if a Local shadow of the id existed, the caller's tenant reads a different queue.
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);
            if (registry.Get(reqTenant.Value, id) is null)
                return Results.NotFound(new { error = "director not found" });
            var events = directorEvents?.For(reqTenant.Value, id) ?? (IReadOnlyList<DirectorEventDto>)Array.Empty<DirectorEventDto>();
            return Results.Json(new { directorId = id, events });
        });

        // Gateway Cleanup mission (Phase 2/3): the two-way connectivity handshake (POST /directors/{id}/verify
        // + the verify-ws leg) is DELETED. It dialed the Director's HTTP/WebSocket callback endpoints - a
        // Gateway->Director HTTP path the tunnel-only endgame removes - and drove the reachability circuit
        // breaker, which is also gone. Liveness is now the tunnel connection itself.

        app.MapDelete("/directors/{id}/registration", (string id) =>
        {
            // MTR-01 (Codex round 1): part of the legacy same-machine discovery plane - unavailable on hosted
            // (see /directors/register). Left open, an unbound hosted caller holding the shared machine token
            // could remove a Local registration; on hosted there is no such plane to unregister from.
            if (tenantBoundary?.IsHosted == true)
                return LegacyDiscoveryPlaneUnavailable();
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
            // Hosted Multi-Tenancy (session-serving PR1): serve THIS request's tenant's roster, resolved from
            // its authenticated device key. On hosted a request with no bound tenant is DENIED (403), never
            // served the Local partition. Self-host is Local, unchanged.
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            var directors = registry.ListDirectors()
                .Where(d => string.IsNullOrEmpty(director) || string.Equals(d.DirectorId, director, StringComparison.OrdinalIgnoreCase))
                .Where(d => string.IsNullOrEmpty(machine) || string.Equals(d.MachineName, machine, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Hosted Multi-Tenancy (session-serving PR1): the Director registry is fleet-global, but a tenant's
            // roster must only ever NAME its own directors. Scope the list to the request tenant's partition so
            // another tenant's directors never appear - not as sessions, and not as "unreachable"
            // machineError / reachability rows (which would otherwise leak their ids and machine names in the
            // ?envelope response). A hosted director reaches the registry only via its tunnel Hello, which first
            // binds it into its tenant's partition, so scoping to the partition drops nothing of the tenant's
            // own. On self-host the boundary is inert and the registry already IS the one tenant's directors, so
            // this is skipped and behavior is unchanged (a registered-but-unpushed director still surfaces).
            if (tenantBoundary?.IsHosted == true && pushedSessions is not null)
            {
                var mine = new HashSet<string>(pushedSessions.DirectorIdsFor(reqTenant.Value), StringComparer.OrdinalIgnoreCase);
                directors = directors.Where(d => mine.Contains(d.DirectorId)).ToList();
            }

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
                    var cached = pushedSessions.TryGetFresh(reqTenant.Value, d.DirectorId, streamStale);
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
            // Defect 13: the UNFILTERED fleet - the role universe. `all` is the filtered response set and is
            // drawn from these same instances, which is what lets one role pass serve both.
            //
            // KNOWN LIMITATION, deliberately not fixed here: this universe is already scoped by the
            // `machine=` filter applied to the Director list at the top of this handler, so a Worker on
            // MACHINE_A whose Manager runs on MACHINE_B still gets its red un-suppressed by
            // `?machine=MACHINE_A`. Reordering cannot fix that one - the other Director is never read at all -
            // and fixing it means pulling every Director on every filtered read, which is a cost change that
            // needs its own decision. Recorded in docs/new_architecture/session-state.html.
            var fleet = new List<SessionDto>();
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
                        rosterCache.RecordReachable(reqTenant.Value, d.DirectorId, sessions);
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

                var projection = rosterCache.RecordUnreachable(reqTenant.Value, d.DirectorId, reason);
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
                    // Defect 13: the ROLE UNIVERSE is the UNFILTERED fleet, and it is collected HERE -
                    // before the filters below get a vote. A session the caller filtered out still exists,
                    // and still keeps its worker's red suppressed. See StampFleetRolesAndFold.
                    //
                    // The filters deliberately stay where they are rather than moving below the fold. Moving
                    // them would silently widen four unrelated things that read the FILTERED set today:
                    // owners?.Remember (ownership records), inputStats?.ObserveSnapshot, concurrency?.Observe
                    // and sessionNumbers.Adopt. Those are second-order effects of a "simple" reorder and none
                    // of them is part of this defect.
                    fleet.Add(s);

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
                    //
                    // "Gated on raw red" is what this comment ALWAYS said. The code did not do it: it gated
                    // on s.StatusColor - the DIRECTOR's cooked colour - so a colour rendered on the phone and
                    // the Cockpit depended on a decision the Director made. That is precisely what law 2
                    // forbids (the Gateway is the only thing that picks a colour), and it was the last
                    // Gateway consumer of the cooked field. The comment described the intended design and the
                    // code never matched it; now it does.
                    //
                    // THE STAMP THAT WAS HERE IS DELETED, AND MUST NOT COME BACK (gap 5). It read:
                    //
                    //     if (voiceGeneratingFor is not null
                    //         && (s.BriefingState is null or "None" or "Briefed")
                    //         && SessionOrdering.IsRawRed(s)
                    //         && voiceGeneratingFor(s.SessionId))
                    //         s.BriefingState = "Briefing";
                    //
                    // THE GATEWAY MUST NOT OVERWRITE A FIELD THE DIRECTOR OWNS. BriefingState is the
                    // Director's fact. Writing "Briefing" over it destroyed the Director's answer, and a
                    // destroyed fact cannot be argued back: a row carrying BriefingState="Briefing" plus
                    // VoiceGenerating=true could no longer say whether the Director genuinely was briefing
                    // (the desktop folds yellow too - agreement) or the Gateway had overwritten a "None"
                    // (the desktop folds red - a real disagreement). Those are opposite verdicts from an
                    // identical row. The agreement check could only call it "indeterminate" and refuse to
                    // grade it - which is a workaround for the instrument, not a fix for the product.
                    //
                    // NOTHING REPLACED IT, and that is the fix - not a new rule somewhere else. The Gateway
                    // already adds its fact: VoiceGenerating, stamped unconditionally two lines down, and
                    // SessionOrdering.IsVoicePreparing already folds it to the same yellow. The stamp was
                    // redundant as well as destructive.
                    //
                    // READ THIS BEFORE YOU "RESTORE THE MISSING RULE": IsVoicePreparing is NOT this stamp's
                    // condition and is not meant to be. It is narrower - it requires VoiceMode and a session
                    // actually WAITING, where the stamp fired on any raw-red session with voice generating.
                    // A first attempt at this fix did add a rule carrying the stamp's exact condition, on
                    // the theory that it preserved every pixel; the existing suite refuted it
                    // (StateLabel_VoicePreparing_IsPreparingVoice and
                    // EffectiveColor_NonVoiceWaiting_NoAudio_StaysRed both went red) and that attempt was
                    // thrown away. Two rules for one fact is two answers, which is this mission's whole
                    // defect class. If a row looks like it is missing a yellow, the question is whether
                    // IsVoicePreparing is right - not whether this stamp should come back.
                    //
                    // The stamp also made the words wrong, which nobody noticed: hijacking BriefingState
                    // sent a voice-generating session down the fold's IsBriefing arm, so it read "Wingman
                    // reading" when the Gateway's own rule says the truer "Preparing voice". The dot was
                    // yellow either way. Both facts now ride the row, nothing is destroyed, the check can
                    // grade it, and the label is honest.
                    //
                    // If you need the Gateway to say something new about a session, add a Gateway-owned
                    // field. Never reach for a Director-owned one because it happens to be the shape you
                    // want - that trade is a rendered pixel now for an unanswerable row forever.

                    // Issue #553: surface the two voice readiness booleans the color rule and the /m
                    // client read directly. VoiceGenerating = the wingman is producing this session's
                    // spoken summary now; VoiceAudioReady = the gateway has fetchable, playable audio
                    // (the SINGLE truthful "there is voice you can play right now" signal). VoiceGenerating
                    // is the only "preparing voice" hold; VoiceAudioReady controls playback affordances.
                    if (voiceGeneratingFor is not null)
                        s.VoiceGenerating = voiceGeneratingFor(reqTenant.Value, s.SessionId);
                    if (voiceAudioReadyFor is not null)
                        s.VoiceAudioReady = voiceAudioReadyFor(reqTenant.Value, s.SessionId);
                    // Issue #939: when the gateway could not keep this session's voice because hosted AI
                    // is unavailable (out of credits / cap / no key), stamp the ONE shared message so the
                    // owning UI shows the consistent add-credit / add-key state instead of a silently
                    // missing play triangle. Null (voice fine) leaves the field unset.
                    if (voiceUnavailableFor is not null && voiceUnavailableFor(reqTenant.Value, s.SessionId) is Core.HostedAi.HostedAiState reason)
                        s.VoiceUnavailable = HostedAi.HostedAiHttp.Dto(reason);
                    // The FOLDED voice-mode display verdict the Voice screen renders VERBATIM. Every piece
                    // of ruling the phone used to do for itself - the badge, the message, and crucially
                    // whether a "Generate narration" button appears - is decided HERE, from the facts just
                    // stamped plus the "nothing to narrate" marker, so a dumb client never has to guess (the
                    // guess is what put a dead-end Generate button next to a red "unavailable" badge). This
                    // is the law: the Gateway rules, the client renders (docs/new_architecture/session-state.html).
                    s.VoiceDisplay = Wingman.VoiceDisplayFold.Fold(
                        voiceMode: s.VoiceMode,
                        agentWorking: string.Equals(s.ActivityState, "Working", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(s.ActivityState, "Starting", StringComparison.OrdinalIgnoreCase),
                        hasAudio: s.VoiceAudioReady,
                        generating: s.VoiceGenerating,
                        unavailable: voiceUnavailableFor?.Invoke(reqTenant.Value, s.SessionId),
                        nothingToNarrate: nothingToNarrateFor?.Invoke(reqTenant.Value, s.SessionId) ?? false,
                        servedViaFallback: servedViaFallbackFor?.Invoke(reqTenant.Value, s.SessionId) ?? false);
                    // Orange "Transcribing..." while a dictated utterance is uploading/transcribing in
                    // the background for this session (mobile Speak -> Send released the screen). Stamped
                    // BEFORE the NeedsYouSince clock below so the EffectiveColor fold already sees orange
                    // (a transcribing session is not "needs you") when the clock reads the final color.
                    if (transcribingFor is not null)
                        s.Transcribing = transcribingFor(reqTenant.Value, s.SessionId);
                    // Issue #1181, Task 4: the honest phase label - "Uploading from phone" (durable PENDING
                    // marker) vs "Transcribing" (active run). Drives the same orange, but the clients render
                    // this string so the user knows whether it is their phone still uploading or the server.
                    if (dictationStatusFor is not null)
                        s.DictationStatus = dictationStatusFor(reqTenant.Value, s.SessionId);
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
            // human). Done here, once, because the role needs the full fleet view - the UNFILTERED one
            // (`fleet`), not the response set (`all`). See defect 13 in StampFleetRolesAndFold.
            StampFleetRolesAndFold(fleet, all, needsYouStampFor, snoozeRegistry);

            // DevThrottle Stats: fold the assembled roster's per-session input tallies into the always-
            // available aggregate that backs "Your Throttle". This is the ONE path that carries
            // SessionDto.InputStats on the live Gateway regardless of stream mode (the SignalR DirectorHub
            // fold only runs when stream mode is on, which it is not in production). The aggregator's
            // per-session high-water logic makes folding the full roster on every read idempotent - only a
            // genuine increase is added, so repeated /sessions polls never double-count.
            // MTR-08: stamp the REQUEST TENANT. The roster assembled above is this tenant's own (the
            // owned-Director gate filtered it), so its input tallies fold into this tenant's partition and can
            // never coalesce with another account's.
            inputStats?.ObserveSnapshot(all, DateTime.UtcNow, reqTenant.Value);

            // DevThrottle Stats: record fleet concurrency and the hourly activity log from the same
            // assembled roster - max concurrent loaded/running (live) and actively working, plus how many
            // distinct sessions/machines/repositories ran each hour. Per-tenant with no per-Director
            // instrumentation, since the roster already sees this tenant's sessions on every machine. The
            // tracker keeps only the higher value per hour, so folding on every /sessions read never inflates.
            concurrency?.Observe(all, DateTime.UtcNow, reqTenant.Value);

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
        app.MapGet("/interrupted", async (HttpContext ctx, CancellationToken ct) =>
        {
            // MTR-01: the interrupted plane used the fleet-global director list, so it fanned out to - and
            // enumerated - every tenant's Directors. Scope it to THIS request's tenant so a caller only ever
            // sees, and only ever reaches over the tunnel, its own Directors' crash journals. A request with no
            // bound tenant is DENIED (403), never served the fleet-global list.
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            var directors = registry.ListDirectors(reqTenant.Value);
            var fanout = directors.Select(async d =>
            {
                // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (interrupted-list verb, director-level).
                // A non-null stream result is authoritative for this Director - Ok carries its journals, a non-Ok
                // is treated as no journals (skipped).
                var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, d.DirectorId, "interrupted-list", "", null, ct, machineName: d.MachineName);
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
        app.MapDelete("/interrupted/{deadDirectorId}/{deadPid:int}", async (HttpContext ctx, string deadDirectorId, int deadPid, string? via, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayEndpoints] DELETE /interrupted/{deadDirectorId}/{deadPid} via={via}");
            if (string.IsNullOrWhiteSpace(via))
                return Results.BadRequest(new { error = "via (reporting director id) is required" });
            // MTR-01: resolve the reporting Director in the request's OWN tenant, so a caller cannot dismiss a
            // journal via another tenant's Director (403 with no tenant, 404 for a foreign id).
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, via, out _, out var err))
                return err;

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (interrupted-dismiss verb on the reporting
            // Director). The HTTP path collapsed any non-success (incl a 404) to false -> 502, so a non-Ok
            // stream result maps to 502 to stay byte-identical.
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, via, "interrupted-dismiss", "",
                new InterruptedDismissRequest { DeadDirectorId = deadDirectorId, DeadPid = deadPid }, ct);
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502 like a failed dismiss.
            return sr is not null && sr.Ok ? Results.Json(new { dismissed = true }) : TunnelFailure(sr);
        });

        // Dismiss ONE session from an interrupted journal (issue #212 W4): the rest of the
        // journal stays in the Interrupted sessions list. Routed like the journal-level dismiss above.
        app.MapDelete("/interrupted/{deadDirectorId}/{deadPid:int}/sessions/{sessionId}",
            async (HttpContext ctx, string deadDirectorId, int deadPid, string sessionId, string? via, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayEndpoints] DELETE /interrupted/{deadDirectorId}/{deadPid}/sessions/{sessionId} via={via}");
            if (string.IsNullOrWhiteSpace(via))
                return Results.BadRequest(new { error = "via (reporting director id) is required" });
            // MTR-01: resolve the reporting Director in the request's OWN tenant (403 with no tenant, 404 foreign).
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, via, out _, out var err))
                return err;

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (interrupted-remove verb on the reporting
            // Director). Non-Ok -> 502, matching the HTTP path's false -> 502 collapse.
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, via, "interrupted-remove", "",
                new InterruptedRemoveRequest { DeadDirectorId = deadDirectorId, DeadPid = deadPid, SessionId = sessionId }, ct);
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            return sr is not null && sr.Ok ? Results.Json(new { removed = true }) : TunnelFailure(sr);
        });

        // Restore one interrupted session (issue #212 W4): create a CONTINUATION session -
        // a fresh session in the dead session's repo, seeded with a context document built
        // from the Gateway's surviving turn-brief history. Never `claude --resume`. The
        // continuation is created on req.ToDirectorId when given, else on the reporting
        // Director (req.Via) - the reporter shares the dead Director's machine, so the repo
        // path is valid there. After a successful create the restored session is removed
        // from the dirty journal so the Interrupted sessions list reflects what is still unrecovered.
        app.MapPost("/interrupted/{deadDirectorId}/{deadPid:int}/restore",
            async (HttpContext ctx, string deadDirectorId, int deadPid, RestoreInterruptedRequest req, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayEndpoints] POST /interrupted/{deadDirectorId}/{deadPid}/restore: sid={req?.SessionId} via={req?.Via} toDir={req?.ToDirectorId}");
            if (req is null || string.IsNullOrWhiteSpace(req.SessionId))
                return Results.BadRequest(new { error = "sessionId is required" });
            if (string.IsNullOrWhiteSpace(req.Via))
                return Results.BadRequest(new { error = "via (reporting director id) is required" });

            // MTR-01: both the reporting Director and any explicit target Director are resolved in the request's
            // OWN tenant, so a restore can neither read a foreign crash journal nor spawn a continuation session
            // on another tenant's Director (403 with no tenant, 404 for a foreign id).
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, req.Via, out var reporter, out var reporterErr))
                return reporterErr;
            DirectorDto target;
            if (string.IsNullOrWhiteSpace(req.ToDirectorId))
                target = reporter;
            else if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, req.ToDirectorId, out target, out var targetErr))
                return targetErr;

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
            var createSr = await DirectorCommandRouter.TrySendAsync(sendCommand, target.DirectorId, "create", "", spawnReq, CancellationToken.None, machineName: target.MachineName);
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
                var patchSr = await DirectorCommandRouter.TrySendAsync(sendCommand, target.DirectorId, "patch", created.SessionId, renameReq, CancellationToken.None, machineName: target.MachineName);
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
            var removeSr = await DirectorCommandRouter.TrySendAsync(sendCommand, reporter.DirectorId, "interrupted-remove", "", removeReq, CancellationToken.None, machineName: reporter.MachineName);
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
            // Hosted Multi-Tenancy (session-serving PR1): resolve the request's tenant from the authenticated
            // device key and DENY (403) when hosted returns no bound tenant - a by-id read must never fall back
            // to Local or SYSTEM. On self-host this is always Local.
            var reqTenant = ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);

            // LocateSessionAsync resolves the OWNING DIRECTOR (and refreshes the ownership record). It also
            // hands back a session copy, which we deliberately drop: that copy is not part of the role
            // universe assembled below, and stamping an instance the role pass never walked would leave
            // SessionRole null and fold a colour from it. We take our instance from the fleet instead.
            var (director, _) = await LocateSessionAsync(registry, sid, pushedSessions, streamStaleResolved, reqTenant.Value, owners);
            if (director is null)
                return Results.NotFound(new { error = "session not found across any director" });

            // Defect 15: this route returned EffectiveColor / StateLabel / TriageBucket as NULL and left the
            // expired-snooze override unapplied, because it never ran the fold - StampFleetRolesAndFold was
            // private to the roster handler and this route just serialized the raw cached DTO. SessionDto
            // documents all three as "Required on Gateway /sessions responses", and this route violated it.
            //
            // HONEST SCOPE: that is a verified CODE fact, not an observed symptom. No shipped client fetches
            // this route - the Cockpit and the phone read the roster and go through client-core, which throws
            // if the fields are missing, and neither app calls this. The fix is justified by the contract the
            // DTO documents, not by a user-visible bug, and no such bug is claimed.
            var byDirector = FleetByDirector(registry, pushedSessions, streamStaleResolved, reqTenant.Value);
            var fleet = byDirector.Values.SelectMany(x => x).ToList();
            var session = fleet.FirstOrDefault(x => string.Equals(x.SessionId, sid, StringComparison.Ordinal));
            if (session is null)
                return Results.NotFound(new { error = "session not found across any director" });

            var baseUrl = DeriveDirectorBaseUrl(ctx, director);
            session.DirectorId = director.DirectorId;
            session.MachineName = director.MachineName;
            session.User = director.User;
            session.TailnetEndpoint = baseUrl;
            session.ViewUrl = $"{baseUrl}/sessions/{session.SessionId}/view?gw={Uri.EscapeDataString(DeriveGatewayBaseUrl(ctx))}";

            // needsYouStampFor is deliberately NOT passed: the needs-you clock has entry/exit semantics and
            // is driven by the roster read. Letting a by-id read stamp it would drive that clock out of band
            // and corrupt the roster's own waiting times. NeedsYouSince stays unstamped here, exactly as
            // before - this fix does not claim it.
            StampFleetRolesAndFold(fleet, new[] { session }, needsYouStampFor: null, snoozeRegistry: snoozeRegistry);
            return Results.Json(session);
        });

        // Forward "kill this session" to the owning Director so a remote client (the
        // phone) can shut a session down. Without this, DELETE only worked on the
        // Director's own Control API, never through the Gateway.
        app.MapDelete("/sessions/{sid}", async (HttpContext ctx, string sid) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Post-cut: tunnel-only. A null result (Director not connected) stays 502 like a failed kill, but now
            // says so. This verb is the sharpest case for explaining itself: on a timeout or a mid-flight drop the
            // session may or may not have been killed, and a bare 502 left the user with no idea which.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "kill", sid, null, CancellationToken.None, machineName: director.MachineName);
            var ok = streamResult is not null && streamResult.Ok;
            if (!ok)
                return TunnelFailure(streamResult, director.MachineName);
            return Results.Json(new { killed = true });
        });

        // Forward "flag this session for deletion" to the owning Director, so a session on ONE
        // machine (or a remote client) can request the async teardown of a session on another. The
        // owning Director's reaper does the actual removal. Body is optional ({ "reason": "..." }).
        app.MapPost("/sessions/{sid}/request-deletion", async (HttpContext ctx, string sid, SessionDeletionRequest? body, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Tunnel-only. The Ok result is success and synthesizes the { pendingDeletion } body; a null result
            // (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "request-deletion", sid, body, ct, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok
                ? Results.Json(new { pendingDeletion = true })
                : TunnelFailure(streamResult);
        });

        // Forward "cancel the pending deletion" to the owning Director (grace-window undo).
        app.MapDelete("/sessions/{sid}/request-deletion", async (HttpContext ctx, string sid, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Gateway Cleanup (Phase 2, PR C): tunnel-first, HTTP fallback on a null return (byte-identical).
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "cancel-deletion", sid, null, ct, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok
                ? Results.Json(new { pendingDeletion = false })
                : TunnelFailure(streamResult);
        });

        // Phase 4b: forward wingman observability through the Gateway so the merged
        // Session View on the gateway side can render WHY a dot is the color it is.
        app.MapGet("/sessions/{sid}/wingman", async (HttpContext ctx, string sid, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Tunnel-only. The Ok body IS the WingmanViewDto JSON, passed through exactly as the HTTP body.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "wingman-view", sid, null, ct, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : TunnelFailure(streamResult);
        });

        // Phase 5: forward "ask the wingman" calls. Each is one fresh side-call
        // (Haiku for free-text asks; Opus when Mode=="explain"). Body forwards verbatim.
        app.MapPost("/sessions/{sid}/wingman/ask", async (HttpContext ctx, string sid, WingmanAskRequest req, CancellationToken ct) =>
        {
            var explain = string.Equals(req?.Mode, "explain", StringComparison.OrdinalIgnoreCase);
            if (req is null || (!explain && string.IsNullOrWhiteSpace(req.Question)))
                return Results.BadRequest(new WingmanAskResult { Status = "bad_request", Error = "question is required" });
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Gateway Cleanup (Phase 2, PR C): tunnel-first. This is a SLOW LLM call - the request ct threads
            // straight into the SignalR invocation (which has no per-invocation timeout; keep-alive pings sustain
            // the long await), so the synchronous browser contract is byte-identical to the HTTP forward. A null
            // The Ok body IS the WingmanAskResult JSON.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            // Runs a language model on the Director before it can answer, so it gets the longer wait.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "wingman-ask", sid, req, ct,
                timeout: DirectorCommandRouter.LanguageModelCommandTimeout, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : TunnelFailure(streamResult);
        });

        // Forward "set the session goal" to the owning Director. Body forwards verbatim.
        app.MapPost("/sessions/{sid}/wingman/goal", async (HttpContext ctx, string sid, WingmanGoalRequest req, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var goalReq = req ?? new WingmanGoalRequest();
            // Post-cut: tunnel-only. The Ok stream body IS the { goal, goalSetAt, goalState } JSON; a null
            // result (Director not connected) or a non-Ok result collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "wingman-goal", sid, goalReq, ct, machineName: director.MachineName);
            var body = streamResult is not null && streamResult.Ok ? streamResult.BodyJson : null;
            if (body is null)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Content(body, "application/json");
        });

        // Automatic session roles (chunk 2.5): (re)declare a session's sticky explicit role, routed DOWN the
        // stream first (DirectorCommandRouter), HTTP fallback otherwise. The Ok body is the updated SessionDto.
        app.MapPost("/sessions/{sid}/role", async (HttpContext ctx, string sid, SetRoleRequest req, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var roleReq = req ?? new SetRoleRequest();
            // Post-cut: tunnel-only. The Ok stream body is the updated SessionDto JSON; a null or non-Ok result collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "set-role", sid, roleReq, ct, machineName: director.MachineName);
            var body = streamResult is not null && streamResult.Ok ? streamResult.BodyJson : null;
            if (body is null)
                return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Content(body, "application/json");
        });

        // Record (or clear) the Gateway-owned snooze for this session - "park / un-park" (hold) (Snooze
        // Length mission, docs/architecture/snooze-length-mission-2026-07-11.md). Snooze IS the hold: the
        // Gateway owns the state AND the expiry timestamp, so holding a session records a snooze-until in the
        // registry - the AUTHORITATIVE result - and the session is GUARANTEED to return to "needs you" on its
        // own even if its Director later dies; un-holding clears it. The Gateway does NOT forward a plain hold
        // to the Director: it mutates the registry FIRST, then triggers a prompt, bounded set-display-state
        // push so the desktop rail reflects the folded hold (the single writer of the Director's raw hold).
        // The registry mutation stands even if that push times out - the periodic sweep reconciles the rail.
        app.MapPost("/sessions/{sid}/hold", async (HttpContext ctx, string sid, HoldRequest req, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var holdReq = req ?? new HoldRequest();
            // Issue #1500: an explicit per-call snooze length. Validate it BEFORE recording the hold, so a
            // bad value fails loudly (no fallback / no silent clamp) and never parks the session. Null = use
            // the per-user default. Only the Gateway reads SnoozeMinutes; the hold is recorded in the Gateway
            // registry and reflected to the Director through the display-state channel, not a plain hold, so
            // this stays a Gateway-only capability.
            if (holdReq.OnHold && holdReq.SnoozeMinutes is int requested
                && !Core.Configuration.SnoozeDefaultConfig.IsValid(requested))
            {
                return Results.BadRequest(new
                {
                    error = $"snoozeMinutes must be a whole number of minutes between "
                            + $"{Core.Configuration.SnoozeDefaultConfig.MinMinutes} and "
                            + $"{Core.Configuration.SnoozeDefaultConfig.MaxMinutes}"
                });
            }
            if (snoozeRegistry is null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            // THE GATEWAY DECIDES, HERE, AND NOWHERE ELSE.
            //
            // This used to forward the hold to the Director, read HoldResponse.Pending back, and record
            // whatever the DIRECTOR had decided. That is the whole defect in one paragraph: the ruling
            // ("is it working, so should this defer?") was made on a Director, the clock was kept here,
            // and the two drifted - defects 12, 20, 21, 22, and every hold that died within minutes on
            // 15 July 2026.
            //
            // The activity is already in hand: LocateSessionAsync returned the session, and its
            // ActivityState is the one fact the Director reports and the Gateway rules on.
            var decided = HoldStates.None;
            if (holdReq.OnHold)
            {
                // Issue #1500: honour a per-call snooze length when the caller passed one (already
                // validated above); otherwise the per-user default (snooze_default_minutes), read now so a
                // Settings change applies to the next snooze.
                var minutes = holdReq.SnoozeMinutes ?? Core.Configuration.SnoozeDefaultConfig.Get();

                // Working -> DEFER. THE RULING (owner, 14 July 2026): the clock starts when the work ENDS,
                // so a deferral records its LENGTH and no deadline, and SnoozeLandingObserver starts the
                // clock when the Director reports the work has stopped. Arming a clock at request time is
                // what made an agent-requested snooze permanent.
                //
                // "Working" here means BOTH Working AND Starting - the same set Session.IsWorking uses and the
                // same set SnoozeLandingObserver's working edge deletes an armed snooze on. If this armed a
                // Starting session instead of deferring it, the very next Starting push would delete the
                // just-created snooze through that edge. The defer decision and the working edge must agree on
                // what "working" is, or a snooze set on a Starting session cannot survive.
                var activityNow = session.ActivityState?.Trim();
                var working = string.Equals(activityNow, nameof(Core.Sessions.ActivityState.Working), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(activityNow, nameof(Core.Sessions.ActivityState.Starting), StringComparison.OrdinalIgnoreCase);
                // The owner-turn BASELINE: this Director's own LastOwnerTurnAtUtc as of right now. The
                // hold is superseded when a LATER value arrives from that same Director - one clock,
                // compared against itself. Never against DateTime.UtcNow here: that is the GATEWAY's
                // clock, and comparing it to a Director's stamp makes every hold hostage to clock skew.
                var ownerTurnBaseline = session.LastOwnerTurnAtUtc;

                if (working)
                {
                    snoozeRegistry.SnoozeDeferred(sid, minutes, director.DirectorId, ownerTurnBaseline);
                    decided = HoldStates.DeferredHold;
                }
                else
                {
                    snoozeRegistry.Snooze(sid, DateTime.UtcNow.AddMinutes(minutes), director.DirectorId, ownerTurnBaseline);
                    decided = HoldStates.Held;
                }
            }
            else
            {
                // Manual unsnooze: drop the timer (an alarm turned off).
                snoozeRegistry.Clear(sid);
            }

            // The hold is now a FACT, recorded and persisted in the registry. Round 4 finding 1: the desktop
            // rail is updated through the ONE reliable channel, not a second direct hold command. Trigger a
            // prompt push of the FOLDED hold state (from the registry we just changed) down the same
            // change-gated FleetDisplayStateObserver that serves every other surface - so there is a single
            // writer of the Director's raw hold and no descheduled second writer can leave it stale. Best-
            // effort BY DESIGN: the hold does not depend on it - a slow, unreachable or dead Director cannot
            // prevent the owner from holding a session, and the fold already reports the truth to every other
            // surface from the registry, with the periodic sweep reconciling the desktop.
            // Bounded and cancellable (round 5 finding 1): PushSessionAsync routes through the standard
            // DirectorCommandRouter 30s chokepoint carrying THIS request's token, so a connected-but-
            // unresponsive Director cannot hang the Snooze / Unsnooze click. On timeout or an unreachable
            // Director this still returns SUCCESS below - the registry mutation is the authoritative result
            // and the periodic sweep reconciles the desktop.
            if (fleetDisplayState is not null)
                await fleetDisplayState.PushSessionAsync(sid, ct);

            return Results.Json(new HoldResponse
            {
                OnHold = HoldStates.IsHeld(decided),
                Pending = decided == HoldStates.DeferredHold,
            });
        });

        // Mark / clear a session as transcribing a dictated utterance. Unlike hold this is a purely
        // Gateway-owned transient flag - it is NOT forwarded to the Director; it only feeds the
        // orange "Transcribing..." roster color. The mobile Speak flow calls { transcribing: true }
        // the instant the user hits Send (releasing the screen) and { transcribing: false } once the
        // background upload/transcribe/submit finishes or fails. A literal route so it wins over the
        // /sessions/{sid}/{**rest} catch-all Director proxy. Verified the session exists so a stale id
        // cannot pin a phantom mark.
        app.MapPost("/sessions/{sid}/transcribing", async (HttpContext ctx, string sid, TranscribingRequest req) =>
        {
            if (transcribingSessions is null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            // The transcribing mark is keyed by (tenant, sid) (issue #1884, Gap B): resolve the caller's
            // tenant from the authenticated device key and refuse (403) when none resolves on hosted, so a
            // request can only ever set or clear ITS OWN account's mark - never paint or clear another
            // account's session by supplying that account's session id. Self-host resolves Local, unchanged.
            if (ResolveReadTenant(ctx, tenantBoundary) is not { } reqTenant)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var transcribing = req?.Transcribing ?? false;
            if (transcribing)
                transcribingSessions.Begin(reqTenant, sid);
            else
                transcribingSessions.End(reqTenant, sid);
            return Results.Json(new { transcribing });
        });

        app.MapPatch("/sessions/{sid}", async (HttpContext ctx, string sid, SessionUpdateRequest req) =>
        {
            if (req is null)
                return Results.BadRequest(new { error = "request body is required" });

            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });

            FileLog.Write($"[GatewayEndpoints] PATCH /sessions/{sid}: name=\"{req.Name}\", director={director.DirectorId}");

            // Post-cut: tunnel-only. A null result means the Director is not connected -> 502.
            SessionDto? body;
            string? err;
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "patch", sid, req, CancellationToken.None, machineName: director.MachineName);
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

        app.MapGet("/sessions/{sid}/buffer", async (HttpContext ctx, string sid, int? lines, bool? raw, long? since, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null)
                return Results.NotFound(new { error = "session not found across any director" });

            if (director is null)
                return Results.NotFound(new { error = "session not found across any director" });

            // Post-cut: tunnel-only. The query params ride in a BufferRequest payload the Director's buffer
            // verb reads. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "buffer", sid,
                new BufferRequest { Lines = lines, Raw = raw == true, Since = since }, ct,
                machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : TunnelFailure(streamResult);
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

            var (director, session) = await LocateSessionForRequestAsync(httpCtx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });

            FileLog.Write($"[GatewayEndpoints] POST prompt: sid={sid}, director={director.DirectorId}, waitForIdle={req.WaitForIdle}");

            // Post-cut: tunnel-only. A null result means the Director is not connected -> 502. The WaitForIdle
            // poll below is unchanged - it observes the session regardless of how the prompt was delivered.
            bool ok; PromptResponse? body; string? err;
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "prompt", sid, req, CancellationToken.None, machineName: director.MachineName);
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
                // The idle poll rides the tunnel too (snapshot verb). Tunnel-only: there is no HTTP arm.
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

        app.MapPost("/sessions/{sid}/interrupt", async (HttpContext ctx, string sid) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });

            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "interrupt", sid, null, CancellationToken.None, machineName: director.MachineName);
            var ok = streamResult is not null && streamResult.Ok;
            return ok
                ? Results.Json(new { accepted = true })
                : TunnelFailure(streamResult);
        });

        app.MapPost("/sessions/{sid}/escape", async (HttpContext ctx, string sid) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });

            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "escape", sid, null, CancellationToken.None, machineName: director.MachineName);
            var ok = streamResult is not null && streamResult.Ok;
            return ok
                ? Results.Json(new { accepted = true })
                : TunnelFailure(streamResult);
        });

        // Phone image upload: the browser POSTs the image to the Gateway (its origin); we
        // forward the bytes to the owning Director, which files it into its screenshots
        // folder (same machine as the session) and returns the saved absolute path.
        app.MapPost("/sessions/{sid}/upload-image", async (string sid, HttpContext ctx) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
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
            // A null begin means the Director is not connected and collapses to 502 below. A
            // non-null-but-failed step is authoritative and collapses to 502 (a retryable upload failure).
            var uploadId = Guid.NewGuid().ToString("N");
            var begin = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "upload-image-begin", sid,
                new UploadImageBeginRequest { UploadId = uploadId, FileName = file.FileName, TotalBytes = bytes.Length }, ctx.RequestAborted,
                machineName: director.MachineName);
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
                    var cr = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "upload-image-chunk", sid, chunk, ctx.RequestAborted, machineName: director.MachineName);
                    if (cr is null || !cr.Ok)
                        return Results.Json(new { error = cr is null ? "tunnel dropped mid-upload" : DirectorCommandRouter.DescribeFailure(cr) }, statusCode: StatusCodes.Status502BadGateway);
                }

                var done = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "upload-image-complete", sid,
                    new UploadImageCompleteRequest { UploadId = uploadId }, ctx.RequestAborted,
                    machineName: director.MachineName);
                if (done is null || !done.Ok || string.IsNullOrEmpty(done.BodyJson))
                    return Results.Json(new { error = done is null ? "tunnel dropped mid-upload" : DirectorCommandRouter.DescribeFailure(done) }, statusCode: StatusCodes.Status502BadGateway);

                return Results.Content(done.BodyJson, "application/json"); // { path, fileName }
            }

            // Post-cut: tunnel-only. A null begin means the Director is not connected -> 502.
            return Results.Json(new { error = "director not connected to the tunnel" }, statusCode: StatusCodes.Status502BadGateway);
        });

        app.MapGet("/directors/{id}/repos", async (HttpContext ctx, string id, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (repos-list verb, director-level so SessionId
            // is ""). Tunnel-only: a null return means the Director is not connected, and a non-Ok stream
            // result collapses to 502.
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "repos-list", "", null, ct, machineName: d.MachineName);
            if (sr is not null)
            {
                if (!sr.Ok) return TunnelFailure(sr);
                return Results.Json(DirectorCommandRouter.ReadBody<List<RepositoryDto>>(sr) ?? new List<RepositoryDto>());
            }

            // Post-cut: tunnel-only. A null result (Director not connected) stays 502, but now says so instead of arriving as a silent bare status.
            return TunnelFailure(null);
        });

        // Issue #330: pull a registered Director's machine facts (tool inventory with
        // versions + launcher presence/port) through the existing proxy leg. Pulled on
        // demand rather than pushed in registration/heartbeat: the inventory is large and
        // changes rarely, so riding the 15s heartbeat would bloat the hot path for a fact
        // a consumer reads occasionally.
        app.MapGet("/directors/{id}/facts", async (HttpContext ctx, string id, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (facts verb, director-level).
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "facts", "", null, ct, machineName: d.MachineName);
            if (sr is not null)
            {
                if (!sr.Ok) return TunnelFailure(sr);
                var body = DirectorCommandRouter.ReadBody<DirectorFactsDto>(sr);
                if (body is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
                return Results.Json(body);
            }

            // Post-cut: tunnel-only. A null result (Director not connected) stays 502, but now says so instead of arriving as a silent bare status.
            return TunnelFailure(null);
        });

        // Issue #1497: the target Director's configured, enabled agents (one per kind) for the Cockpit New
        // Session dialog's agent picker. Rides the tunnel (agents-list verb, director-level), mirroring the
        // facts/repos-list read legs above; a null result (Director not connected) collapses to 502.
        app.MapGet("/directors/{id}/agents", async (HttpContext ctx, string id, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "agents-list", "", null, ct, machineName: d.MachineName);
            if (sr is not null)
            {
                if (!sr.Ok) return TunnelFailure(sr);
                return Results.Json(DirectorCommandRouter.ReadBody<List<AgentChoiceDto>>(sr) ?? new List<AgentChoiceDto>());
            }

            return TunnelFailure(null, d.MachineName);
        });

        // Gateway Cleanup CUT RESTORATION: the Cockpit's Director Settings editor
        // (apps/cockpit/src/fleet/DirectorDetailView.tsx -> client-core getDirectorSettings/putDirectorSettings).
        // The cut dropped the HTTP reverse-proxy leg that used to serve these two and deferred remote config
        // editing to Phase 4, but the CALLER was left pointing here. With nothing mapped, the request fell
        // through to the single-page-app fallback, which answered the Cockpit's GET with the HTML shell at
        // status 200 - and the client only checks res.ok, so it loaded a web page into the settings editor and
        // called it settings. These legs ride the tunnel like every director-level verb above; the settings body
        // is an OPAQUE object the Director owns, so it is forwarded VERBATIM in both directions rather than
        // being modelled here (the Gateway must not become a second definition of the Director's config).
        app.MapGet("/directors/{id}/settings", async (HttpContext ctx, string id, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "settings-get", "", null, ct, machineName: d.MachineName);
            if (sr is null || !sr.Ok) return DirectorAnswerFailure(sr, d.MachineName);

            // The verb's body IS the config object; forward the exact bytes the Director sent.
            return Results.Content(sr.BodyJson ?? "{}", "application/json");
        });

        app.MapPut("/directors/{id}/settings", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            string raw;
            using (var reader = new StreamReader(ctx.Request.Body))
                raw = await reader.ReadToEndAsync(ct);

            // Parse only far enough to know it is a JSON object - the Director owns what the keys MEAN. A
            // malformed edit is rejected HERE, naming the fault, rather than travelling to the Director to
            // fail there or, worse, being written as garbage.
            JsonNode? patch;
            try
            {
                patch = JsonNode.Parse(raw);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = $"The settings you sent are not valid JSON: {ex.Message}" },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            if (patch is not JsonObject)
                return Results.Json(new { error = "request body must be a JSON object" },
                    statusCode: StatusCodes.Status400BadRequest);

            FileLog.Write($"[GatewayEndpoints] PUT /directors/{id}/settings: machine={d.MachineName}");

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "settings-put", "",
                new SettingsPutPayload { Settings = patch }, ct, machineName: d.MachineName);
            if (sr is null || !sr.Ok) return DirectorAnswerFailure(sr, d.MachineName);

            // The merged config the Director actually stored, forwarded verbatim.
            return Results.Content(sr.BodyJson ?? "{}", "application/json");
        });

        app.MapPost("/directors/{id}/sessions", async (HttpContext ctx, string id, NewSessionRequest req) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var ownerErr)) return ownerErr;
            if (req is null || string.IsNullOrWhiteSpace(req.RepoPath))
                return Results.BadRequest(new { error = "repoPath is required" });

            FileLog.Write($"[GatewayEndpoints] POST /directors/{id}/sessions: repo={req.RepoPath}, agent={req.Agent}");

            // Issue #1177 (Phase 1): create rides the target Director's stream. Tunnel-only: a null return
            // means the Director is not connected, and a non-Ok stream result (validation/creation failure)
            // collapses to 502 - both surface as the error below.
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

        app.MapDelete("/directors/{id}/repos", async (HttpContext ctx, string id, string? path, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "path is required" });

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (repo-delete verb, director-level). The
            // Director core returns { removed } in its body; a non-Ok stream result collapses to 502.
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "repo-delete", "", new RepoDeleteRequest { Path = path }, ct, machineName: d.MachineName);
            if (sr is not null)
            {
                if (!sr.Ok) return TunnelFailure(sr);
                return Results.Content(sr.BodyJson ?? "{\"removed\":false}", "application/json");
            }

            // Post-cut: tunnel-only. A null result (Director not connected) stays 502, but now says so instead of arriving as a silent bare status.
            return TunnelFailure(null);
        });

        // Gateway Cleanup CUT RESTORATION (SB-4a): register a repository explicitly in the recent list. Rides
        // the repo-add verb (director-level). The Director core validates directory-existence and returns
        // { added, repo }; added selects the old route's 201 (newly added) vs 200 (already present). A typed
        // failure preserves 400; a null result (Director not tunnel-connected) is 502.
        app.MapPost("/directors/{id}/repos", async (HttpContext ctx, string id, RepoAddRequest req, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;
            if (req is null || string.IsNullOrWhiteSpace(req.Path)) return Results.BadRequest(new { error = "path is required" });

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "repo-add", "", req, ct, machineName: d.MachineName);
            if (sr is null || !sr.Ok) return MapDirectorFailure(sr);
            var body = DirectorCommandRouter.ReadBody<RepoAddResponse>(sr);
            if (body is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(body, statusCode: body.Added ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        });

        // Gateway Cleanup CUT RESTORATION (SB-4a): rename a registered repository (path is the identity). Rides
        // the repo-rename verb (director-level). A path not in the registry is the executor's NotFound -> 404;
        // a null result (Director not tunnel-connected) is 502.
        app.MapPatch("/directors/{id}/repos", async (HttpContext ctx, string id, RepoRenameRequest req, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;
            if (req is null || string.IsNullOrWhiteSpace(req.Path)) return Results.BadRequest(new { error = "path is required" });
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name is required" });

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "repo-rename", "", req, ct, machineName: d.MachineName);
            if (sr is null || !sr.Ok) return MapDirectorFailure(sr);
            var body = DirectorCommandRouter.ReadBody<RepositoryDto>(sr);
            if (body is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(body);
        });

        // Gateway Cleanup CUT RESTORATION (SB-4a): the enriched per-repo overview the Repositories page reads.
        // Rides the repos-overview verb (director-level). A null result (Director not tunnel-connected) is 502.
        app.MapGet("/directors/{id}/repos/overview", async (HttpContext ctx, string id, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "repos-overview", "", null, ct, machineName: d.MachineName);
            if (sr is null || !sr.Ok) return MapDirectorFailure(sr);
            return Results.Json(DirectorCommandRouter.ReadBody<List<RepoOverviewDto>>(sr) ?? new List<RepoOverviewDto>());
        });

        app.MapGet("/directors/{id}/coaching/categories", async (HttpContext ctx, string id, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (coaching-categories verb, director-level).
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "coaching-categories", "", null, ct, machineName: d.MachineName);
            if (sr is not null)
            {
                if (!sr.Ok) return TunnelFailure(sr);
                return Results.Json(DirectorCommandRouter.ReadBody<List<CoachingCategoryDto>>(sr) ?? new List<CoachingCategoryDto>());
            }

            // Post-cut: tunnel-only. A null result (Director not connected) stays 502, but now says so instead of arriving as a silent bare status.
            return TunnelFailure(null);
        });

        app.MapGet("/directors/{id}/claude-sessions", async (HttpContext ctx, string id, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (claude-sessions verb, director-level; no
            // repo filter on this route, so the payload is empty).
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "claude-sessions", "", null, ct, machineName: d.MachineName);
            if (sr is not null)
            {
                if (!sr.Ok) return TunnelFailure(sr);
                return Results.Json(DirectorCommandRouter.ReadBody<List<ClaudeSessionDto>>(sr) ?? new List<ClaudeSessionDto>());
            }

            // Post-cut: tunnel-only. A null result (Director not connected) stays 502, but now says so instead of arriving as a silent bare status.
            return TunnelFailure(null);
        });

        app.MapGet("/directors/{id}/handovers", async (HttpContext ctx, string id, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (handovers-list verb, director-level; this
            // route has no repo filter, so the payload is empty).
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "handovers-list", "", null, ct, machineName: d.MachineName);
            if (sr is not null)
            {
                if (!sr.Ok) return TunnelFailure(sr);
                return Results.Json(DirectorCommandRouter.ReadBody<List<HandoverDto>>(sr) ?? new List<HandoverDto>());
            }

            // Post-cut: tunnel-only. A null result (Director not connected) stays 502, but now says so instead of arriving as a silent bare status.
            return TunnelFailure(null);
        });

        app.MapGet("/directors/{id}/handovers/content", async (HttpContext ctx, string id, string? path, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "path is required" });

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (handovers-content verb, director-level; the
            // ?path query rides in the payload). A non-Ok stream result collapses to 502, matching the HTTP null.
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "handovers-content", "", new HandoverContentRequest { Path = path }, ct, machineName: d.MachineName);
            if (sr is not null)
            {
                if (!sr.Ok) return TunnelFailure(sr);
                var body = DirectorCommandRouter.ReadBody<HandoverContentDto>(sr);
                if (body is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
                return Results.Json(body);
            }

            // Post-cut: tunnel-only. A null result (Director not connected) stays 502, but now says so instead of arriving as a silent bare status.
            return TunnelFailure(null);
        });

        // Gateway Cleanup CUT RESTORATION (SB-4a): create a standalone saved-handover document. Rides the
        // handover-create verb (director-level). Success is the old route's 201; a typed failure preserves 400;
        // a null result (Director not tunnel-connected) is 502.
        app.MapPost("/directors/{id}/handovers", async (HttpContext ctx, string id, HandoverCreateRequest req, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;
            if (req is null || string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "title is required" });
            if (string.IsNullOrWhiteSpace(req.Content)) return Results.BadRequest(new { error = "content is required" });

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "handover-create", "", req, ct, machineName: d.MachineName);
            if (sr is null || !sr.Ok) return MapDirectorFailure(sr);
            var body = DirectorCommandRouter.ReadBody<HandoverDto>(sr);
            if (body is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
            return Results.Json(body, statusCode: StatusCodes.Status201Created);
        });

        // Gateway Cleanup CUT RESTORATION (SB-4a): delete a saved-handover document. Rides the handover-delete
        // verb (director-level; the ?path query rides in the payload). A path outside the handover folder is the
        // executor's BadRequest -> 400; a missing file its NotFound -> 404; a null result (Director not
        // tunnel-connected) is 502.
        app.MapDelete("/directors/{id}/handovers", async (HttpContext ctx, string id, string? path, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "path is required" });

            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "handover-delete", "", new RepoDeleteRequest { Path = path }, ct, machineName: d.MachineName);
            if (sr is null || !sr.Ok) return MapDirectorFailure(sr);
            return Results.Content(sr.BodyJson ?? "{\"removed\":true}", "application/json");
        });

        app.MapGet("/directors/{id}/fs/list", async (HttpContext ctx, string id, string? path, CancellationToken ct) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var err)) return err;

            // Gateway Cleanup Phase 2 (PR D): ride the tunnel first (fs-list verb, director-level; the ?path
            // query rides in the payload). A non-Ok stream result (e.g. the Director core's bad-path BadRequest)
            // collapses to 502, exactly as the HTTP path surfaced a null.
            var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, id, "fs-list", "", new FsListRequest { Path = path }, ct, machineName: d.MachineName);
            if (sr is not null)
            {
                if (!sr.Ok) return TunnelFailure(sr);
                var body = DirectorCommandRouter.ReadBody<DirectoryListingDto>(sr);
                if (body is null) return Results.StatusCode(StatusCodes.Status502BadGateway);
                return Results.Json(body);
            }

            // Post-cut: tunnel-only. A null result (Director not connected) stays 502, but now says so instead of arriving as a silent bare status.
            return TunnelFailure(null);
        });

        app.MapPost("/directors/{id}/sessions/github", async (HttpContext ctx, string id, GitHubSessionRequest req) =>
        {
            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var d, out var ownerErr)) return ownerErr;
            if (req is null || string.IsNullOrWhiteSpace(req.Owner) || string.IsNullOrWhiteSpace(req.Repo))
                return Results.BadRequest(new { error = "owner and repo are required" });

            FileLog.Write($"[GatewayEndpoints] POST /directors/{id}/sessions/github: {req.Owner}/{req.Repo} mode={req.TriggerMode}");

            // Gateway Cleanup Phase 2: create rides the target Director's stream (create-from-github verb,
            // director-level so SessionId is ""). Tunnel-only: a null return means the Director is not
            // connected, and a non-Ok stream result collapses to 502 - both surface as the error below.
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

            if (!TryResolveOwnedDirector(ctx, tenantBoundary, registry, id, out var director, out var ownerErr))
                return ownerErr;

            if (string.IsNullOrWhiteSpace(body.Reason))
            {
                FileLog.Write($"[GatewayEndpoints] DELETE director REJECTED (no reason): id={id} client={caller}");
                return Results.BadRequest(new { error = "reason is required: state why this Director is being shut down" });
            }

            // Post-cut: read the live session list from the push store (it carries the same SessionDto incl.
            // Status). A Director with no fresh push is not connected to the tunnel, so the live count is
            // unknowable and the session gate is skipped below. MTR-01: read the push store under the REQUEST's
            // own tenant (the same tenant the Director was just resolved in), never a hard-coded Local - on
            // hosted the Director lives in its account's partition, and Local would read the wrong one.
            var gateTenant = ResolveReadTenant(ctx, tenantBoundary)!.Value;
            var cachedSessions = pushedSessions?.TryGetFresh(gateTenant, director.DirectorId, streamStaleResolved);
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
            // director-level so SessionId is ""). Tunnel-only: there is no HTTP arm. POST /shutdown stays on the
            // Director loopback floor for the local launcher; this tunnel verb triggers the same in-process
            // self-shutdown.
            var shutdownSr = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "shutdown", "", null, CancellationToken.None, machineName: director.MachineName);
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

        app.MapGet("/sessions/{sid}/summary", async (HttpContext ctx, string sid, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Tunnel-only. The Director's summary core sets DirectorId in its body, so the pass-through matches.
            if (director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "summary", sid, null, ct, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : TunnelFailure(streamResult);
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
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Tunnel-only (verb "git-status"). The Ok body IS the GitSnapshot JSON, passed through unchanged.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "git-status", sid, null, ctx.RequestAborted, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : TunnelFailure(streamResult);
        });

        // Handover info proxy (issue #1214). Forwards to whichever Director owns the session and returns
        // the desktop "Handover info" identity block (name, session id, repo, director id, machine,
        // version) for a browser. Gated by the same Bearer/device-key auth as every other session route
        // (the global AuthMiddleware 401s a credential-less request before it reaches here). The Director
        // address is never leaked: this returns HandoverInfoDto, which carries no Control API endpoint,
        // and the resolved ControlEndpoint stays server-side. 404 when the session is unknown to every
        // Director; 502 when the owning Director is unreachable (never a silent empty body).
        app.MapGet("/sessions/{sid}/handover", async (HttpContext ctx, string sid, CancellationToken ct) =>
        {
            // Issue #1240: resolve the owner through the same cache fast path as every other per-session route.
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Tunnel-only. The Director's handover core sets DirectorId in its body, so the pass-through matches.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "handover", sid, null, ct, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : TunnelFailure(streamResult);
        });

        // Recap proxy. Both endpoints transparently forward to whichever Director owns the
        // session. The Director side does the heavy lifting (claude --print + cache); this
        // is just routing.
        app.MapGet("/sessions/{sid}/recap", async (HttpContext ctx, string sid, CancellationToken ct) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            // Tunnel-only (read the cached recap). This is the READ; the slow generate (POST) is handled separately.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "recap", sid, null, ct, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json")
                : TunnelFailure(streamResult);
        });

        app.MapPost("/sessions/{sid}/recap", async (string sid, HttpContext ctx) =>
        {
            var (director, session) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
            if (session is null || director is null)
                return Results.NotFound(new { error = "session not found across any director" });
            var model = ctx.Request.Query["model"].ToString();
            FileLog.Write($"[GatewayEndpoints] POST /recap: sid={sid}, director={director.DirectorId}, model={model ?? "(default)"}");
            // Gateway Cleanup (Phase 2, PR C): tunnel-first. Like wingman-ask this is a SLOW LLM call, so the
            // request ct (ctx.RequestAborted) threads into the SignalR invocation (no per-invocation timeout;
            // keep-alive pings sustain the long await) - synchronous browser contract byte-identical. A null
            // The Ok body IS the RecapResponse JSON, returned 201 as before.
            // Post-cut: tunnel-only. A null result (Director not connected) collapses to 502.
            var streamResult = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "recap-generate", sid,
                new RecapGenerateRequest { Model = model }, ctx.RequestAborted,
                // Runs a language model on the Director before it can answer, so it gets the longer wait.
                timeout: DirectorCommandRouter.LanguageModelCommandTimeout, machineName: director.MachineName);
            return streamResult is not null && streamResult.Ok && !string.IsNullOrEmpty(streamResult.BodyJson)
                ? Results.Content(streamResult.BodyJson, "application/json", null, StatusCodes.Status201Created)
                : Results.Problem("recap failed", statusCode: StatusCodes.Status502BadGateway);
        });

        app.MapPost("/handover", async (HttpContext ctx, HandoverRequest req) =>
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

            var (sourceDirector, sourceSession) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, req.FromSessionId, pushedSessions, streamStaleResolved, owners);
            if (sourceSession is null || sourceDirector is null)
                return Results.NotFound(new { error = "source session not found across any director" });

            DirectorDto? targetDirector = null;
            if (!string.IsNullOrEmpty(req.ToDirectorId)
                && !string.Equals(req.ToDirectorId, sourceDirector.DirectorId, StringComparison.OrdinalIgnoreCase))
            {
                // Issue #1869: resolve the TARGET Director in the REQUEST'S OWN tenant. The id here is
                // client-supplied, and the fleet-global lookup this replaced would answer about a Director
                // belonging to another account - so whether another tenant's Director exists changed this
                // caller's answer. That mattered little while the route was unreachable on hosted; this change
                // makes it reachable, so activating it on a fleet-global lookup would open a cross-tenant path
                // in the act of fixing one. A caller can now only ever name a Director it owns.
                var targetTenant = ResolveReadTenant(ctx, tenantBoundary);
                targetDirector = targetTenant is null ? null : registry.Get(targetTenant.Value, req.ToDirectorId);
                if (targetDirector is null)
                    return Results.NotFound(new { error = "target director not found" });
            }

            if (targetDirector is null)
            {
                // Same-Director: proxy the entire request. Gateway Cleanup Phase 2: ride the tunnel first
                // (handover-generate verb, director-level so SessionId is ""). Tunnel-only: a null return means
                // the Director is not connected, and a non-Ok stream result collapses to 502.
                HandoverResponse? body; string? err;
                // Runs a language model on the Director before it can answer, so it gets the longer wait.
                var hgSr = await DirectorCommandRouter.TrySendAsync(sendCommand, sourceDirector.DirectorId, "handover-generate", "", req, CancellationToken.None,
                    timeout: DirectorCommandRouter.LanguageModelCommandTimeout, machineName: sourceDirector.MachineName);
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
            var spawnSr = await DirectorCommandRouter.TrySendAsync(sendCommand, targetDirector.DirectorId, "create", "", spawnReq, CancellationToken.None, machineName: targetDirector.MachineName);
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

        app.MapPost("/fanout", async (HttpContext ctx, FanoutRequest req) =>
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
                var (d, s) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, sid, pushedSessions, streamStaleResolved, owners);
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
                var (sd, ss) = await LocateSessionForRequestAsync(ctx, tenantBoundary, registry, req.FromSessionId, pushedSessions, streamStaleResolved, owners);
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
                // Fanout delivery rides the tunnel (prompt verb). Tunnel-only: there is no HTTP arm.
                bool ok; PromptResponse? body; string? err;
                var deliverSr = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "prompt", sid, promptReq, CancellationToken.None, machineName: director.MachineName);
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
            // The removal carries its tenant now, but this stream has no tenant of its own to filter against
            // (it is the fleet-global event feed), so it still announces every removal to every listener. It
            // is on the existing list of not-yet-tenant-aware routes and is converted with them, not here.
            void OnRemoved(DirectorRemoval removal) => queue.Writer.TryWrite(new GatewayEvent("director.removed", removal.DirectorId));

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

        // Windows-only: this launches the Windows desktop cc-director.exe via ShellExecute, which
        // only exists on a Windows install. Off Windows the route is not mapped.
        if (OperatingSystem.IsWindows())
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
                    // MTR-01: this route ShellExecutes a cc-director.exe on the GATEWAY's own machine, so the
                    // newly-registered instance is always a Local-tenant entry - resolve it within Local, not by
                    // a bare cross-tenant scan.
                    var d = registry.Get(TenantId.Local, newId)!;
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

    /// <summary>
    /// The ROLE UNIVERSE for a fold that runs OUTSIDE the /sessions roster loop: every tunnel-connected
    /// Director's pushed roster, grouped by Director.
    ///
    /// The roster handler builds its universe inline as it walks the Directors it already pulled. The two
    /// other folding routes (/exes/list and GET /sessions/{sid}) have no such loop, and both need the whole
    /// fleet for the same reason: a session's role depends on whether its controller is alive, and the
    /// controller may live on a Director this route was never otherwise interested in. /exes/list is the
    /// sharp case - it is a LOCAL-MACHINE page, but a local Worker's Manager can be on another machine
    /// entirely, so the universe is deliberately the whole fleet and not the local Directors.
    ///
    /// Returns copies (the push store hands out deep copies), so stamping the result never writes through
    /// to the cache.
    /// </summary>
    internal static Dictionary<string, IReadOnlyList<SessionDto>> FleetByDirector(
        DirectorRegistry registry, Streaming.PushedSessionStore? pushedSessions, TimeSpan streamStale,
        TenantId tenant)
    {
        var byDirector = new Dictionary<string, IReadOnlyList<SessionDto>>(StringComparer.Ordinal);
        if (pushedSessions is null) return byDirector;
        foreach (var d in registry.ListDirectors())
        {
            var cached = pushedSessions.TryGetFresh(tenant, d.DirectorId, streamStale);
            if (cached is not null) byDirector[d.DirectorId] = cached;
        }
        return byDirector;
    }

    /// <summary>
    /// Resolve a request's tenant for a session READ (Hosted Multi-Tenancy, session-serving PR1). Null means
    /// the caller must be DENIED (403): on the hosted Gateway an authenticated request whose device key has no
    /// bound tenant is refused, NEVER served the Local partition (which would be a wrong-tenant read waiting to
    /// happen). Self-host, or no boundary (older callers / tests), is always Local - behavior unchanged.
    /// </summary>
    internal static TenantId? ResolveReadTenant(HttpContext ctx, Tenancy.HostedTenantBoundary? boundary)
        => boundary is null ? TenantId.Local : boundary.ResolveRequestTenant(ctx);

    /// <summary>
    /// MTR-01 (Codex round 1): the answer for the legacy same-machine HTTP discovery plane (register /
    /// heartbeat / doorbell / unregister) when this is the hosted Gateway. That plane is a self-host-only
    /// concept - hosted Directors ride the tunnel - and every entry it writes is Local-keyed, so leaving it
    /// reachable on hosted is the Local-shadow registration / event-ring injection path. 403, explicit.
    /// </summary>
    private static IResult LegacyDiscoveryPlaneUnavailable()
        => Results.Json(new { error = "the same-machine HTTP discovery plane is not available on the hosted Gateway" },
            statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// MTR-01: resolve a per-director route's target Director IN THE REQUEST'S OWN TENANT. Every client-serving
    /// <c>/directors/{id}/...</c> route (and the <c>/interrupted</c> plane's by-id legs) resolves its Director
    /// through this, so a client-supplied id can only ever name a Director the caller's authenticated device key
    /// owns. The registry has no bare-id accessor anymore, so there is no way to reach another tenant's entry.
    ///
    /// On success returns true and hands back the caller's own Director; on failure returns false and the
    /// <see cref="IResult"/> the route must return unchanged:
    ///   - 403 when the request has no bound tenant (deny-by-default, NEVER the Local partition);
    ///   - 404 when the id is not in the caller's tenant (NEVER another tenant's freshest match).
    /// Self-host is unchanged: there the request tenant is always Local and the registry holds the one tenant's
    /// Directors, so this is an ordinary present/absent lookup.
    /// </summary>
    private static bool TryResolveOwnedDirector(
        HttpContext ctx, Tenancy.HostedTenantBoundary? boundary, DirectorRegistry registry, string id,
        out DirectorDto director, out IResult error)
    {
        director = null!;
        var reqTenant = ResolveReadTenant(ctx, boundary);
        if (reqTenant is null)
        {
            error = Results.Json(new { error = "no tenant is bound to this request" },
                statusCode: StatusCodes.Status403Forbidden);
            return false;
        }

        var found = registry.Get(reqTenant.Value, id);
        if (found is null)
        {
            error = Results.NotFound(new { error = "director not found" });
            return false;
        }

        director = found;
        error = null!;
        return true;
    }

    /// <summary>
    /// THE route-facing session locator (issue #1869). Every per-session HTTP route resolves its session
    /// through this, and it takes the REQUEST - so the tenant comes from the caller's authenticated device
    /// key and there is no tenant argument for a route to get wrong.
    ///
    /// This exists because the read path was made tenant-aware while the command path was not: twenty-three
    /// per-session routes passed a literal <see cref="TenantId.Local"/> straight into
    /// <see cref="LocateSessionAsync"/>. On hosted, where the request's tenant is a real account, that read
    /// the empty Local partition and returned "session not found across any director" - so prompt, interrupt,
    /// escape, buffer, summary, git, wingman, role, hold and delete ALL 404'd for a correctly enrolled
    /// Director whose sessions the roster was listing perfectly. You could see everything and do nothing, and
    /// because /buffer was among them the terminal view was dead too.
    ///
    /// It was INVISIBLE on self-host, because there the request's tenant genuinely IS Local, so every test and
    /// every developer machine agreed with the bug. Only driving a real Director against the hosted box found
    /// it. That is why this is a separate entry point rather than twenty-three corrected arguments: a fixed
    /// argument can be got wrong again by the next route, and would be just as invisible.
    ///
    /// DENY BY DEFAULT: a request whose device key resolves to no tenant locates NOTHING. It does not fall
    /// back to Local, and the caller gets its route's ordinary not-found answer, which is the truthful one -
    /// a caller with no tenant owns no sessions. The refusal is logged distinctly so it is never confused with
    /// an ordinary miss.
    ///
    /// The <see cref="LocateSessionAsync"/> primitive still takes an explicit tenant. Its remaining callers,
    /// named exactly rather than waved at:
    ///  - the voice sweep in GatewayHost, a background pass with no request, pinned to Local until the voice
    ///    state it mutates is partitioned (converting it first would ARM a cross-tenant audio path, not close
    ///    one);
    ///  - <c>SessionVerbClient.ResolveAsync</c>, also pinned to Local - and it is NOT purely background: it is
    ///    reached from the wingman voice endpoint's request handling, so those voice reads stay inert on
    ///    hosted rather than working. That is deliberate and it is the same precondition as the sweep, but it
    ///    is a REMAINING GAP, not a solved case, and it is booked as its own work;
    ///  - the dictation completion path, DELIBERATELY LEFT LOCAL. Tenant-scoping only its /complete leg was
    ///    tried in this pull request and REMOVED in review: it would have activated a route whose upload
    ///    store is unpartitioned - one global root keyed solely by a caller-supplied upload id, with no
    ///    tenant on the record, and sibling routes (upload, chunk, ack, abandon) that authorize a device but
    ///    never check whose upload it is. Reaching the route is not the boundary; a shared identifier space
    ///    is. Booked as issue #1884, with the same precondition as voice: partition the state first.
    ///
    /// This does NOT make the mistake impossible for the next route, and saying so would overstate it: the
    /// primitive is still internal and a new route could call it with any tenant it liked. What it does is
    /// remove the tenant argument from the path a route would naturally take, and make the omission visible -
    /// review caught a route that had been left on the primitive precisely because a test existed that would
    /// have covered it. Enforcing a route-versus-background split in the type system is follow-up work.
    /// </summary>
    internal static Task<(DirectorDto? director, SessionDto? session)> LocateSessionForRequestAsync(
        HttpContext ctx, Tenancy.HostedTenantBoundary? boundary,
        DirectorRegistry registry, string sid,
        Streaming.PushedSessionStore? pushedSessions, TimeSpan streamStale,
        SessionOwnerCache? owners = null)
    {
        var tenant = ResolveReadTenant(ctx, boundary);
        if (tenant is null)
        {
            FileLog.Write($"[GatewayEndpoints] LocateSessionForRequestAsync: DENIED for sid={sid} - the authenticated device key resolves to no tenant, so nothing is located (never the Local partition)");
            return Task.FromResult<(DirectorDto?, SessionDto?)>((null, null));
        }

        return LocateSessionAsync(registry, sid, pushedSessions, streamStale, tenant.Value, owners);
    }

    /// <summary>
    /// THE fold. Resolve every session's role from the WHOLE fleet, then stamp the presentation fold
    /// (EffectiveColor / StateLabel / TriageBucket / NeedsYouSince) onto the response set.
    ///
    /// TWO LISTS, AND THE DIFFERENCE IS THE WHOLE POINT (defect 13). <paramref name="roleUniverse"/> is the
    /// UNFILTERED fleet - every session the Gateway can see. <paramref name="toStamp"/> is only what this
    /// response will return. They differ whenever a caller filters, and the role MUST be resolved from the
    /// universe: "is my controller alive?" is a question about sessions the caller may have filtered out.
    /// Resolving it from the filtered set let `?statusColor=red` drop a WORKING controller out of the
    /// liveness set, reclassify its Worker as Standalone, and un-suppress a red the human should never have
    /// been shown - a worker nagging the human because of a query parameter.
    ///
    /// <paramref name="toStamp"/> entries must APPEAR IN <paramref name="roleUniverse"/> (matched by session
    /// id); references or copies both work, and one that is absent fails loud. This used to require by-
    /// REFERENCE entries, with a copy silently yielding a null SessionRole - see the note at the
    /// FleetRoleResolver.Stamp call below for why that requirement was removed rather than documented.
    ///
    /// INTERNAL, not private, and called by exactly three routes - the roster, /exes/list and
    /// GET /sessions/{sid}. Those three used to fold independently (or not at all), which is how they came
    /// to disagree; there is one implementation because there must only ever be one answer.
    /// </summary>
    internal static void StampFleetRolesAndFold(
        List<SessionDto> roleUniverse,
        IReadOnlyList<SessionDto> toStamp,
        Func<string, bool, DateTime?>? needsYouStampFor = null,
        Snooze.SnoozeRegistry? snoozeRegistry = null)
    {
        if (roleUniverse is null) throw new ArgumentNullException(nameof(roleUniverse));
        if (toStamp is null) throw new ArgumentNullException(nameof(toStamp));

        var all = toStamp;

        // Snooze Length mission: an EXPIRED snooze must read as "needs you" again. The registry is the
        // source of truth for the timer; the cleanest fold (issue #1177 keeps the Gateway the single
        // fold, decision #6) is to override OnHold=false on this aggregated DTO copy BEFORE the color /
        // label / triage are computed, so SessionOrdering.Classify puts the session straight back into
        // NeedsYou with no new classification logic. This is a pure, continuous overlay: while a snooze
        // is expired every read reports the session as un-held, so it never flickers back to "Snoozed"
        // between the moment it expires and the moment its Director confirms the clear. A DEAD Director's
        // session still carries its last-known OnHold=true in the cached roster; this overlay is exactly
        // what surfaces it anyway - the dead-man's-switch.
        // THE GATEWAY OWNS HOLD. The registry is not consulted as an overlay on a Director's answer any
        // more - it IS the answer. Whatever a Director reported in SessionDto.HoldState is overwritten
        // here, unread, because a Director does not decide hold and its copy is a display mirror this
        // Gateway wrote in the first place.
        //
        // This one assignment is what makes every surface agree by construction: the roster, /exes/list
        // and GET /sessions/{sid} all fold here, and the fold is the only place hold is decided. It
        // replaces three workarounds that existed solely because the truth used to live on the Director -
        // a read-time OnHold=false overlay, a tunnel round-trip to ask a Director for its hold, and a
        // nudge-write to beg it to change. All three are gone.
        //
        // An elapsed snooze reads None straight out of HoldStateFor, so "expired" needs no special case:
        // the owner asked for N minutes of quiet and got them. SnoozeExpired is display metadata, not a
        // hold state - it says "this one JUST came back BECAUSE its timer ran out", which the clients render
        // as a distinct "Snooze ended" badge and the phone announces once.
        var nowUtc = DateTime.UtcNow;
        if (snoozeRegistry is not null)
            foreach (var s in all)
            {
                if (string.IsNullOrEmpty(s.SessionId)) continue;
                s.HoldState = snoozeRegistry.HoldStateFor(s.SessionId, nowUtc);
                // Expiry is a REGISTRY fact, not a Director one, and it is ASSIGNED both ways every fold -
                // never OR-ed in. The DTO reaching this fold can already carry SnoozeExpired=true (the
                // FleetRosterCache stores folded clones and re-serves them), so a one-way "set true when
                // expired" would latch the badge on forever: it never wrote false, so a session that left
                // needs-you by any route OTHER than timer expiry - work deleting the entry (the working
                // edge), a re-snooze arming a fresh clock, an owner turn - kept a stale badge it never
                // earned. Assigning = IsExpired makes the badge mean EXACTLY one thing, both directions:
                // true only while an armed entry's clock has elapsed, false the instant that stops being so.
                s.SnoozeExpired = snoozeRegistry.IsExpired(s.SessionId, nowUtc);
            }

        // Defect 5: the role resolution moved to Fleet.FleetRoleResolver so this roster read and the
        // FleetRoleObserver (which pushes the role down to the owning Director's desktop) share ONE
        // implementation. Two copies would be two authorities, and when they drifted the desktop and the
        // phone would disagree again - which IS defect 5. Behaviour here is unchanged: every branch still
        // assigns, so an inbound role never survives this pass.
        //
        // Defect 13: resolved across the ROLE UNIVERSE, never the filtered response set.
        //
        // This passes BOTH lists, so the resolver stamps toStamp by SESSION ID rather than relying on its
        // entries being the same OBJECTS as the universe's. That by-reference requirement used to be a
        // comment on this method, and a comment is exactly the wrong place for it: an equal-but-copied DTO
        // satisfied the type system, returned a null SessionRole, and folded from it SILENTLY - this
        // mission's own defect shape (a consumer reading a value production never put there), pre-loaded for
        // the next caller. The overload makes it structurally impossible instead: references or copies both
        // work, and a session absent from the universe fails loud.
        Fleet.FleetRoleResolver.Stamp(roleUniverse, all);

        foreach (var s in all)
        {
            var effectiveColor = SessionOrdering.EffectiveColor(s);
            s.EffectiveColor = effectiveColor;
            // The "Dumb Clients" palette slice: resolve the colour NAME to its pixel HEX through the ONE
            // canonical map, right here beside the name, so the /sessions consumers (the web phone and the
            // Cockpit) paint that hex verbatim and carry no name->hex table that can drift. The DESKTOP does
            // NOT receive this hex - it is held to the SAME canonical values at compile time (StatusPalette
            // references SessionColorPalette) and by the agreement tests (approved Fork B: no pushed hex).
            // So this stamp is for the /sessions wire only; the display-state push still carries the name.
            //
            // FAIL LOUD on a name the canonical map does not know. HexFor returns the magenta sentinel for an
            // unknown name, and a valid-looking #FF00FF would otherwise sail through the web unlogged - a
            // silent magenta, the exact class of quiet failure this mission ends. A fold colour the palette
            // does not know is a bug (the fold learned a name nobody taught the palette), so say so here.
            if (!SessionColorPalette.Knows(effectiveColor))
                FileLog.Write($"[GatewayEndpoints] UNKNOWN FOLD COLOUR '{effectiveColor}' for session " +
                              $"{s.SessionId} - not in SessionColorPalette; stamping the magenta BROKEN sentinel. " +
                              "The fold emitted a colour name the canonical palette does not know; see " +
                              "docs/new_architecture/session-state.html.");
            s.EffectiveColorHex = SessionColorPalette.HexFor(effectiveColor);
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
            // The armed-snooze deadline, so a client can show "Snoozed - wakes in Xh". Read straight from the
            // registry (the sole timer owner) alongside HoldState above; null when there is no running clock
            // (no snooze, or a deferred one that has not landed). Folded HERE so the roster, the observer that
            // pushes this down to the desktop, and the single-session read all emit the same deadline.
            s.SnoozeUntil = snoozeRegistry?.SnoozeUntilFor(s.SessionId);
        }
    }

    // Gateway Cleanup CUT RESTORATION (SB-4a): map a tunnel command's null-or-failed result to the faithful
    // HTTP status the old REST route returned. A null result (Director not tunnel-connected) is 502; a typed
    // failure preserves the executor's BadRequest/NotFound as 400/404 so the repos/handover-management contract
    // - which the consumers and the re-added tests assert - is byte-identical to the pre-cut REST surface. Any
    // other status collapses to 502. Callers use this only on the not-Ok path (sr is null OR !sr.Ok).
    //
    // Stable Release (v1.3.0), Tier 1 item 1: the two Gateway-synthesized outcomes get their own arms, and both
    // carry the message in the BODY. Without these arms they would fall into the bare collapse below - which
    // sends no body at all - and the explanation the whole item exists to deliver would be silently dropped one
    // line before reaching the caller. The Director-sent statuses above and the collapse below are untouched, so
    // the byte-identical pre-cut contract still holds for every status that existed before.
    // Stable Release (v1.3.0), Tier 1 item 1: the message-preserving twin of MapDirectorFailure, for the many
    // endpoints that answer a failed tunnel command with a BARE 502 carrying no body at all.
    //
    // The router now explains every dropped command, but an endpoint that throws the explanation away leaves the
    // user staring at a naked 502 - so the fix would compute a perfect message nobody ever reads. This carries
    // the body for the three outcomes that have something to say, and changes NOTHING else:
    //   - not connected     -> 502 (unchanged status) + "the Director is offline"
    //   - timed out         -> 504 + the router's message
    //   - dropped mid-flight-> 502 (unchanged status) + the router's message
    //   - any other status  -> the exact bare 502 it returns today, byte-for-byte
    // Deliberately NOT MapDirectorFailure: that maps BadRequest/NotFound to 400/404, and these call sites ship
    // a 502 for those today. Changing that is a contract change and belongs to a different piece of work.
    /// <summary>
    /// The settings legs' failure mapping. <see cref="TunnelFailure"/> words the two GATEWAY-synthesized
    /// outcomes (no tunnel, timeout, mid-command drop) well, so those are handed straight to it. What it does
    /// NOT do is carry a DIRECTOR-sent failure's message: its default branch collapses every one to a bare 502
    /// with no body. That is exactly the trap item 1 paid for - a command that computes a perfect explanation
    /// which no endpoint ever shows a human. The settings verbs return real, actionable messages ("request body
    /// must be a JSON object"; a refused gateway patch saying nothing was written), so these legs map the
    /// Director's own status to its HTTP equivalent and carry its words through to the person who typed the
    /// edit. Scoped to these two routes deliberately: the other legs' bare-502 behaviour is not this item's to
    /// change.
    /// </summary>
    private static IResult DirectorAnswerFailure(DirectorCommandResult? sr, string? machineName)
    {
        if (sr is null
            || sr.Status is DirectorCommandStatus.Timeout or DirectorCommandStatus.TunnelDropped)
            return TunnelFailure(sr, machineName);

        var status = sr.Status switch
        {
            DirectorCommandStatus.BadRequest => StatusCodes.Status400BadRequest,
            DirectorCommandStatus.NotFound => StatusCodes.Status404NotFound,
            DirectorCommandStatus.Conflict => StatusCodes.Status409Conflict,
            DirectorCommandStatus.Locked => StatusCodes.Status423Locked,
            _ => StatusCodes.Status502BadGateway,
        };
        return Results.Json(new { error = sr.Error }, statusCode: status);
    }

    private static IResult TunnelFailure(DirectorCommandResult? sr, string? machineName = null)
    {
        if (sr is null)
        {
            var offline = string.IsNullOrWhiteSpace(machineName)
                ? "The Director is not connected right now, so the command was not delivered."
                : $"The Director on {machineName} is not connected right now, so the command was not delivered.";
            return Results.Json(new { error = offline }, statusCode: StatusCodes.Status502BadGateway);
        }
        return sr.Status switch
        {
            DirectorCommandStatus.Timeout => Results.Json(new { error = sr.Error },
                statusCode: StatusCodes.Status504GatewayTimeout),
            DirectorCommandStatus.TunnelDropped => Results.Json(new { error = sr.Error },
                statusCode: StatusCodes.Status502BadGateway),
            // A DIRECTOR-SENT failure (BadRequest / NotFound / Conflict / Locked / a plain Failed). The
            // Director computed a real explanation and this branch used to drop it on the floor, returning a
            // bodyless 502 - the human got a bare status they could not act on, and the words that would have
            // told them what to do were discarded one hop from being shown.
            //
            // The STATUS stays 502, byte-identical, for every one of these. That is deliberate: these legs
            // ship a 502 for a Director BadRequest/NotFound today, and mapping them to 400/404 (what
            // MapDirectorFailure does) would change a shipped contract. This carries the words and moves
            // nothing else.
            _ => Results.Json(new { error = sr.Error }, statusCode: StatusCodes.Status502BadGateway),
        };
    }

    private static IResult MapDirectorFailure(DirectorCommandResult? sr)
    {
        // The Director is not tunnel-connected. The status stays 502 - no contract moves - but it now says why
        // instead of arriving as a silent bare status the user cannot act on.
        if (sr is null)
            return Results.Json(new { error = "The Director is not connected right now, so the command was not delivered." },
                statusCode: StatusCodes.Status502BadGateway);
        return sr.Status switch
        {
            DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = sr.Error ?? "bad request" }),
            DirectorCommandStatus.NotFound => Results.NotFound(new { error = sr.Error ?? "not found" }),
            DirectorCommandStatus.Timeout => Results.Json(new { error = sr.Error ?? "The Director did not answer in time." },
                statusCode: StatusCodes.Status504GatewayTimeout),
            DirectorCommandStatus.TunnelDropped => Results.Json(new { error = sr.Error ?? "The connection to the Director dropped while the command was being sent." },
                statusCode: StatusCodes.Status502BadGateway),
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
    // owning session snapshot / terminal buffer over the tunnel (snapshot / buffer verbs). Tunnel-only: there
    // is no HTTP arm.
    // Shared by both poll sites so there is one tunnel-branch to prove.
    private static async Task<SessionDto?> SnapshotTunnelFirstAsync(
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand,
        DirectorDto director, string sid, CancellationToken ct)
    {
        var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "snapshot", sid, null, ct, machineName: director.MachineName);
        return sr is not null && sr.Ok ? DirectorCommandRouter.ReadBody<SessionDto>(sr) : null;
    }

    private static async Task<BufferResponse?> BufferTunnelFirstAsync(
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand,
        DirectorDto director, string sid, int lines, long? since, CancellationToken ct)
    {
        var sr = await DirectorCommandRouter.TrySendAsync(sendCommand, director.DirectorId, "buffer", sid,
            new BufferRequest { Lines = lines, Raw = false, Since = since }, ct,
            machineName: director.MachineName);
        return sr is not null && sr.Ok ? DirectorCommandRouter.ReadBody<BufferResponse>(sr) : null;
    }

    // Gateway Cleanup mission (post-cut): the pushed stream cache is the ONLY session locator. A Director with
    // no fresh push is not connected to the tunnel, so its sessions are unreachable and location returns null -
    // the same not-found the old HTTP-pull fallback produced when a Director was down. Kept Task-returning so
    // the many `await LocateSessionAsync(...)` call sites (and SessionVerbClient.ResolveAsync) are unchanged.
    internal static Task<(DirectorDto? director, SessionDto? session)> LocateSessionAsync(
        DirectorRegistry registry, string sid,
        Streaming.PushedSessionStore? pushedSessions, TimeSpan streamStale,
        TenantId tenant,
        SessionOwnerCache? owners = null)
    {
        if (pushedSessions is not null)
        {
            var located = pushedSessions.TryLocate(tenant, sid, streamStale);
            if (located is not null)
            {
                var (directorId, pushedSession) = located.Value;
                // Issue #1847: resolve the owning Director IN THE SAME TENANT the session was located under.
                // The pushed store is already tenant-scoped, but the registry lookup used to be by bare id, so
                // once the registry could hold that id for more than one tenant, this line could stamp ANOTHER
                // tenant's machine name, operating system user and version onto the session being served here.
                var owner = registry.Get(tenant, directorId);
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
        var bin = CcStorage.Bin();
        foreach (var name in names)
        {
            var candidate = Path.Combine(bin, name);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    internal sealed record GatewayEvent(string Type, string Id);

    /// <summary>One-line-safe log form of a caller-supplied string (reason fields etc.).</summary>
    private static string Truncate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var oneLine = s.Replace('\r', ' ').Replace('\n', ' ');
        return oneLine.Length <= 200 ? oneLine : oneLine[..200] + "...";
    }

    /// <summary>
    /// The <c>settings-put</c> verb payload. Mirrors the Director-side <c>SettingsPutRequest</c>: the settings
    /// patch travels as an opaque JSON object under one property, so the command envelope stays well-formed
    /// without the Gateway modelling the Director's config keys.
    /// </summary>
    private sealed class SettingsPutPayload
    {
        public JsonNode? Settings { get; set; }
    }
}
