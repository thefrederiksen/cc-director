using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi.Chat;
using CcDirector.Core.Account;
using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Configuration;
using CcDirector.Core.Fleet;
using CcDirector.Core.History;
using CcDirector.Core.Network;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Wingman;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps the Director's Control API endpoints onto the provided IEndpointRouteBuilder.
/// Now serves both REST JSON and a self-contained HTML Director UI.
/// </summary>
internal static class ControlEndpoints
{
    public static void Map(IEndpointRouteBuilder app, SessionManager sessionManager, string directorId, string version, Func<Task> requestShutdownAsync, bool authEnabled = false, RepositoryRegistry? repositoryRegistry = null, TurnSummaryCache? turnSummaryCache = null, string? gatewayUrl = null, ProactiveExplainService? proactiveExplain = null, GatewayConnectionMonitor? gatewayMonitor = null, Func<TailnetEndpointResolution>? resolveTailnetEndpoint = null, Func<GatewayClient?>? gatewayClientProvider = null, MessageSteward? messageSteward = null, MissionStore? missionStore = null, Func<CancellationToken, Task<SignedInUser?>>? signedInUserResolver = null)
    {
        var logoutVisibility = authEnabled ? "" : "style=\"display:none\"";
        // URL of the Gateway this Director is registered with, for the "Gateway" nav
        // button in the served HTML. Empty when no gateway.url is configured -- the
        // pages hide the button rather than render a dead link.
        var gatewayUrlAttr = System.Net.WebUtility.HtmlEncode(gatewayUrl ?? "");

        // Issue #335: identity fields populated by the Director at request time.
        // Called on every session DTO request; the resolver re-runs the detection ladder
        // each time so a Tailscale daemon coming up between requests self-heals without
        // requiring a Director restart.
        // When no resolver is wired (tests, old callers) the fields default to empty -
        // the Gateway back-compat pass then enriches them exactly as before.
        SessionDto MapWithIdentity(Session s, TurnSummaryCache? cache = null)
        {
            var (mn, usr, ep) = resolveTailnetEndpoint is not null
                ? ResolveDirectorIdentity(resolveTailnetEndpoint)
                : (string.Empty, string.Empty, string.Empty);
            return Map(s, directorId, cache, mn, usr, ep, gatewayUrl);
        }

        // Create a session LOCALLY through the shared SessionCommandExecutor (issue #1177 Phase 1), then
        // build the identity-stamped 201 response. Shared by POST /sessions and the local branch of
        // POST /fleet/spawn so both create identically; the Machine routing field is advisory here (the
        // routing decision is made before the request reaches this Director).
        async Task<IResult> CreateLocalSessionAsync(NewSessionRequest? req)
        {
            var command = new DirectorCommand
            {
                Verb = "create",
                SessionId = "",
                PayloadJson = req is null ? "" : SessionCommandExecutor.Serialize(req),
            };
            // Pass the Mission store so a create-time MissionId (attach at spawn) resolves+validates
            // through the SAME executor path the Gateway stream down-channel uses.
            var createServices = new SessionCommandServices { ProactiveExplain = proactiveExplain, TurnSummaryCache = turnSummaryCache, MissionStore = missionStore };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, createServices);

            if (result.Status == DirectorCommandStatus.BadRequest)
                return Results.BadRequest(new { error = result.Error });
            if (result.Status != DirectorCommandStatus.Ok)
                return Results.Problem(result.Error ?? "create failed", statusCode: 500);

            var created = SessionCommandExecutor.Deserialize<SessionDto>(result.BodyJson);
            if (created is null || !Guid.TryParse(created.SessionId, out var newGuid))
                return Results.Problem("created session id missing", statusCode: 500);
            var session = sessionManager.GetSession(newGuid);
            return session is null
                ? Results.Problem("created session not found", statusCode: 500)
                : Results.Json(MapWithIdentity(session, turnSummaryCache), statusCode: 201);
        }

        // ===== Healthz =====
        app.MapGet("/healthz", () => Results.Json(new HealthDto
        {
            Status = "ok",
            Directors = 1,
            Sessions = sessionManager.ListSessions().Count,
            Version = version,
            ServerTime = DateTime.UtcNow,
            DirectorId = directorId,
            MachineName = Environment.MachineName,
        }));

        // ===== Two-way handshake callback (issues #223/#224) =====
        // The Gateway dials this with the nonce the Director just POSTed to its
        // /directors/{id}/verify. Deliberately NOT in DirectorAuth.PublicPaths: the
        // handshake must prove the SAME authenticated channel real Gateway traffic uses -
        // a token mismatch should fail verification loudly, not be bypassed by it.
        // Echoing the Director id lets the Gateway catch an advertised URL that reaches
        // the wrong process; recording the receipt lets THIS side independently confirm
        // the callback landed here (the anti-impostor cross-check in GatewayClient).
        app.MapGet("/verify/{nonce}", (string nonce) => Results.Json(new VerifyCallbackDto
        {
            DirectorId = directorId,
            Nonce = nonce,
            Known = gatewayMonitor?.RecordCallback(nonce) ?? false,
        }));

        // ===== Two-way handshake callback, WEBSOCKET leg =====
        // The Gateway dials this to prove it can complete a WebSocket UPGRADE to this Director -
        // the exact operation the Cockpit terminal stream depends on, and the one the plain GET
        // /verify above does NOT exercise. A Director can pass /verify (plain HTTP/1.1) while the
        // real stream path is dead (e.g. an h2-only proxy that cannot carry the upgrade, a blocked
        // upgrade, a missing Tailscale Serve mapping). Mirrors /verify - echoes id + nonce so a
        // wrong-process or token mismatch fails loudly - but over a real WS so a broken stream is
        // caught at install/connect time instead of by a user staring at "stream lost,
        // reconnecting...". Authenticated like /verify and the live stream.
        app.MapGet("/verify-ws/{nonce}", async (string nonce, HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("expected websocket upgrade");
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var dto = new VerifyCallbackDto
            {
                DirectorId = directorId,
                Nonce = nonce,
                Known = gatewayMonitor?.RecordCallback(nonce) ?? false,
            };
            try
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(dto);
                await ws.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ctx.RequestAborted);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "verify-ws complete", ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // Client or server going away mid-probe. Normal; the Gateway treats it as a fail.
            }
            catch (WebSocketException ex)
            {
                FileLog.Write($"[ControlEndpoints] /verify-ws/{nonce} socket dropped: {ex.Message}");
            }
        });

        // ===== HTML pages =====
        // The cards-grid Director (manager.html) is the default UI at "/" -- the
        // multi-session directory the phone lands on. The old text-only "Director
        // chat" screen was removed; per-session messaging lives in the session view.
        app.MapGet("/", (HttpContext ctx) =>
        {
            // If browser asks for JSON, give them session list (handy for curl users)
            if (!DirectorAuth.PrefersHtml(ctx))
                return Results.Json(sessionManager.ListSessions().Select(s => MapWithIdentity(s, turnSummaryCache)).ToList());

            var html = EmbeddedResources.Load("manager.html")
                .Replace("__LOGOUT_VISIBILITY__", logoutVisibility);
            return Results.Content(html, "text/html; charset=utf-8");
        });

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

            var actual = DirectorAuth.LoadOrCreateToken();
            if (!string.Equals(submitted, actual, StringComparison.Ordinal))
            {
                var html = EmbeddedResources.Load("login.html")
                    .Replace("__NEXT__", System.Web.HttpUtility.HtmlAttributeEncode(next))
                    .Replace("__ERROR__", "Wrong token. Check gateway-token.txt and try again.");
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(html);
                return;
            }

            ctx.Response.Cookies.Append(DirectorAuth.CookieName, actual, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddDays(30),
                IsEssential = true,
                // No Secure flag - we're on plain HTTP (loopback/Tailscale).
            });
            ctx.Response.Redirect(IsSafeRedirect(next) ? next : "/");
        });

        app.MapGet("/logout", (HttpContext ctx) =>
        {
            ctx.Response.Cookies.Delete(DirectorAuth.CookieName);
            return Results.Redirect("/login");
        });

        app.MapGet("/sessions/{sid}/view", (HttpContext ctx, string sid) =>
        {
            if (!Guid.TryParse(sid, out _))
                return Results.BadRequest(new { error = "invalid session id format" });
            var shortSid = sid.Substring(0, Math.Min(8, sid.Length));
            var html = EmbeddedResources.Load("session-view.html")
                .Replace("__SID__", sid)
                .Replace("__SHORT_SID__", shortSid)
                .Replace("__GATEWAY_URL__", gatewayUrlAttr);
            return Results.Content(html, "text/html; charset=utf-8");
        });

        // ===== REST: Sessions =====
        // Phase 3: Exited sessions are hidden by default. They aren't a "color" on
        // the directory map - if a session is gone, its card/row disappears. History
        // tooling can opt in via ?includeExited=true.
        app.MapGet("/sessions", (bool? includeExited) =>
        {
            var includeExitedActual = includeExited ?? false;
            var sessions = sessionManager.ListSessions()
                .Where(s => includeExitedActual || s.ActivityState != ActivityState.Exited)
                .Select(s => MapWithIdentity(s, turnSummaryCache))
                .ToList();
            return Results.Json(sessions);
        });

        // Gateway Cleanup Phase 0: the read runs through the shared SessionReadExecutor core so this REST
        // path and the Gateway stream down-channel are identical and cannot drift. The Director's local
        // response still uses its identity-stamped mapper (MachineName/User/TailnetEndpoint), so this
        // endpoint stays byte-identical; the executor returns the plain stream DTO that the Gateway stamps
        // during aggregation (exactly as the patch verb does). Phase 1 deletes this route.
        app.MapGet("/sessions/{sid}", async (string sid) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var command = new DirectorCommand { Verb = "snapshot", SessionId = sid };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            if (result.Status == DirectorCommandStatus.BadRequest)
                return Results.BadRequest(new { error = result.Error });
            if (result.Status != DirectorCommandStatus.Ok)
                return Results.NotFound(new { error = result.Error });

            var session = sessionManager.GetSession(guid);
            return session is null
                ? Results.NotFound(new { error = "session not found" })
                : Results.Json(MapWithIdentity(session, turnSummaryCache));
        });

        // The launch-time "fleet awareness" preamble for a session: its own identity plus the
        // cc-devthrottle commands to reach the rest of the fleet. The Claude SessionStart hook fetches this
        // and injects it as additionalContext so the agent knows the fleet instantly, with no
        // skill lookup. Plain text (not JSON) so a hook can drop it straight into a context field.
        app.MapGet("/sessions/{sid}/fleet-preamble", async (string sid, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            // Issue #800: the display name goes through the single composer so it is never the
            // bare folder name (legacy sessions with no CustomName get folder + type + disambiguator).
            var name = SessionName.DisplayName(session.CustomName,
                SessionName.FolderName(session.RepoPath),
                SessionName.Disambiguator(session.Id));

            // Issue #1357: name the signed-in DevThrottle user so the agent binds "me / my account /
            // email me" to that account. Resolved (cached) from the Gateway; null when no one is signed
            // in or no resolver is wired (tests, standalone) - the preamble then omits the identity line.
            SignedInUser? user = signedInUserResolver is null ? null : await signedInUserResolver(ct);

            // A session only ever calls its OWN Director, so this Director's machine name is the
            // session's machine.
            var text = FleetPreamble.Build(session.Id.ToString(), name, Environment.MachineName, session.RepoPath, user);
            return Results.Text(text, "text/plain");
        });

        // The fleet preamble pre-wrapped as ready-to-print SessionStart hook output. The
        // macOS/Linux shell hook cannot safely BUILD JSON (escaping arbitrary preamble text in
        // POSIX shell), so the Director serializes the whole hookSpecificOutput envelope and the
        // script just prints this response body to stdout. Empty body when there is no preamble,
        // so the hook emits nothing rather than an empty envelope.
        app.MapGet("/sessions/{sid}/fleet-preamble-hook-output", (string sid) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var name = SessionName.DisplayName(session.CustomName,
                SessionName.FolderName(session.RepoPath),
                SessionName.Disambiguator(session.Id));

            var text = FleetPreamble.Build(session.Id.ToString(), name, Environment.MachineName, session.RepoPath);
            if (string.IsNullOrWhiteSpace(text))
                return Results.Text("", "text/plain");

            return Results.Json(new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "SessionStart",
                    additionalContext = text,
                },
            });
        });

        // ===== REST: Fleet messaging (issue #705) =====
        // A session can only reach its OWN Director (CC_DIRECTOR_API); it never holds the Gateway
        // URL or the fleet token. These endpoints let a session list and message other sessions
        // across the fleet by relaying through this Director, which forwards to the Gateway using
        // the token it already holds. cc-devthrottle wraps these.

        // Resolve the sender's display name from THIS Director's own session record (never trusted
        // from the request body) and build the framed message the recipient sees.
        string FrameForSender(string? fromSessionId, string text, bool includeReplyHint = true)
        {
            string? fromName = null;
            if (!string.IsNullOrWhiteSpace(fromSessionId) && Guid.TryParse(fromSessionId, out var fromGuid))
            {
                var sender = sessionManager.GetSession(fromGuid);
                if (sender is not null)
                    // Issue #800: route the sender's display name through the single composer.
                    fromName = SessionName.DisplayName(sender.CustomName,
                        SessionName.FolderName(sender.RepoPath),
                        SessionName.Disambiguator(sender.Id));
            }
            return FleetMessaging.BuildFramedMessage(fromSessionId, fromName, Environment.MachineName, text, includeReplyHint);
        }

        // GET /fleet/sessions - the fleet directory. With a Gateway, relay its aggregated list;
        // standalone, serve this Director's own sessions (the no-Gateway acceptance criterion).
        app.MapGet("/fleet/sessions", async (CancellationToken ct) =>
        {
            var gw = gatewayClientProvider?.Invoke();
            if (gw is { IsEnabled: true })
            {
                try
                {
                    var fleet = await gw.ListFleetSessionsAsync(ct);
                    return Results.Json(fleet);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[ControlEndpoints] /fleet/sessions relay FAILED: {ex.Message}");
                    return Results.Json(new { error = $"Cannot reach the Gateway: {ex.Message}" },
                        statusCode: StatusCodes.Status502BadGateway);
                }
            }

            var local = sessionManager.ListSessions()
                .Where(s => s.ActivityState != ActivityState.Exited)
                .Select(s => MapWithIdentity(s, turnSummaryCache))
                .ToList();
            return Results.Json(local);
        });

        // POST /fleet/send - deliver one message. A local target is delivered directly (works with
        // or without a Gateway); a remote target is relayed through the Gateway. An unknown target
        // with no Gateway is a clear error (no silent drop, no fallback).
        app.MapPost("/fleet/send", async (FleetSendRequest req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.ToSessionId))
                return Results.BadRequest(new { error = "toSessionId is required" });
            if (string.IsNullOrWhiteSpace(req.Text))
                return Results.BadRequest(new { error = "text is required" });
            if (!Guid.TryParse(req.ToSessionId, out var toGuid))
                return Results.BadRequest(new { error = "invalid toSessionId format" });

            // Fleet-message steward (messaging.steward): dedupe + per-source rate limit on this session's
            // OUTGOING messages. Never silent - a drop is logged AND returned to the sender. Disabled or
            // not wired => Allow (byte-identical).
            if (messageSteward is not null)
            {
                var decision = messageSteward.CheckMessage(req.FromSessionId, req.ToSessionId, req.Text);
                if (!decision.Allowed)
                {
                    FileLog.Write($"[ControlEndpoints] /fleet/send steward {decision.Outcome}: from={FleetMessaging.ShortId(req.FromSessionId)} to={FleetMessaging.ShortId(req.ToSessionId)} - {decision.Reason}");
                    return Results.Json(new FleetSendResponse { Accepted = false, DeliveredCount = 0, Error = decision.Reason },
                        statusCode: decision.Outcome == StewardOutcome.DuplicateSuppressed ? StatusCodes.Status200OK : StatusCodes.Status429TooManyRequests);
                }
            }

            var framed = string.IsNullOrWhiteSpace(req.FromSessionId)
                ? req.Text
                : FrameForSender(req.FromSessionId, req.Text);

            var local = sessionManager.GetSession(toGuid);
            if (local is not null)
            {
                try
                {
                    // Fleet message delivery is framework-mediated, not a human racing the dictation, so it
                    // is exempt from the dictation lock (issue #1181, Task 3b).
                    await local.SendTextAsync(framed, SendSource.Internal);
                    return Results.Json(new FleetSendResponse { Accepted = true, DeliveredCount = 1 });
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[ControlEndpoints] /fleet/send local deliver to {toGuid} FAILED: {ex.Message}");
                    return Results.Json(new FleetSendResponse { Accepted = false, Error = ex.Message },
                        statusCode: StatusCodes.Status500InternalServerError);
                }
            }

            var gw = gatewayClientProvider?.Invoke();
            if (gw is not { IsEnabled: true })
                return Results.Json(new FleetSendResponse
                {
                    Accepted = false,
                    Error = "Session not found on this Director and no Gateway is configured.",
                }, statusCode: StatusCodes.Status404NotFound);

            try
            {
                var resp = await gw.SendPromptToFleetAsync(req.ToSessionId, framed, ct);
                return Results.Json(new FleetSendResponse
                {
                    Accepted = resp.Accepted,
                    DeliveredCount = resp.Accepted ? 1 : 0,
                    Error = resp.Error,
                });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] /fleet/send relay to {toGuid} FAILED: {ex.Message}");
                return Results.Json(new FleetSendResponse
                {
                    Accepted = false,
                    Error = $"Cannot reach the target via the Gateway: {ex.Message}",
                }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // POST /fleet/broadcast - send to every other session in the fleet. With a Gateway, fan out
        // via the Gateway; standalone, send to this Director's own sessions (except the sender).
        app.MapPost("/fleet/broadcast", async (FleetBroadcastRequest req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.BadRequest(new { error = "text is required" });

            // Fleet-message steward (messaging.steward): dedupe + per-source BROADCAST throttle (a broadcast
            // fans out to the whole fleet, so it is capped tighter). Never silent. Disabled/unwired => Allow.
            if (messageSteward is not null)
            {
                var decision = messageSteward.CheckBroadcast(req.FromSessionId, req.Text);
                if (!decision.Allowed)
                {
                    FileLog.Write($"[ControlEndpoints] /fleet/broadcast steward {decision.Outcome}: from={FleetMessaging.ShortId(req.FromSessionId)} - {decision.Reason}");
                    return Results.Json(new FleetSendResponse { Accepted = false, DeliveredCount = 0, Error = decision.Reason },
                        statusCode: decision.Outcome == StewardOutcome.DuplicateSuppressed ? StatusCodes.Status200OK : StatusCodes.Status429TooManyRequests);
                }
            }

            var framed = FrameForSender(req.FromSessionId, req.Text);

            var gw = gatewayClientProvider?.Invoke();
            if (gw is { IsEnabled: true })
            {
                try
                {
                    var fleet = await gw.ListFleetSessionsAsync(ct);

                    // Issue #1229: by default a broadcast reaches only the sender's own team - the sessions
                    // sharing its group, or (for a solo session) the sessions in the same repository on the
                    // same machine. Only an explicit fleet-wide request (Everyone) targets every session,
                    // and the Gateway Hub then gates that on a human-issued grant. Narrowing here is a
                    // convenience; the Gateway enforces the same rule as the authority.
                    var senderDto = fleet.FirstOrDefault(s =>
                        string.Equals(s.SessionId, req.FromSessionId, StringComparison.OrdinalIgnoreCase));
                    BroadcastScope? senderScope = senderDto is not null
                        ? BroadcastScope.FromAggregatedSession(senderDto)
                        : null;

                    var targets = fleet
                        .Where(s => !string.IsNullOrWhiteSpace(s.SessionId)
                            && !string.Equals(s.SessionId, req.FromSessionId, StringComparison.OrdinalIgnoreCase))
                        .Where(s => req.Everyone
                            || (senderScope?.Includes(BroadcastScope.FromAggregatedSession(s)) ?? false))
                        .Select(s => s.SessionId)
                        .ToList();

                    if (targets.Count == 0)
                        return Results.Json(new FleetSendResponse
                        {
                            Accepted = true,
                            DeliveredCount = 0,
                            Warning = req.Everyone ? null : "No other sessions are on your team.",
                        });

                    var resp = await gw.FanoutToFleetAsync(
                        targets, framed, req.FromSessionId,
                        req.Everyone ? req.Reason : null,
                        req.Everyone ? req.GrantId : null,
                        ct);

                    // The Hub refused on scope grounds (fleet-wide without a grant, or over the rate limit).
                    if (resp.Denied)
                    {
                        FileLog.Write($"[ControlEndpoints] /fleet/broadcast DENIED by Hub: {resp.DeniedReason}");
                        return Results.Json(new FleetSendResponse { Accepted = false, DeliveredCount = 0, Error = resp.DeniedReason },
                            statusCode: StatusCodes.Status403Forbidden);
                    }

                    var delivered = resp.Results.Count(r => r.Error is null);
                    return Results.Json(new FleetSendResponse { Accepted = true, DeliveredCount = delivered });
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[ControlEndpoints] /fleet/broadcast relay FAILED: {ex.Message}");
                    return Results.Json(new FleetSendResponse
                    {
                        Accepted = false,
                        Error = $"Cannot reach the Gateway: {ex.Message}",
                    }, statusCode: StatusCodes.Status502BadGateway);
                }
            }

            // Standalone (no Gateway): the "fleet" is only this Director's own sessions, so there is no
            // cross-repo/cross-machine storm to guard and no Hub to mint grants. Still honor the team
            // default (issue #1229): a plain broadcast reaches the sender's team; Everyone reaches all
            // local sessions.
            var senderLocal = Guid.TryParse(req.FromSessionId, out var senderGuid)
                ? sessionManager.GetSession(senderGuid)
                : null;
            BroadcastScope? senderLocalScope = senderLocal is not null
                ? new BroadcastScope(senderLocal.MissionId?.ToString(), senderLocal.GroupId?.ToString(), senderLocal.RepoPath, Environment.MachineName)
                : null;

            var locals = sessionManager.ListSessions()
                .Where(s => s.ActivityState != ActivityState.Exited)
                .Where(s => !string.Equals(s.Id.ToString(), req.FromSessionId, StringComparison.OrdinalIgnoreCase))
                .Where(s => req.Everyone
                    || (senderLocalScope?.Includes(new BroadcastScope(s.MissionId?.ToString(), s.GroupId?.ToString(), s.RepoPath, Environment.MachineName)) ?? false))
                .ToList();
            var count = 0;
            foreach (var s in locals)
            {
                // Best effort per target: one failing session must not abort the rest of a broadcast.
                try
                {
                    await s.SendTextAsync(framed, SendSource.Internal);
                    count++;
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[ControlEndpoints] /fleet/broadcast local deliver to {s.Id} FAILED: {ex.Message}");
                }
            }
            return Results.Json(new FleetSendResponse { Accepted = true, DeliveredCount = count });
        });

        // Capture a LOCAL target's answer when there is no Gateway to do the wait for us (issue #717):
        // record the target's buffer cursor, deliver the framed question, wait for it to return to
        // Idle (or time out), then read the cleaned output produced since the cursor as the answer.
        async Task<(string answer, string status)> AskLocalAsync(Session target, string framed, int timeoutMs, CancellationToken ct)
        {
            var cursor = target.Buffer?.TotalBytesWritten ?? 0;

            // For transcript-capable agents, remember how many assistant messages existed BEFORE the
            // question, so afterwards we wait for a genuinely NEW one rather than returning a stale
            // prior answer.
            var supportsTranscript = CcDirector.Core.History.SessionHistoryReader.IsSupported(target);
            var preAssistantCount = supportsTranscript
                ? CcDirector.Core.History.SessionHistoryReader.Read(target).Messages
                    .Count(m => m.Role == CcDirector.Core.History.ConversationRole.Assistant)
                : 0;

            await target.SendTextAsync(framed, SendSource.Internal);

            // Give the target a moment to leave Idle, then wait for it to settle back to Idle.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(300, ct);
            while (sw.ElapsedMilliseconds < timeoutMs && target.ActivityState != ActivityState.Idle)
                await Task.Delay(250, ct);

            var timedOut = target.ActivityState != ActivityState.Idle;
            var answer = "";

            // Prefer the transcript: a clean, parsed answer (the NEW assistant message) instead of a
            // scrape of the repainting TUI buffer. Crisp for Claude, Codex and Pi, and the clean text
            // carries no TUI glyphs that could crash the message ask client print on a legacy Windows
            // console. A transcript can flush several seconds after Idle (Pi is the slowest), so poll
            // for an assistant message BEYOND preAssistantCount for up to ~12 s; fall back to the
            // buffer scrape only if no new answer appears (e.g. a turn that produced only tool calls).
            if (supportsTranscript)
            {
                // Up to ~25 s. The loop exits the instant a new answer appears, so Claude/Codex return
                // immediately; only Pi, whose session file flushes a few seconds after it goes idle,
                // uses the longer tail. This stays well inside the ask's overall timeout.
                for (var r = 0; r < 50 && answer.Length == 0; r++)
                {
                    var assistants = CcDirector.Core.History.SessionHistoryReader.Read(target).Messages
                        .Where(m => m.Role == CcDirector.Core.History.ConversationRole.Assistant)
                        .ToList();
                    if (assistants.Count > preAssistantCount)
                        answer = string.Join("\n", assistants[^1].Parts
                            .Where(p => p.Kind == CcDirector.Core.History.ConversationPartKind.Text)
                            .Select(p => p.Text)).Trim();
                    if (answer.Length == 0)
                        await Task.Delay(500, ct);
                }
            }

            if (answer.Length == 0 && target.Buffer is not null)
            {
                var (data, _) = target.Buffer.GetWrittenSince(cursor);
                answer = AnsiCleaner.Clean(data);
            }

            return (answer, timedOut ? "timeout" : "idle");
        }

        // POST /fleet/ask - ask one session a question and return its answer. With a Gateway, relay
        // to the Gateway's prompt-with-wait (uniform for local and remote targets); standalone, only
        // a local target can be asked. A timeout returns 504; unreachable/unknown returns a clear error.
        app.MapPost("/fleet/ask", async (FleetAskRequest req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.ToSessionId))
                return Results.BadRequest(new { error = "toSessionId is required" });
            if (string.IsNullOrWhiteSpace(req.Question))
                return Results.BadRequest(new { error = "question is required" });
            if (!Guid.TryParse(req.ToSessionId, out var toGuid))
                return Results.BadRequest(new { error = "invalid toSessionId format" });

            var timeoutMs = req.TimeoutMs > 0 ? req.TimeoutMs : 120_000;

            // Fleet-message steward (messaging.steward): dedupe + per-source rate limit on this session's
            // outgoing asks too. Never silent. Disabled/unwired => Allow (byte-identical).
            if (messageSteward is not null)
            {
                var decision = messageSteward.CheckMessage(req.FromSessionId, req.ToSessionId, req.Question);
                if (!decision.Allowed)
                {
                    FileLog.Write($"[ControlEndpoints] /fleet/ask steward {decision.Outcome}: from={FleetMessaging.ShortId(req.FromSessionId)} to={FleetMessaging.ShortId(req.ToSessionId)} - {decision.Reason}");
                    return Results.Json(new FleetAskResponse
                    {
                        Answered = false,
                        Status = decision.Outcome == StewardOutcome.DuplicateSuppressed ? "duplicate" : "throttled",
                        Error = decision.Reason,
                    }, statusCode: decision.Outcome == StewardOutcome.DuplicateSuppressed ? StatusCodes.Status200OK : StatusCodes.Status429TooManyRequests);
                }
            }

            // No reply hint for an ask: the asker is waiting and reads the answer from the target's
            // output, so the target must answer directly rather than try to send a separate reply.
            var framed = FrameForSender(req.FromSessionId, req.Question, includeReplyHint: false);

            var gw = gatewayClientProvider?.Invoke();
            if (gw is { IsEnabled: true })
            {
                try
                {
                    var resp = await gw.AskFleetAsync(req.ToSessionId, framed, timeoutMs, ct);
                    if (!resp.Accepted)
                        return Results.Json(new FleetAskResponse
                        {
                            Answered = false, Status = "failed",
                            Error = resp.Error ?? "The target rejected the question.",
                        }, statusCode: StatusCodes.Status502BadGateway);
                    if (string.Equals(resp.WaitStatus, "timeout", StringComparison.OrdinalIgnoreCase))
                        return Results.Json(new FleetAskResponse
                        {
                            Answered = false, Status = "timeout",
                            Error = $"No answer from {req.ToSessionId} within {timeoutMs} ms.",
                        }, statusCode: StatusCodes.Status504GatewayTimeout);
                    return Results.Json(new FleetAskResponse
                    {
                        Answered = true,
                        Status = resp.WaitStatus ?? "idle",
                        Answer = resp.Output ?? "",
                    });
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[ControlEndpoints] /fleet/ask relay to {toGuid} FAILED: {ex.Message}");
                    return Results.Json(new FleetAskResponse
                    {
                        Answered = false, Status = "failed",
                        Error = $"Cannot reach the target via the Gateway: {ex.Message}",
                    }, statusCode: StatusCodes.Status502BadGateway);
                }
            }

            // Standalone: only a local target can be asked (no Gateway to reach a remote one).
            var local = sessionManager.GetSession(toGuid);
            if (local is null)
                return Results.Json(new FleetAskResponse
                {
                    Answered = false, Status = "not_found",
                    Error = "Session not found on this Director and no Gateway is configured.",
                }, statusCode: StatusCodes.Status404NotFound);

            try
            {
                var (answer, status) = await AskLocalAsync(local, framed, timeoutMs, ct);
                if (status == "timeout")
                    return Results.Json(new FleetAskResponse
                    {
                        Answered = false, Status = "timeout",
                        Error = $"No answer from {req.ToSessionId} within {timeoutMs} ms.",
                    }, statusCode: StatusCodes.Status504GatewayTimeout);
                return Results.Json(new FleetAskResponse { Answered = true, Status = status, Answer = answer });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] /fleet/ask local to {toGuid} FAILED: {ex.Message}");
                return Results.Json(new FleetAskResponse
                {
                    Answered = false, Status = "failed", Error = ex.Message,
                }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // POST /fleet/spawn - "start a session on another computer". The body is a NewSessionRequest whose
        // Machine field selects the target: empty / "local" / this Director's own machine name spawns
        // LOCALLY (unchanged local behavior); any other machine name routes the spawn through the Gateway to
        // a Director on that machine (first available, auto-launched if none is running). A remote spawn
        // FAILS LOUD when no Gateway is configured or the machine is off / unreachable - it NEVER falls back
        // to a local spawn.
        app.MapPost("/fleet/spawn", async (NewSessionRequest req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.RepoPath))
                return Results.BadRequest(new { error = "repoPath is required" });

            var machine = req.Machine?.Trim();
            var isLocal = string.IsNullOrEmpty(machine)
                || string.Equals(machine, "local", StringComparison.OrdinalIgnoreCase)
                || string.Equals(machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

            if (isLocal)
            {
                FileLog.Write($"[ControlEndpoints] POST /fleet/spawn: LOCAL, repo={req.RepoPath}, agent={req.Agent}");
                return await CreateLocalSessionAsync(req);
            }

            FileLog.Write($"[ControlEndpoints] POST /fleet/spawn: machine={machine}, repo={req.RepoPath}, agent={req.Agent}");
            var gw = gatewayClientProvider?.Invoke();
            if (gw is not { IsEnabled: true })
                return Results.Json(
                    new { error = $"Cannot start a session on '{machine}': no Gateway is configured on this Director." },
                    statusCode: StatusCodes.Status502BadGateway);

            try
            {
                var dto = await gw.SpawnOnMachineAsync(machine!, req, ct);
                return Results.Json(dto, statusCode: StatusCodes.Status201Created);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] POST /fleet/spawn relay to {machine} FAILED: {ex.Message}");
                return Results.Json(
                    new { error = $"Cannot start a session on '{machine}': {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Ask the wingman about this session. Two behaviors, both on the strong model:
        //   mode=explain -> terse "what's happening" briefing over pre-built context.
        //   free-text question -> the "Ask the Wingman" channel: a read-only full-power
        //   session (Read/Grep/Glob) over the whole terminal + repo that answers
        //   faithfully and reads content VERBATIM when asked, never summarizing.
        // Gateway Cleanup Phase 0 (wave 3): the ask runs through the shared SessionWriteExecutor core so this
        // REST path and the Gateway stream down-channel are identical and cannot drift. The turn-summary cache
        // (explain mode's context input) rides in the services. The wingman's own "bad_request" outcome comes
        // back as a 200 result carrying Status="bad_request"; this route maps that Status to its original 400,
        // exactly as the execute-action route maps its executor outcomes. Phase 1 deletes this route.
        app.MapPost("/sessions/{sid}/wingman/ask", async (string sid, WingmanAskRequest req, CancellationToken ct) =>
        {
            var command = new DirectorCommand
            {
                Verb = "wingman-ask",
                SessionId = sid,
                PayloadJson = req is null ? "" : SessionCommandExecutor.Serialize(req),
            };
            var services = new SessionCommandServices { TurnSummaryCache = turnSummaryCache };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, services, cancellationToken: ct);

            switch (result.Status)
            {
                case DirectorCommandStatus.Ok:
                {
                    var ask = SessionCommandExecutor.Deserialize<WingmanAskResult>(result.BodyJson);
                    // The question-required guard is the wingman's bad_request outcome, returned as a 400 with
                    // the result body - identical to the pre-lift lambda. Every other outcome is a 200.
                    return ask?.Status == "bad_request"
                        ? Results.BadRequest(ask)
                        : Results.Json(ask);
                }
                case DirectorCommandStatus.BadRequest:
                    return Results.BadRequest(new { error = result.Error });
                case DirectorCommandStatus.NotFound:
                    return Results.NotFound(new { error = result.Error });
                default:
                    return Results.Problem(result.Error ?? "wingman-ask command failed");
            }
        });

        // Structured-intent actuation (Path A): the Wingman looks at the session's live
        // screen + state, decides ONE action (type / send_keys / submit / none), and the
        // Director executes it. The decision runs on a tool-less strong-model side-call; the
        // model never gets a write tool - WingmanActionExecutor is the only thing that writes
        // to the PTY. Pass ?decideOnly=true to get the decision WITHOUT executing it (dry run).
        app.MapPost("/sessions/{sid}/wingman/act", async (string sid, bool? decideOnly, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new WingmanActResult { Status = WingmanActResult.StatusBadRequest, Error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            if (string.IsNullOrWhiteSpace(sessionManager.Options.ClaudePath))
                return Results.Json(new WingmanActResult { Status = WingmanActResult.StatusNoClaude, Error = "no claude CLI configured" });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var context = await WingmanContextBuilder.BuildAsync(session, turnSummaryCache, ct);
                var action = await Core.Wingman.WingmanService.DecideSessionActionAsync(
                    context, sessionManager.Options.ClaudePath, ct);
                sw.Stop();

                WingmanActResult result;
                if (decideOnly == true)
                {
                    result = new WingmanActResult { Action = action.Action, Text = action.Text, Reason = action.Reason };
                    result.Keys.AddRange(action.Keys);
                }
                else
                {
                    result = Core.Wingman.WingmanActionExecutor.Execute(session, action);
                }
                result.Model = Core.Wingman.WingmanService.Model;
                result.LatencyMs = sw.ElapsedMilliseconds;
                FileLog.Write($"[ControlEndpoints] POST /wingman/act: session={guid} decideOnly={decideOnly == true} action={result.Action} performed={result.Performed} status={result.Status}");
                return Results.Json(result);
            }
            catch (Exception ex)
            {
                sw.Stop();
                FileLog.Write($"[ControlEndpoints] POST /wingman/act FAILED: session={guid}: {ex.Message}");
                return Results.Json(new WingmanActResult
                {
                    Status = WingmanActResult.StatusWingmanFailed,
                    Error = ex.Message,
                    Model = Core.Wingman.WingmanService.Model,
                    LatencyMs = sw.ElapsedMilliseconds,
                });
            }
        });

        // Mobile experience: the proactively-cached wingman briefing for this session.
        // Returns instantly (no LLM call) so a phone shows it the moment the view opens.
        // text is null when nothing has been cached yet (mobile mode off, or first turn
        // not finished). The phone falls back to the on-demand /wingman/ask in that case.
        // Gateway Cleanup Phase 0: the read runs through the shared SessionReadExecutor core so this REST
        // path and the Gateway stream down-channel are identical and cannot drift. Phase 1 deletes this route.
        app.MapGet("/sessions/{sid}/wingman/explain", async (string sid) =>
        {
            var command = new DirectorCommand { Verb = "wingman-explain", SessionId = sid };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<WingmanExplainResponse>(result.BodyJson)),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "wingman-explain command failed"),
            };
        });

        // Toggle mobile mode for a session. When turned on, kick off an immediate background
        // briefing so the cache is warm right away instead of waiting for the next turn-end.
        // Gateway Cleanup Phase 0 (Worker W1): the mobile-mode toggle (and its briefing-cache warm side
        // effect) run through the shared SessionWriteExecutor core so this REST path and the Gateway stream
        // down-channel are identical. The optional body is read at this HTTP boundary exactly as before
        // (empty body -> default enable) and handed to the core as the command payload.
        app.MapPost("/sessions/{sid}/mobile-mode", async (string sid, HttpContext httpCtx) =>
        {
            var enabled = true;
            try
            {
                var body = await httpCtx.Request.ReadFromJsonAsync<MobileModeRequest>();
                if (body is not null) enabled = body.Enabled;
            }
            catch { /* empty body -> default enable */ }

            var command = new DirectorCommand
            {
                Verb = "mobile-mode",
                SessionId = sid,
                PayloadJson = SessionCommandExecutor.Serialize(new MobileModeRequest(enabled)),
            };
            var services = new SessionCommandServices { ProactiveExplain = proactiveExplain, TurnSummaryCache = turnSummaryCache };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, services);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Content(result.BodyJson ?? "", "application/json"),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "mobile-mode command failed"),
            };
        });

        // Toggle voice (in-car) mode for a session. The mobile Voice tab calls this on tab switch:
        // enabled -> Voice (the wingman will write spoken-friendly remarks); disabled -> Text (the
        // user left the Voice tab but the phone is still on the mobile app). Like /mobile-mode this
        // warms the briefing cache immediately so the phone has something to speak right away.
        // Gateway Cleanup Phase 0 (Worker W1): the voice-mode toggle (and its unconditional briefing-cache
        // warm) run through the shared SessionWriteExecutor core. The optional body is read here at the HTTP
        // boundary exactly as before (empty body -> default enable).
        app.MapPost("/sessions/{sid}/voice-mode", async (string sid, HttpContext httpCtx) =>
        {
            var enabled = true;
            try
            {
                var body = await httpCtx.Request.ReadFromJsonAsync<VoiceModeRequest>();
                if (body is not null) enabled = body.Enabled;
            }
            catch { /* empty body -> default enable */ }

            var command = new DirectorCommand
            {
                Verb = "voice-mode",
                SessionId = sid,
                PayloadJson = SessionCommandExecutor.Serialize(new VoiceModeRequest(enabled)),
            };
            var services = new SessionCommandServices { ProactiveExplain = proactiveExplain, TurnSummaryCache = turnSummaryCache };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, services);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Content(result.BodyJson ?? "", "application/json"),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "voice-mode command failed"),
            };
        });

        // Park / un-park a session in the FIFO voice queue. The phone's FIFO mode calls this
        // when the user says "put this on hold": held sessions stay reported with their true
        // state and color, but the FIFO conductor skips them until they are taken off hold.
        // Empty body defaults to onHold=true (the common case is "hold this one").
        app.MapPost("/sessions/{sid}/hold", async (string sid, HttpContext httpCtx) =>
        {
            // Read the (optional) body at the boundary, exactly as before: an empty body defaults to hold.
            var onHold = true;
            try
            {
                var body = await httpCtx.Request.ReadFromJsonAsync<HoldRequest>();
                if (body is not null) onHold = body.OnHold;
            }
            catch { /* empty body -> default to hold */ }

            // Issue #1177 (Phase 1): the hold state change runs through the shared SessionCommandExecutor
            // so this REST path and the Gateway stream down-channel are identical.
            var command = new DirectorCommand
            {
                Verb = "hold",
                SessionId = sid,
                PayloadJson = SessionCommandExecutor.Serialize(new HoldRequest { OnHold = onHold }),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<HoldResponse>(result.BodyJson)),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "hold command failed"),
            };
        });

        // Toggle the Wingman experience for a session. Default ON for every session; users
        // turn it OFF on the per-session settings UI when they want a plain terminal with
        // no auto-explain and no Voice/Wingman tabs. When the toggle is flipped back ON we
        // kick off an immediate background briefing so the cache is warm right away. When
        // flipped OFF mid-flight we also clear IsExplaining so the dot doesn't stick on
        // Yellow waiting for the in-flight briefing to finish.
        // Empty body defaults to enabled=true (the common case is "turn it on").
        // Gateway Cleanup Phase 0 (Worker W1): the Wingman on/off toggle (with its cache-warm on / clear the
        // in-flight explaining flag off) runs through the shared SessionWriteExecutor core. The optional body
        // is read here at the HTTP boundary exactly as before (empty body -> default enable).
        app.MapPost("/sessions/{sid}/wingman-enabled", async (string sid, HttpContext httpCtx) =>
        {
            var enabled = true;
            try
            {
                var body = await httpCtx.Request.ReadFromJsonAsync<WingmanEnabledRequest>();
                if (body is not null) enabled = body.Enabled;
            }
            catch { /* empty body -> default enable */ }

            var command = new DirectorCommand
            {
                Verb = "wingman-enabled",
                SessionId = sid,
                PayloadJson = SessionCommandExecutor.Serialize(new WingmanEnabledRequest(enabled)),
            };
            var services = new SessionCommandServices { ProactiveExplain = proactiveExplain, TurnSummaryCache = turnSummaryCache };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, services);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Content(result.BodyJson ?? "", "application/json"),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "wingman-enabled command failed"),
            };
        });

        // Resolve the session repo's GitHub "new issue" URL from its origin remote. The
        // Cockpit's session menu (#191) calls this because the repo lives on THIS Director's
        // machine - the browser cannot read the git config itself. 409 when the repo has no
        // GitHub origin (the menu shows the message verbatim).
        // Gateway Cleanup Phase 0: the read runs through the shared SessionReadExecutor core so this REST
        // path and the Gateway stream down-channel are identical and cannot drift. A repo with no GitHub
        // origin is a Conflict, mapped back to the route's 409. Phase 1 deletes this route.
        app.MapGet("/sessions/{sid}/github-urls", async (string sid) =>
        {
            var command = new DirectorCommand { Verb = "github-urls", SessionId = sid };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<GithubUrlsResponse>(result.BodyJson)),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                DirectorCommandStatus.Conflict => Results.Json(new { error = result.Error }, statusCode: 409),
                _ => Results.Problem(result.Error ?? "github-urls command failed"),
            };
        });

        // Mobile view-links: serve a local file INLINE so a phone can tap a link and VIEW
        // the file (HTML/PDF/image/text) in the browser, instead of getting a useless file
        // path it cannot open. Browser Back returns to the session.
        //
        // Security: per the solo-tailnet decision (see remote-experience-plan.md) there is
        // NO sandbox/allowed-roots restriction - the tailnet boundary is the only gate, and
        // the tailnet is the owner's own devices. Revisit (add auth/signed links) the moment
        // a non-owner device or second user joins the tailnet.
        app.MapGet("/file", (string? path) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path is required" });
            if (!System.IO.File.Exists(path))
                return Results.NotFound(new { error = "file not found: " + path });

            var ctype = FileContentType(path);
            FileLog.Write($"[ControlEndpoints] GET /file: {path} ({ctype})");
            // No fileDownloadName -> served inline, so the browser renders it (not a download).
            return Results.File(path, ctype);
        });

        // Local Files mission (Phase 1): the session-scoped sibling of the top-level /file above.
        // The client viewing a session always has the session id, so it asks the Gateway for
        // GET /sessions/{sid}/file?path=... ; the Gateway's per-session catch-all
        // (/sessions/{sid}/{**rest}) uses the sid ONLY to resolve the owning Director, then forwards
        // the request here unchanged. Because the session lives on this machine, any absolute path
        // its terminal or chat emitted is a path on this machine - correct by construction. The sid
        // is the routing vehicle, NOT a sandbox: like /file above, any existing absolute path is
        // served (per the solo-tailnet decision), 404 if it does not exist. Two things this route
        // adds over the top-level /file: enableRangeProcessing (large PDFs/images seek and resume)
        // and X-Content-Type-Options: nosniff (the browser must honor the content type we set, so an
        // HTML file cannot be re-sniffed into something that executes with different authority).
        app.MapGet("/sessions/{sid}/file", (HttpContext ctx, string sid, string? path) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path is required" });
            if (!System.IO.File.Exists(path))
                return Results.NotFound(new { error = "file not found: " + path });

            var ctype = FileContentType(path);
            FileLog.Write($"[ControlEndpoints] GET /sessions/{sid}/file: {path} ({ctype})");
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            // No fileDownloadName -> served inline, so the browser renders it (not a download).
            return Results.File(path, ctype, enableRangeProcessing: true);
        });

        // Phase 4b: observability into the wingman. Returns current color + reason,
        // a timestamped log of recent decisions, and the latest TurnSummary if any.
        // Gateway Cleanup Phase 0: the read runs through the shared SessionReadExecutor core (given the
        // Director-local TurnSummaryCache via the command services) so this REST path and the Gateway stream
        // down-channel are identical and cannot drift. Phase 1 deletes this route.
        app.MapGet("/sessions/{sid}/wingman", async (string sid) =>
        {
            var command = new DirectorCommand { Verb = "wingman-view", SessionId = sid };
            var services = new SessionCommandServices { TurnSummaryCache = turnSummaryCache };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, services);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<WingmanViewDto>(result.BodyJson)),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "wingman-view command failed"),
            };
        });

        // Goal management: set (or clear) the session's stated goal. Setting a goal
        // kicks off an immediate background assessment so the verdict is warm. Pass an
        // empty/null goal to clear it and stop goal-tracking.
        app.MapPost("/sessions/{sid}/wingman/goal", async (string sid, WingmanGoalRequest req) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            // Issue #1177 (Phase 1, increment 6): the goal set + its cache-warm side effect run through the
            // shared SessionCommandExecutor (given the Director-local services), so this REST path and the
            // Gateway stream down-channel are identical. The local response is re-read here, byte-identical.
            var command = new DirectorCommand
            {
                Verb = "wingman-goal",
                SessionId = sid,
                PayloadJson = req is null ? "" : SessionCommandExecutor.Serialize(req),
            };
            var services = new SessionCommandServices { ProactiveExplain = proactiveExplain, TurnSummaryCache = turnSummaryCache };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, services);

            if (result.Status == DirectorCommandStatus.BadRequest)
                return Results.BadRequest(new { error = result.Error });
            if (result.Status != DirectorCommandStatus.Ok)
                return Results.NotFound(new { error = result.Error });

            var session = sessionManager.GetSession(guid);
            return session is null
                ? Results.NotFound(new { error = "session not found" })
                : Results.Json(new
                {
                    goal = session.WingmanGoal,
                    goalSetAt = session.WingmanGoalSetAt,
                    goalState = session.WingmanGoalState,
                });
        });

        // Automatic session roles (chunk 2.5): (re)declare this session's sticky explicit role. Runs through
        // the shared SessionCommandExecutor so this REST path and the Gateway stream down-channel are
        // identical. Returns the updated session DTO; a blank role clears the explicit role.
        app.MapPost("/sessions/{sid}/role", async (string sid, SetRoleRequest req) =>
        {
            if (!Guid.TryParse(sid, out _))
                return Results.BadRequest(new { error = "invalid session id format" });

            var command = new DirectorCommand
            {
                Verb = "set-role",
                SessionId = sid,
                PayloadJson = req is null ? "" : SessionCommandExecutor.Serialize(req),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            if (result.Status == DirectorCommandStatus.BadRequest)
                return Results.BadRequest(new { error = result.Error });
            if (result.Status != DirectorCommandStatus.Ok)
                return Results.NotFound(new { error = result.Error });
            return Results.Content(result.BodyJson ?? "{}", "application/json");
        });

        // ===== Missions (mission-as-first-class-unit-of-work) =====
        // A Mission is its OWN persisted record that sessions attach to. These endpoints create and read
        // the records; POST /sessions/{sid}/mission attaches a session to one.

        // Create a Mission record and return it.
        app.MapPost("/missions", (NewMissionRequest req) =>
        {
            FileLog.Write($"[ControlEndpoints] POST /missions: name=\"{req?.MissionName}\"");

            if (missionStore is null)
                return Results.Problem("mission store not available", statusCode: 500);
            if (req is null || string.IsNullOrWhiteSpace(req.MissionName))
                return Results.BadRequest(new { error = "missionName is required" });

            var mission = missionStore.Create(req.MissionName, req.ParentMissionId);
            return Results.Json(ToMissionDto(mission), statusCode: 201);
        });

        // List every Mission record.
        app.MapGet("/missions", () =>
        {
            if (missionStore is null)
                return Results.Problem("mission store not available", statusCode: 500);
            return Results.Json(missionStore.List().Select(ToMissionDto).ToList());
        });

        // Read one Mission record by id.
        app.MapGet("/missions/{mid}", (string mid) =>
        {
            if (!Guid.TryParse(mid, out var missionId))
                return Results.BadRequest(new { error = "invalid mission id format" });
            if (missionStore is null)
                return Results.Problem("mission store not available", statusCode: 500);

            var mission = missionStore.Get(missionId);
            return mission is null
                ? Results.NotFound(new { error = "mission not found" })
                : Results.Json(ToMissionDto(mission));
        });

        // Attach a session to a Mission (or detach on a blank/absent missionId). Runs through the shared
        // SessionCommandExecutor so this REST path and the Gateway stream down-channel are identical, exactly
        // like POST /sessions/{sid}/role. Returns the updated session DTO.
        app.MapPost("/sessions/{sid}/mission", async (string sid, SetMissionRequest req) =>
        {
            if (!Guid.TryParse(sid, out _))
                return Results.BadRequest(new { error = "invalid session id format" });

            var command = new DirectorCommand
            {
                Verb = "attach-mission",
                SessionId = sid,
                PayloadJson = req is null ? "" : SessionCommandExecutor.Serialize(req),
            };
            var services = new SessionCommandServices { ProactiveExplain = proactiveExplain, TurnSummaryCache = turnSummaryCache, MissionStore = missionStore };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, services);

            if (result.Status == DirectorCommandStatus.BadRequest)
                return Results.BadRequest(new { error = result.Error });
            if (result.Status != DirectorCommandStatus.Ok)
                return Results.NotFound(new { error = result.Error });
            return Results.Content(result.BodyJson ?? "{}", "application/json");
        });

        app.MapPatch("/sessions/{sid}", async (string sid, SessionUpdateRequest req) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            FileLog.Write($"[ControlEndpoints] PATCH /sessions/{sid}: name=\"{req?.Name}\"");

            // Issue #1177 (Phase 1): the rename runs through the shared SessionCommandExecutor so this REST
            // path and the Gateway stream down-channel apply the identical mutation. The Director's local
            // response still uses its identity-stamped mapper (MachineName/User/TailnetEndpoint), so this
            // endpoint stays byte-identical to before; the executor returns the plain stream DTO that the
            // Gateway stamps during aggregation.
            var command = new DirectorCommand
            {
                Verb = "patch",
                SessionId = sid,
                PayloadJson = req is null ? "" : SessionCommandExecutor.Serialize(req),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            if (result.Status == DirectorCommandStatus.BadRequest)
                return Results.BadRequest(new { error = result.Error });
            if (result.Status != DirectorCommandStatus.Ok)
                return Results.NotFound(new { error = result.Error });

            var session = sessionManager.GetSession(guid);
            return session is null
                ? Results.NotFound(new { error = "session not found" })
                : Results.Json(MapWithIdentity(session, turnSummaryCache));
        });

        // Gateway Cleanup Phase 0: the read runs through the shared SessionReadExecutor core so this REST
        // path and the Gateway stream down-channel are identical and cannot drift. The route's query-string
        // arguments (lines/raw/since) ride in the command payload as a BufferRequest. Phase 1 deletes this route.
        app.MapGet("/sessions/{sid}/buffer", async (string sid, int? lines, bool? raw, long? since) =>
        {
            var command = new DirectorCommand
            {
                Verb = "buffer",
                SessionId = sid,
                PayloadJson = SessionCommandExecutor.Serialize(new BufferRequest { Lines = lines, Raw = raw, Since = since }),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<BufferResponse>(result.BodyJson)),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "buffer command failed"),
            };
        });

        // ===== HTML snapshot of the terminal grid =====
        // The Avalonia terminal renders cleanly because it pipes the raw PTY
        // bytes through a real xterm-compatible VT emulator. The HTML "Raw
        // terminal" tab needs the same treatment, otherwise CR-overwrites and
        // status-bar redraws stack as junk lines. We expose the per-session
        // parser snapshot here as styled HTML; the client just swaps innerHTML.
        // Gateway Cleanup Phase 0: the read runs through the shared SessionReadExecutor core so this REST
        // path and the Gateway stream down-channel are identical and cannot drift. Phase 1 deletes this route.
        app.MapGet("/sessions/{sid}/buffer/html", async (string sid) =>
        {
            var command = new DirectorCommand { Verb = "buffer-html", SessionId = sid };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<BufferHtmlResponse>(result.BodyJson)),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "buffer-html command failed"),
            };
        });

        // (The agent-agnostic conversation history endpoint, GET /sessions/{sid}/history, is already
        // provided by SessionHistoryEndpoint via SessionHistoryReader - it covers Claude, Codex, and
        // Pi. cc-history reads that existing endpoint; no duplicate is registered here.)

        // Issue #1177 / Gateway Cleanup Phase 0: the read runs through the shared SessionReadExecutor core so
        // this REST path and the Gateway stream down-channel are identical and cannot drift. Phase 1 deletes
        // this route and leaves the core reached only over the tunnel.
        app.MapGet("/sessions/{sid}/turns", async (string sid) =>
        {
            var command = new DirectorCommand { Verb = "turns", SessionId = sid };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<TurnsResponse>(result.BodyJson)),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "turns command failed"),
            };
        });

        // Gateway Cleanup Phase 0: the read runs through the shared SessionReadExecutor core so this REST
        // path and the Gateway stream down-channel are identical and cannot drift. Phase 1 deletes this route.
        app.MapGet("/sessions/{sid}/summary", async (string sid) =>
        {
            var command = new DirectorCommand { Verb = "summary", SessionId = sid };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<SessionSummaryDto>(result.BodyJson)),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "summary command failed"),
            };
        });

        // ===== REST: Handover info (issue #1214) =====
        // The small identity/locate block the desktop app's "Copy Handover Info" menu item shows for a
        // session (name, session id, repo, director id, machine, version). Deliberately does NOT include
        // this Director's Control API endpoint: the Gateway proxies this to a browser, and issue #1214
        // requires the browser to talk only to the Gateway and never learn a Director address. This is a
        // pure read of the live session record - no transcript parsing, no I/O.
        // Gateway Cleanup Phase 0 (wave 3): the read runs through the shared SessionReadExecutor core so this
        // REST path and the Gateway stream down-channel are identical and cannot drift. The Director version -
        // the one dependency the tunnel command surface did not carry - is passed in through the services and
        // stamped by the core. Phase 1 deletes this route and leaves the core reached only over the tunnel.
        app.MapGet("/sessions/{sid}/handover", async (string sid) =>
        {
            var command = new DirectorCommand { Verb = "handover", SessionId = sid };
            var services = new SessionCommandServices { DirectorVersion = version };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, services);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Content(result.BodyJson ?? "{}", "application/json"),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "handover command failed"),
            };
        });

        // ===== REST: Brief - the Cockpit's full-page session view (ASK / DID / NEEDS YOU) =====
        // Sourced from the Claude JSONL transcript, never the terminal screen. The DID bullets
        // and verbatim NEEDS-YOU extraction come from a cached OpenAI condensation (one call
        // per completed turn); the raw blocks are served even when the condenser is
        // unavailable - explicit degrade, never a blank page.
        // Plan: docs/plans/cockpit-brief-view.md
        app.MapGet("/sessions/{sid}/brief", async (string sid, HttpContext ctx) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var resp = new BriefResponse
            {
                SessionId = sid,
                ActivityState = session.ActivityState.ToString(),
                CreatedAt = session.CreatedAt.UtcDateTime,
            };

            if (string.IsNullOrEmpty(session.ClaudeSessionId))
            {
                resp.Status = "no_session_id";
                resp.Error = "Session has not been linked to a Claude session id yet.";
                return Results.Json(resp);
            }

            try
            {
                var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
                if (!File.Exists(jsonl))
                {
                    resp.Status = "no_jsonl";
                    resp.Error = $"JSONL file not found at {jsonl}";
                    return Results.Json(resp);
                }

                var messages = StreamMessageParser.ParseFile(jsonl);
                var widgets = WidgetBuilder.BuildFromMessages(messages);
                var extract = BriefBuilder.Extract(widgets);

                resp.TurnCount = extract.TurnCount;
                resp.Goal = TruncateForDisplay(extract.FirstUserPrompt, BriefBuilder.GoalMaxChars);
                resp.LastAsk = TruncateForDisplay(extract.LastUserPrompt, BriefBuilder.LastAskMaxChars);
                resp.FullReply = extract.LastAssistantText;
                resp.ReplyPending = extract.ReplyPending;

                // While the current turn's reply is missing from the transcript (mid-reply, or
                // blocked in an on-screen interactive prompt), do NOT condense or fall back:
                // the condensation would describe the PREVIOUS turn against the NEW ask, and a
                // fallback needs-you would quote the wrong reply. The client routes the user
                // to the Terminal tab instead.
                if (extract.LastAssistantText is not null && !extract.ReplyPending)
                {
                    var cached = BriefCache.TryGetCurrent(guid, extract.TurnCount);
                    if (cached is null)
                    {
                        using var condenser = BriefBuilder.TryCreate();
                        if (condenser is not null)
                        {
                            var c = await condenser.CondenseAsync(
                                extract.LastUserPrompt, extract.LastAssistantText, ctx.RequestAborted);
                            if (c is not null)
                            {
                                cached = new BriefCache.Entry
                                {
                                    AtTurnCount = extract.TurnCount,
                                    DidBullets = c.Bullets,
                                    NeedsYouVerbatim = c.NeedsYouVerbatim,
                                    Condenser = condenser.CondenserId,
                                    GeneratedAt = DateTime.UtcNow,
                                };
                                BriefCache.Set(guid, cached);
                            }
                        }
                    }

                    if (cached is not null)
                    {
                        resp.DidBullets = cached.DidBullets;
                        resp.Condenser = cached.Condenser;
                        resp.GeneratedAt = cached.GeneratedAt;
                        resp.NeedsYou = cached.NeedsYouVerbatim;
                        resp.NeedsYouSource = cached.NeedsYouVerbatim is null ? null : "model";
                    }

                    // Verbatim-by-construction fallback: the reply's final paragraph. Applied
                    // both when the condenser is unavailable AND when its extraction failed
                    // validation but the session is visibly waiting on the user.
                    if (resp.NeedsYou is null &&
                        session.ActivityState == ActivityState.WaitingForInput)
                    {
                        resp.NeedsYou = BriefBuilder.FallbackNeedsYou(extract.LastAssistantText);
                        resp.NeedsYouSource = resp.NeedsYou is null ? null : "fallback";
                    }
                }

                resp.Status = "ok";
                return Results.Json(resp);
            }
            catch (OperationCanceledException)
            {
                // Client navigated away mid-condensation. Normal.
                return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] /brief FAILED: {ex.Message}");
                resp.Status = "parse_error";
                resp.Error = ex.Message;
                return Results.Json(resp);
            }
        });

        app.MapGet("/sessions/{sid}/handover-context", (string sid, string? extraContext) =>
        {
            // Return the plain-text prompt that would be sent to a target session on
            // POST /handover. Useful for clients (skills, UI) that want to preview or
            // edit the context before dispatching.
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            SessionSummaryDto summary;
            if (string.IsNullOrEmpty(session.ClaudeSessionId))
            {
                summary = new SessionSummaryDto
                {
                    SessionId = sid, DirectorId = directorId,
                    Agent = session.AgentKind.ToString(),
                    RepoPath = session.RepoPath,
                    ActivityState = session.ActivityState.ToString(),
                    CreatedAt = session.CreatedAt.UtcDateTime,
                };
            }
            else
            {
                var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
                summary = File.Exists(jsonl)
                    ? SummaryBuilder.Build(StreamMessageParser.ParseFile(jsonl))
                    : new SessionSummaryDto();
                summary.SessionId = sid;
                summary.DirectorId = directorId;
                summary.Agent = session.AgentKind.ToString();
                summary.RepoPath = session.RepoPath;
                summary.ActivityState = session.ActivityState.ToString();
                summary.CreatedAt = session.CreatedAt.UtcDateTime;
            }

            var text = SummaryBuilder.FormatAsHandoverPrompt(summary, extraContext);
            return Results.Text(text, "text/plain; charset=utf-8");
        });

        // ===== REST: Recap (cheap claude --print side-call, cached) =====
        // Two endpoints: GET returns whatever is in the cache (or status=not_cached),
        // POST regenerates and writes to cache. We never start a generation on GET
        // because GET should always be cheap and never trigger an API spend.
        // Gateway Cleanup Phase 0: the read runs through the shared SessionReadExecutor core so this REST
        // path and the Gateway stream down-channel are identical and cannot drift. Phase 1 deletes this route.
        app.MapGet("/sessions/{sid}/recap", async (string sid) =>
        {
            var command = new DirectorCommand { Verb = "recap", SessionId = sid };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<RecapResponse>(result.BodyJson)),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "recap command failed"),
            };
        });

        // Gateway Cleanup Phase 0 (wave 3): the recap generation runs through the shared SessionWriteExecutor
        // core so this REST path and the Gateway stream down-channel are identical and cannot drift. The
        // optional ?model= query rides in the command payload. The domain-state bodies (no_session_id,
        // no_jsonl, generation_failed) come back as Ok results the route serves as 200; a successful generation
        // (Status="ok") is served as the route's original 201. A caller cancellation bubbles out of the core
        // and is mapped to the route's 499 here. Phase 1 deletes this route.
        app.MapPost("/sessions/{sid}/recap", async (string sid, HttpContext ctx) =>
        {
            FileLog.Write($"[ControlEndpoints] POST /sessions/{sid}/recap");
            var model = ctx.Request.Query["model"].ToString();
            var command = new DirectorCommand
            {
                Verb = "recap-generate",
                SessionId = sid,
                PayloadJson = SessionCommandExecutor.Serialize(new RecapGenerateRequest { Model = model }),
            };

            try
            {
                var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, cancellationToken: ctx.RequestAborted);

                return result.Status switch
                {
                    // A successful recap carries Status="ok" and maps to the original 201; the domain-state
                    // bodies (no_session_id / no_jsonl / generation_failed) map to 200, exactly as before.
                    DirectorCommandStatus.Ok => RecapGenerateResult(result.BodyJson),
                    DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                    DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                    _ => Results.Problem(result.Error ?? "recap command failed"),
                };
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(499); // Client Closed Request
            }

            static IResult RecapGenerateResult(string? bodyJson)
            {
                var resp = SessionCommandExecutor.Deserialize<RecapResponse>(bodyJson);
                return Results.Json(resp, statusCode: resp?.Status == "ok" ? 201 : 200);
            }
        });

        // ===== REST: Voice (Whisper-backed Director UI voice mode) ==================
        // Accepts a multipart/form-data upload with one audio file field. Returns the
        // transcript + an executed reply. The hosted AI credential is resolved server-side and is
        // never sent to the browser.
        app.MapPost("/voice/command", async (HttpContext ctx) =>
        {
            FileLog.Write($"[ControlEndpoints] POST /voice/command");

            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "expected multipart/form-data with an audio file" });

            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.GetFile("file") ?? form.Files.GetFile("audio") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "no audio file uploaded; use form field 'file'" });

            var svc = new VoiceService(sessionManager, sessionManager.Options);
            await using var stream = file.OpenReadStream();
            var resp = await svc.HandleAsync(stream, file.FileName, ctx.RequestAborted);
            return Results.Json(resp);
        });

        // GET /voice/status - reports whether voice mode is enabled (key configured).
        // The browser uses this on page load to hide / disable the Voice button when
        // no key is present, instead of letting the user try and get an error mid-record.
        app.MapGet("/voice/status", () =>
        {
            var svc = new VoiceService(sessionManager, sessionManager.Options);
            return Results.Json(new { available = svc.IsAvailable });
        });

        // ===== REST: Resumable voice utterance upload (spotty-network safe) =========
        // Same origin as the Voice tab. Flow: register -> chunk(idempotent) -> complete.
        // Built for the car: each chunk that lands stays landed, so a dropped connection
        // resumes at the next missing chunk instead of re-sending the whole clip.
        app.MapPost("/voice/utterance", (VoiceUtteranceRegisterRequest? req) =>
        {
            var svc = new VoiceUtteranceService(sessionManager, sessionManager.Options);
            if (!svc.IsAvailable)
                return Results.Json(new { status = "no_key", error = "OpenAI API key missing" });
            var id = svc.Register(req?.UtteranceId);
            return Results.Json(new { utteranceId = id });
        });

        // Raw audio bytes in the body; X-Chunk-Sha256 header carries the hex digest so the
        // server can reject corruption and treat an identical retry as a no-op.
        app.MapPut("/voice/utterance/{id}/chunk/{index:int}", async (string id, int index, HttpContext ctx) =>
        {
            var sha = ctx.Request.Headers["X-Chunk-Sha256"].ToString();
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            var bytes = ms.ToArray();

            var svc = new VoiceUtteranceService(sessionManager, sessionManager.Options);
            try
            {
                await svc.StoreChunkAsync(id, index, bytes, string.IsNullOrEmpty(sha) ? null : sha, ctx.RequestAborted);
                return Results.Json(new { ok = true, index, bytes = bytes.Length });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] PUT /voice/utterance chunk FAILED: {ex.Message}");
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/voice/utterance/{id}/complete", async (string id, VoiceUtteranceCompleteRequest req, HttpContext ctx) =>
        {
            if (req is null || req.TotalChunks <= 0)
                return Results.BadRequest(new { error = "totalChunks (>0) is required" });

            var repoPath = "";
            var sessionName = "";
            if (!string.IsNullOrEmpty(req.SessionId) && Guid.TryParse(req.SessionId, out var sg))
            {
                var s = sessionManager.GetSession(sg);
                repoPath = s?.RepoPath ?? "";
                // Issue #800: route the display name through the single composer (never bare folder).
                sessionName = s is null ? ""
                    : SessionName.DisplayName(s.CustomName, SessionName.FolderName(s.RepoPath),
                        SessionName.Disambiguator(s.Id));
            }

            var svc = new VoiceUtteranceService(sessionManager, sessionManager.Options);
            var resp = await svc.CompleteAsync(id, req.TotalChunks, req.Mime ?? "audio/webm", repoPath,
                req.SessionId ?? "", sessionName, ctx.RequestAborted);
            // Out of credits / cap (issue #941): the transcribe carried the machine code as the status;
            // return the ONE shared 402 payload so the Voice tab shows the add-credits message + CTA.
            if (resp.Status == Core.HostedAi.HostedAiErrorMapper.InsufficientCreditsCode
                || resp.Status == Core.HostedAi.HostedAiErrorMapper.MonthlyLimitReachedCode)
                return Results.Json(
                    Core.HostedAi.HostedAiPayload.For(Core.HostedAi.HostedAiErrorMapper.MapCode(resp.Status)),
                    statusCode: Core.HostedAi.HostedAiPayload.PaymentRequired);
            // "incomplete" is a client-recoverable state (re-send missing chunks), so 409.
            return resp.Status == "incomplete"
                ? Results.Json(resp, statusCode: StatusCodes.Status409Conflict)
                : Results.Json(resp);
        });

        // ===== REST: Manager chat (Phase 1) =================================================
        // Relays one user message to the session configured by Chat.SessionRepoPath in
        // appsettings.json. Waits for the agent's turn to complete, returns the reply.
        // See docs/features/director/GOAL_VOICE_MANAGER.md Phase 1.
        app.MapPost("/chat", async (ChatRequest req, HttpContext ctx) =>
        {
            FileLog.Write($"[ControlEndpoints] POST /chat: textLen={req?.Text?.Length ?? 0}, pollOnly={req?.PollOnly ?? false}");
            // A poll request carries no new message (PollOnly): it only reads the
            // session's current state, so Text is not required in that mode.
            if (req is null || (!req.PollOnly && string.IsNullOrWhiteSpace(req.Text)))
                return Results.BadRequest(new { error = "text is required" });

            var svc = new ChatService(sessionManager, sessionManager.Options);
            var resp = await svc.HandleAsync(req, ctx.RequestAborted);

            // Map the service status to an HTTP code so the UI can branch cleanly.
            return resp.Status switch
            {
                "ok" or "timeout" or "working" => Results.Json(resp),
                "no_session_configured" => Results.Json(resp, statusCode: StatusCodes.Status503ServiceUnavailable),
                "session_not_found" => Results.Json(resp, statusCode: StatusCodes.Status404NotFound),
                "session_busy" => Results.Json(resp, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Json(resp, statusCode: StatusCodes.Status500InternalServerError),
            };
        });

        // ===== REST: Wingman rules / git / recovery (Phases 5-7) =========================
        // Each of these is a thin HTTP wrapper over the matching WingmanService method.
        app.MapPost("/sessions/{sid}/rule-violations", async (string sid, HttpContext ctx) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null) return Results.NotFound(new { error = "session not found" });

            var latest = turnSummaryCache?.GetForSession(guid).LastOrDefault();
            if (latest is null)
                return Results.Json(new RuleViolationsResponse { SessionId = sid, Status = "no_summary" });

            var resp = await WingmanService.CheckRulesAsync(latest, session.RepoPath, sessionManager.Options.ClaudePath, ctx.RequestAborted);
            resp.SessionId = sid;
            return Results.Json(resp);
        });

        // Read-only source-control snapshot (issue #1266). The summary fields (branch, dirty, ahead/behind,
        // last commit, status) come from WingmanService.GitSnapshotAsync as before; when the repo reads "ok"
        // the response is additively enriched with the per-file staged/unstaged lists from GitStatusProvider
        // (its own ten-second cache), so the Cockpit's Source Control tab can list what is changed and insert
        // a path into the composer. The enrichment is additive-only, so the existing Wingman consumer of
        // GitSnapshotAsync is untouched.
        // Gateway Cleanup Phase 0 (Worker R2): the read runs through the shared CatalogReadExecutor core so
        // this REST path and the Gateway stream down-channel are identical and cannot drift. Phase 1 deletes
        // this route and leaves the core reached only over the tunnel.
        app.MapGet("/sessions/{sid}/git", async (string sid, HttpContext ctx) =>
        {
            var command = new DirectorCommand { Verb = "git-status", SessionId = sid };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, cancellationToken: ctx.RequestAborted);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<GitSnapshot>(result.BodyJson)),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "git-status command failed"),
            };
        });

        // ===== Git WRITE actions (mirror the desktop Source Control view) =====
        // Reads stay on GET /git above; these mutate the working tree of the session's repo.
        // Gateway Cleanup Phase 0 (Worker W2): each git write now runs through the shared QueueGitExecutor
        // core so the REST route and the Gateway tunnel down-channel cannot drift. The command body (the
        // git result) rides back verbatim; a non-zero git exit surfaces as accepted=false, which this route
        // maps to HTTP 409 exactly as the old RunGitWrite helper did.
        async Task<IResult> DispatchGitWrite(string verb, string sid, object? payload)
        {
            var command = new DirectorCommand
            {
                Verb = verb,
                SessionId = sid,
                PayloadJson = payload is null ? "" : SessionCommandExecutor.Serialize(payload),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);
            return result.Status switch
            {
                DirectorCommandStatus.Ok =>
                    SessionCommandExecutor.Deserialize<GitWriteEnvelope>(result.BodyJson)!.Accepted
                        ? Results.Content(result.BodyJson ?? "", "application/json")
                        : Results.Content(result.BodyJson ?? "", "application/json", null, StatusCodes.Status409Conflict),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "git command failed"),
            };
        }

        app.MapPost("/sessions/{sid}/git/stage", (string sid, GitPathsRequest? req) =>
            DispatchGitWrite("git-stage", sid, req));
        app.MapPost("/sessions/{sid}/git/unstage", (string sid, GitPathsRequest? req) =>
            DispatchGitWrite("git-unstage", sid, req));
        app.MapPost("/sessions/{sid}/git/discard", (string sid, GitPathsRequest? req) =>
            DispatchGitWrite("git-discard", sid, req));
        app.MapPost("/sessions/{sid}/git/commit", (string sid, GitCommitRequest? req) =>
            DispatchGitWrite("git-commit", sid, req));

        // Re-point a Director session at a different Claude session id (mirrors the desktop
        // Relink button - recover continuity when the underlying Claude session id changed).
        // Gateway Cleanup Phase 0 (Worker W1): routed through the shared SessionWriteExecutor core.
        app.MapPost("/sessions/{sid}/relink", async (string sid, RelinkRequest? req) =>
        {
            var command = new DirectorCommand
            {
                Verb = "relink",
                SessionId = sid,
                PayloadJson = req is null ? "" : SessionCommandExecutor.Serialize(req),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Content(result.BodyJson ?? "", "application/json"),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "relink command failed"),
            };
        });

        app.MapPost("/sessions/{sid}/recovery-prompt", async (string sid, HttpContext ctx) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null) return Results.NotFound(new { error = "session not found" });
            var latest = turnSummaryCache?.GetForSession(guid).LastOrDefault();
            var rp = await WingmanService.BuildRecoveryPromptAsync(sid, session.RepoPath, latest, ctx.RequestAborted);
            return Results.Json(rp);
        });

        // ===== REST: OpenAI TTS (Phase 3) ===================================================
        // Voice mode posts spoken_text here, gets audio/mpeg back.  Falls back to
        // browser SpeechSynthesis on the client side if this fails.
        app.MapPost("/tts", async (TtsRequest req, HttpContext ctx) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.BadRequest(new TtsErrorResponse { Status = "empty_text", Error = "text is required" });

            // Hosted-AI aware: the resolver picks the DevThrottle account key, and the base URL +
            // voice follow the central hosted routing.
            var svc = new TtsService(sessionManager.Options, new Core.Configuration.HostedAiKeyResolver(Core.Configuration.GatewayConfig.Load));
            var result = await svc.GenerateAsync(req.Text, req.Voice, req.Model, ctx.RequestAborted);
            if (!result.Success || result.AudioBytes is null)
            {
                // Out of credits / cap (issue #941): a 402 carries the machine code as the status; return
                // the ONE shared payload (402) so the web player shows the add-credits message + CTA
                // instead of silently falling back to the robotic browser voice.
                if (result.Status == Core.HostedAi.HostedAiErrorMapper.InsufficientCreditsCode
                    || result.Status == Core.HostedAi.HostedAiErrorMapper.MonthlyLimitReachedCode)
                    return Results.Json(
                        Core.HostedAi.HostedAiPayload.For(Core.HostedAi.HostedAiErrorMapper.MapCode(result.Status)),
                        statusCode: Core.HostedAi.HostedAiPayload.PaymentRequired);

                var status = result.Status switch
                {
                    "no_key" => StatusCodes.Status503ServiceUnavailable,
                    "empty_text" => StatusCodes.Status400BadRequest,
                    "openai_failed" => StatusCodes.Status502BadGateway,
                    _ => StatusCodes.Status500InternalServerError,
                };
                return Results.Json(
                    new TtsErrorResponse { Status = result.Status, Error = result.ErrorMessage ?? "" },
                    statusCode: status);
            }
            return Results.File(result.AudioBytes, contentType: result.ContentType ?? "audio/mpeg");
        });

        app.MapGet("/tts/status", async () =>
        {
            var svc = new TtsService(sessionManager.Options, new Core.Configuration.HostedAiKeyResolver(Core.Configuration.GatewayConfig.Load));
            var mode = Core.Configuration.TranscriptionModeConfig.Get();
            return Results.Json(new
            {
                available = await svc.IsAvailableAsync(),
                voice = Core.Configuration.TtsVoiceConfig.Resolve(mode),
                model = Core.Configuration.TtsModelConfig.Resolve(mode),
            });
        });

        // ===== REST: Wingman turn summaries (Phase 2) ====================================
        // Per-completed-turn structured summary produced by the SessionWingman.
        // Feeds the Agent View AND the voice mode's TTS (via summary.spokenText).
        // See docs/goals/GOAL_CC_DIRECTOR_SUPERVISOR.md section 4.
        // Gateway Cleanup Phase 0: the read runs through the shared SessionReadExecutor core (given the
        // Director-local TurnSummaryCache via the command services) so this REST path and the Gateway stream
        // down-channel are identical and cannot drift. Phase 1 deletes this route.
        app.MapGet("/sessions/{sid}/turn-summaries", async (string sid) =>
        {
            var command = new DirectorCommand { Verb = "turn-summaries", SessionId = sid };
            var services = new SessionCommandServices { TurnSummaryCache = turnSummaryCache };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, services);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<TurnSummariesResponse>(result.BodyJson)),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "turn-summaries command failed"),
            };
        });

        // POST /sessions/{sid}/turn-summaries - generate a summary for the LATEST turn
        // on demand.  Used by the voice mode after a chat reply lands, so it can speak
        // the spoken_text version instead of the raw reply.  Synchronous: returns the
        // generated summary directly so the caller doesn't have to poll.
        app.MapPost("/sessions/{sid}/turn-summaries", async (string sid, HttpContext ctx) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            if (turnSummaryCache is null)
                return Results.Problem("Wingman turn-summary cache not wired", statusCode: 500);

            var summary = await turnSummaryCache.GenerateForLatestTurnAsync(guid, ctx.RequestAborted);
            if (summary is null)
                return Results.NotFound(new { error = "session not found or has no terminal output yet" });
            return Results.Json(summary, statusCode: 201);
        });

        // POST /sessions/{sid}/state-vote - human correction of the terminal state detector.
        // The user says what the status SHOULD have been; we capture it with the terminal
        // tail and file it to the GitHub tracking issue (and always locally). This is the
        // ground-truth feedback loop that replaces automated hook-vs-terminal measurement.
        app.MapPost("/sessions/{sid}/state-vote", async (string sid, StateVoteRequest req, HttpContext ctx) =>
        {
            FileLog.Write($"[ControlEndpoints] POST state-vote: sid={sid}, correct={req?.CorrectState}");
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });
            var session = sessionManager.GetSession(guid);
            if (session is null) return Results.NotFound(new { error = "session not found" });
            if (req is null || string.IsNullOrWhiteSpace(req.CorrectState))
                return Results.BadRequest(new { error = "correctState is required" });

            // Capture this session's own terminal tail (ANSI stripped) for context.
            var tail = "";
            try
            {
                var bytes = session.Buffer?.DumpAll();
                if (bytes is { Length: > 0 })
                {
                    const int TailBytes = 8192;
                    var start = Math.Max(0, bytes.Length - TailBytes);
                    tail = AnsiCleaner.Clean(Encoding.UTF8.GetString(bytes, start, bytes.Length - start));
                }
            }
            catch (Exception ex) { FileLog.Write($"[ControlEndpoints] state-vote tail FAILED: {ex.Message}"); }

            var vote = new Core.Feedback.StateVote(
                SessionId: sid,
                RepoPath: session.RepoPath,
                Agent: session.AgentKind.ToString(),
                DetectedState: string.IsNullOrWhiteSpace(req.DetectedState) ? session.ActivityState.ToString() : req.DetectedState!,
                DetectedReason: req.DetectedReason ?? session.LastStatusReason ?? "",
                CorrectState: req.CorrectState!,
                Note: req.Note ?? "",
                TerminalTail: tail,
                At: DateTime.UtcNow);

            var result = await Core.Feedback.StateVoteService.SubmitAsync(vote, ctx.RequestAborted);
            return Results.Json(result);
        });

        // Director-local handover. Source AND target must both live on this Director.
        // Cross-Director handovers go via the Gateway proxy.
        // Gateway Cleanup Phase 0 (wave 3): the atomic handover runs through the shared SessionWriteExecutor
        // core so this REST path and the Gateway stream down-channel are identical and cannot drift. The core
        // returns the target session mapped with the plain Map; this REST route re-maps it with the
        // identity-stamped mapper for its 201, exactly as the create verb (CreateLocalSessionAsync) does.
        // Phase 1 deletes this route and leaves the core reached only over the tunnel.
        app.MapPost("/handover", async (HandoverRequest req) =>
        {
            var command = new DirectorCommand
            {
                Verb = "handover-generate",
                SessionId = "",
                PayloadJson = req is null ? "" : SessionCommandExecutor.Serialize(req),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            switch (result.Status)
            {
                case DirectorCommandStatus.Ok:
                {
                    var resp = SessionCommandExecutor.Deserialize<HandoverResponse>(result.BodyJson);
                    // Re-map the target with this Director's identity-stamped mapper (the core returned the
                    // plain Map; machine/user/tailnet identity is stamped here, same as CreateLocalSessionAsync).
                    if (resp?.TargetSession is not null && Guid.TryParse(resp.TargetSession.SessionId, out var targetGuid))
                    {
                        var target = sessionManager.GetSession(targetGuid);
                        if (target is not null)
                            resp.TargetSession = MapWithIdentity(target, turnSummaryCache);
                    }
                    return Results.Json(resp, statusCode: 201);
                }
                case DirectorCommandStatus.BadRequest:
                    return Results.BadRequest(new { error = result.Error });
                case DirectorCommandStatus.NotFound:
                    return Results.NotFound(new { error = result.Error });
                case DirectorCommandStatus.Conflict:
                    return Results.StatusCode(StatusCodes.Status409Conflict);
                default:
                    return Results.Problem(result.Error ?? "handover failed", statusCode: 500);
            }
        });

        app.MapPost("/sessions/{sid}/prompt", async (string sid, PromptRequest req, HttpContext httpCtx) =>
        {
            // Issue #1181, Task 3b: the Gateway's OWN dictation delivery reaches a LOCKED session through
            // this control API (it bypasses the Gateway front door), so it carries the X-Dictation-Delivery
            // header naming the inbound upload id. The call already arrives with the fleet Bearer token
            // (DirectorEndpointClient), so the header is only present on an authenticated Gateway call and
            // cannot be forged from outside the fleet. Its presence marks this send as the dictation's own
            // arrival (Delivery, exempt); every other caller defaults to UserInput and is checked.
            var deliveryUploadId = httpCtx.Request.Headers["X-Dictation-Delivery"].ToString();
            var source = string.IsNullOrWhiteSpace(deliveryUploadId) ? SendSource.UserInput : SendSource.Delivery;
            FileLog.Write($"[ControlEndpoints] POST prompt: sid={sid}, len={req?.Text?.Length ?? 0}, source={source}");

            // Issue #1177 (Phase 1): the prompt body is executed through the shared SessionCommandExecutor
            // so this REST path and the Gateway stream down-channel run identical logic. The executor's
            // DirectorCommandStatus is mapped back to the same HTTP results this endpoint returned before.
            var command = new DirectorCommand
            {
                Verb = "prompt",
                SessionId = sid,
                PayloadJson = req is null ? "" : SessionCommandExecutor.Serialize(req),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, source: source);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<PromptResponse>(result.BodyJson)),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                DirectorCommandStatus.Conflict => Results.StatusCode(StatusCodes.Status409Conflict),
                DirectorCommandStatus.Locked => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status423Locked),
                _ => Results.Problem(result.Error ?? "prompt command failed"),
            };
        });

        // ===== Per-session prompt queue =====
        // Messages the user composed while the agent was busy. Stored on the session's
        // PromptQueue; the Cockpit's Queue button adds here, the queue panel lists/removes,
        // and "send" delivers an item to the PTY now. Mirrors the existing desktop queue.
        // Gateway Cleanup Phase 0 (Worker W2): every queue verb now runs through the shared QueueGitExecutor
        // core so the REST route and the Gateway tunnel down-channel cannot drift. Each route packages its
        // arguments into a DirectorCommand, dispatches, and maps the typed result back to the same HTTP shape
        // the old lambda returned (the success body is the queue projection, shipped verbatim).
        IResult MapQueueResult(DirectorCommandResult result) => result.Status switch
        {
            DirectorCommandStatus.Ok => Results.Content(result.BodyJson ?? "", "application/json"),
            DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
            DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
            _ => Results.Problem(result.Error ?? "queue command failed"),
        };

        async Task<IResult> DispatchQueue(string verb, string sid, object? payload)
        {
            var command = new DirectorCommand
            {
                Verb = verb,
                SessionId = sid,
                PayloadJson = payload is null ? "" : SessionCommandExecutor.Serialize(payload),
            };
            return MapQueueResult(await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command));
        }

        app.MapGet("/sessions/{sid}/queue", (string sid) =>
            DispatchQueue("queue-read", sid, null));

        app.MapPost("/sessions/{sid}/queue", (string sid, PromptRequest req) =>
        {
            FileLog.Write($"[ControlEndpoints] POST queue enqueue: sid={sid}, len={req?.Text?.Length ?? 0}");
            return DispatchQueue("queue-add", sid, new QueueItemCommand(Text: req?.Text));
        });

        app.MapDelete("/sessions/{sid}/queue/{itemId}", (string sid, string itemId) =>
        {
            FileLog.Write($"[ControlEndpoints] DELETE queue item: sid={sid}, item={itemId}");
            return DispatchQueue("queue-remove", sid, new QueueItemCommand(ItemId: itemId));
        });

        // Edit the text of a queued item in place.
        app.MapPatch("/sessions/{sid}/queue/{itemId}", (string sid, string itemId, PromptRequest req) =>
        {
            FileLog.Write($"[ControlEndpoints] PATCH queue edit: sid={sid}, item={itemId}");
            return DispatchQueue("queue-update", sid, new QueueItemCommand(ItemId: itemId, Text: req?.Text));
        });

        app.MapPost("/sessions/{sid}/queue/{itemId}/move-up", (string sid, string itemId) =>
        {
            FileLog.Write($"[ControlEndpoints] POST queue move-up: sid={sid}, item={itemId}");
            return DispatchQueue("queue-move-up", sid, new QueueItemCommand(ItemId: itemId));
        });

        app.MapPost("/sessions/{sid}/queue/{itemId}/move-down", (string sid, string itemId) =>
        {
            FileLog.Write($"[ControlEndpoints] POST queue move-down: sid={sid}, item={itemId}");
            return DispatchQueue("queue-move-down", sid, new QueueItemCommand(ItemId: itemId));
        });

        app.MapDelete("/sessions/{sid}/queue", (string sid) =>
        {
            FileLog.Write($"[ControlEndpoints] DELETE queue clear: sid={sid}");
            return DispatchQueue("queue-clear", sid, null);
        });

        // Deliver one queued item to the PTY now (and drop it from the queue). Used by the
        // queue panel's per-item "send" and by a "send next" action. Unlike the other queue verbs
        // this one can return 409 (an Exited/Failed session), returned here as an empty 409 exactly
        // as the old lambda did.
        app.MapPost("/sessions/{sid}/queue/{itemId}/send", async (string sid, string itemId) =>
        {
            FileLog.Write($"[ControlEndpoints] POST queue send: sid={sid}, item={itemId}");
            var command = new DirectorCommand
            {
                Verb = "queue-send",
                SessionId = sid,
                PayloadJson = SessionCommandExecutor.Serialize(new QueueItemCommand(ItemId: itemId)),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);
            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Content(result.BodyJson ?? "", "application/json"),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                DirectorCommandStatus.Conflict => Results.StatusCode(StatusCodes.Status409Conflict),
                _ => Results.Problem(result.Error ?? "queue send command failed"),
            };
        });

        // Hard interrupt via the session's agent driver (Ctrl+C for Claude). Drivers
        // own the per-CLI keystrokes now: pi for example has NO safe hard interrupt
        // (Ctrl+C twice quits it) and its driver refuses with 409.
        app.MapPost("/sessions/{sid}/interrupt", async (string sid) =>
        {
            FileLog.Write($"[ControlEndpoints] POST interrupt: sid={sid}");

            // Issue #1177 (Phase 1): executed through the shared SessionCommandExecutor so this REST path
            // and the Gateway stream down-channel run identical logic. A driver that refuses -> Conflict
            // with the same { error } body this endpoint returned before.
            var command = new DirectorCommand { Verb = "interrupt", SessionId = sid };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(new { accepted = true }),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                DirectorCommandStatus.Conflict => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Problem(result.Error ?? "interrupt command failed"),
            };
        });

        // Soft-stop the current turn via the session's agent driver (Esc for Claude
        // AND pi - but the driver owns that knowledge, not this endpoint).
        app.MapPost("/sessions/{sid}/escape", async (string sid) =>
        {
            FileLog.Write($"[ControlEndpoints] POST escape: sid={sid}");

            // Issue #1177 (Phase 1): executed through the shared SessionCommandExecutor (same as interrupt).
            var command = new DirectorCommand { Verb = "escape", SessionId = sid };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(new { accepted = true }),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                DirectorCommandStatus.Conflict => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Problem(result.Error ?? "escape command failed"),
            };
        });

        // Open the tool's in-terminal history picker (Claude's double-Esc). A
        // visible-terminal feature: the desktop/Cockpit terminal must be on screen.
        // Gateway Cleanup Phase 0 (Worker W1): routed through the shared SessionWriteExecutor core.
        app.MapPost("/sessions/{sid}/history-picker", async (string sid) =>
        {
            FileLog.Write($"[ControlEndpoints] POST history-picker: sid={sid}");

            var command = new DirectorCommand { Verb = "history-picker", SessionId = sid };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Content(result.BodyJson ?? "", "application/json"),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                DirectorCommandStatus.Conflict => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Problem(result.Error ?? "history-picker command failed"),
            };
        });

        // Reset the conversation context in place (/clear for Claude, /new for pi) and,
        // for transcript-capable drivers, re-link the Director to the NEW agent session
        // id - closing the stale-relink gap after /clear (issue #172 spike finding).
        // Gateway Cleanup Phase 0 (Worker W1): the reset runs through the shared SessionWriteExecutor core so
        // this REST path and the Gateway stream down-channel are identical and cannot drift.
        app.MapPost("/sessions/{sid}/clear-context", async (string sid, CancellationToken ct) =>
        {
            FileLog.Write($"[ControlEndpoints] POST clear-context: sid={sid}");

            var command = new DirectorCommand { Verb = "clear-context", SessionId = sid };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, cancellationToken: ct);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Content(result.BodyJson ?? "", "application/json"),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                DirectorCommandStatus.Conflict => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Problem(result.Error ?? "clear-context command failed"),
            };
        });

        // A Claude SessionStart hook (matchers startup/resume/clear/compact) reports the CURRENT
        // Claude session id + transcript path here. This is the authoritative, push-based pointer
        // update that keeps the Director tracking the right transcript across /clear and
        // auto-compaction, instead of the best-effort relink scan above. The hook script swallows
        // all errors, so this endpoint just records what it is given and returns 200. Accepts both
        // the mapped camelCase body (Windows PowerShell hook) and Claude's raw snake_case hook
        // event (forwarded verbatim by the macOS/Linux shell hook) - see ClaudeHookEventParser.
        app.MapPost("/sessions/{sid}/claude-hook", async (string sid, HttpContext httpCtx) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            string body;
            using (var reader = new StreamReader(httpCtx.Request.Body))
                body = await reader.ReadToEndAsync();

            var req = ClaudeHookEventParser.Parse(body);
            if (req is null)
                return Results.BadRequest(new { error = "invalid json body" });

            FileLog.Write($"[ControlEndpoints] POST claude-hook: sid={guid} event={req?.HookEvent} source={req?.Source} claudeId={req?.ClaudeSessionId} transcript={req?.TranscriptPath}");
            session.UpdateClaudeSessionPointer(req?.ClaudeSessionId, req?.TranscriptPath, req?.Source);

            // Keep the SessionManager's claude-id routing map in sync with the new id.
            if (!string.IsNullOrWhiteSpace(req?.ClaudeSessionId))
                sessionManager.RelinkClaudeSession(guid, req!.ClaudeSessionId!);

            return Results.Json(new { received = true });
        });

        // Resize the session's PTY grid so a remote terminal (the Cockpit) can use the full
        // window width. Session.Resize no-ops on an unchanged size, so a chatty client can't
        // hammer the PTY (the Wingman repaint-loop invariant).
        // Issue #1177 / Gateway Cleanup Phase 0: the resize runs through the shared SessionWriteExecutor core
        // so this REST path and the Gateway stream down-channel are identical and cannot drift. Phase 1
        // deletes this route and leaves the core reached only over the tunnel.
        app.MapPost("/sessions/{sid}/resize", async (string sid, ResizeRequest req) =>
        {
            var command = new DirectorCommand
            {
                Verb = "resize",
                SessionId = sid,
                PayloadJson = req is null ? "" : SessionCommandExecutor.Serialize(req),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<ResizeResponse>(result.BodyJson)),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "resize command failed"),
            };
        });

        // Upload an image (from the phone) and file it into the user's screenshots folder
        // on THIS Director's machine, where the owning Claude session can read it by
        // absolute path. Accepts multipart/form-data with one image field ("file"). Returns
        // the saved absolute path so the client can drop it into the composer for the user
        // to send. The session and the saved file live on the same machine by construction
        // (the session runs here), so the path is always valid for that session.
        // Gateway Cleanup Phase 0: the multipart form is read at THIS HTTP boundary (the tunnel never
        // carries multipart), then the already-read bytes and file name are handed to the SAME
        // upload-image byte verb the Gateway tunnel dispatches, so the SAVE logic lives in exactly one
        // place (SessionByteExecutor.SaveUploadedImage) and the two paths file bytes identically. The
        // id/session guards moved into that verb; only the genuinely boundary-specific multipart guards
        // (a non-form body, a missing/empty file field) stay in this lambda.
        app.MapPost("/sessions/{sid}/upload-image", async (string sid, HttpContext httpCtx) =>
        {
            FileLog.Write($"[ControlEndpoints] POST upload-image: sid={sid}");

            if (!httpCtx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "expected multipart/form-data with an image file field 'file'" });

            var form = await httpCtx.Request.ReadFormAsync(httpCtx.RequestAborted);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "no image uploaded; use form field 'file'" });

            byte[] bytes;
            await using (var src = file.OpenReadStream())
            using (var memory = new MemoryStream())
            {
                await src.CopyToAsync(memory, httpCtx.RequestAborted);
                bytes = memory.ToArray();
            }

            var command = new DirectorCommand
            {
                Verb = "upload-image",
                SessionId = sid,
                PayloadJson = SessionCommandExecutor.Serialize(new UploadImageRequest
                {
                    FileName = file.FileName,
                    BytesBase64 = Convert.ToBase64String(bytes),
                }),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<UploadImageResponse>(result.BodyJson)),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "upload-image command failed"),
            };
        });

        // ===== REST: Screenshots gallery =====
        // The screenshots folder (CcStorage.Screenshots()) lives on THIS Director's machine.
        // The desktop UI reads it via a FileSystemWatcher; the remote Cockpit can't, so it reads
        // the gallery over these endpoints. The browser loads thumbnails by pointing <img src>
        // straight at GET /screenshots/file (the same browser-direct path the live terminal uses),
        // so that endpoint is permissively CORS-open for image GETs on this single-user tailnet.

        // List the screenshots, newest first, with a pre-formatted local time label so the
        // Cockpit renders the same "MMM d, h:mm tt" as the desktop without re-deriving it.
        // The response is ALWAYS capped: ?count=N for an explicit cap, otherwise the newest
        // DefaultScreenshotCount. The folder can hold thousands of images and no client ever
        // shows more than the newest few - older files are deliberately ignored. "total"
        // always reports the full folder count so clients can say "newest N of total".
        // Gateway Cleanup Phase 0: the list runs through the shared SessionByteExecutor core so this REST
        // path and the Gateway tunnel verb are identical and cannot drift. Phase 1 deletes this route and
        // leaves the core reached only over the tunnel.
        app.MapGet("/screenshots", async (int? count) =>
        {
            var command = new DirectorCommand
            {
                Verb = "screenshots-list",
                PayloadJson = SessionCommandExecutor.Serialize(new ScreenshotListRequest { Count = count }),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<ScreenshotListResponse>(result.BodyJson)),
                _ => Results.Problem(result.Error ?? "screenshots-list command failed"),
            };
        });

        // Serve one screenshot's bytes. Loaded browser-direct as an <img src> and fetched for the
        // "Copy" clipboard action, so it sets Access-Control-Allow-Origin:* (single-user tailnet,
        // no auth/security gating per the deployment model). Path-traversal safe: name must be a
        // bare file that resolves inside the screenshots folder with an image extension.
        app.MapGet("/screenshots/file", (HttpContext ctx, string name) =>
        {
            var full = ResolveScreenshot(name);
            if (full is null)
            {
                FileLog.Write($"[ControlEndpoints] GET /screenshots/file rejected: name={name}");
                return Results.NotFound(new { error = "screenshot not found" });
            }
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            // Screenshot files are written once and never modified, so let the browser keep
            // thumbnails for an hour - session switches and tab revisits stop re-downloading
            // the same bytes over the tailnet.
            ctx.Response.Headers["Cache-Control"] = "public, max-age=3600";
            return Results.File(full, ScreenshotContentType(full), enableRangeProcessing: true);
        });

        // Delete one screenshot from disk (the per-card "Del" action). Mirrors the desktop, which
        // deletes the file off disk. Path-traversal safe via ResolveScreenshot.
        app.MapDelete("/screenshots/file", (string name) =>
        {
            var full = ResolveScreenshot(name);
            if (full is null)
                return Results.NotFound(new { error = "screenshot not found" });
            FileLog.Write($"[ControlEndpoints] DELETE /screenshots/file: {full}");
            File.Delete(full);
            return Results.Json(new { deleted = true, fileName = Path.GetFileName(full) });
        });

        // ===== REST: Fan-out within this Director =====
        app.MapPost("/fanout-local", async (FanoutRequest req) =>
        {
            if (req is null || req.SessionIds is null || req.SessionIds.Count == 0)
                return Results.BadRequest(new { error = "sessionIds is required" });
            if (string.IsNullOrEmpty(req.Text))
                return Results.BadRequest(new { error = "text is required" });

            FileLog.Write($"[ControlEndpoints] POST fanout-local: count={req.SessionIds.Count}, len={req.Text.Length}");

            var startedAt = DateTime.UtcNow;

            var tasks = req.SessionIds.Select(async sid =>
            {
                var sw = Stopwatch.StartNew();
                if (!Guid.TryParse(sid, out var guid))
                {
                    sw.Stop();
                    return new FanoutResult { SessionId = sid, Status = "not_found", Error = "invalid guid", ElapsedMs = sw.ElapsedMilliseconds };
                }
                var session = sessionManager.GetSession(guid);
                if (session is null)
                {
                    sw.Stop();
                    return new FanoutResult { SessionId = sid, Status = "not_found", Error = "session not found", ElapsedMs = sw.ElapsedMilliseconds };
                }

                var cursor = session.Buffer?.TotalBytesWritten ?? 0;
                try
                {
                    if (req.AppendEnter)
                        await session.SendTextAsync(req.Text);
                    else
                        session.SendInput(Encoding.UTF8.GetBytes(req.Text));
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    return new FanoutResult { SessionId = sid, DirectorId = directorId, Status = "failed", Error = ex.Message, ElapsedMs = sw.ElapsedMilliseconds };
                }

                if (!req.WaitForIdle)
                {
                    sw.Stop();
                    return new FanoutResult { SessionId = sid, DirectorId = directorId, Status = "idle", Output = "", ElapsedMs = sw.ElapsedMilliseconds };
                }

                var deadline = DateTime.UtcNow.AddMilliseconds(req.TimeoutMs);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(750);
                    var st = session.ActivityState;
                    if (st is ActivityState.Idle or ActivityState.WaitingForInput or ActivityState.Exited) break;
                }

                string output = "";
                if (session.Buffer is not null)
                {
                    var (data, _) = session.Buffer.GetWrittenSince(cursor);
                    output = AnsiCleaner.LastLines(AnsiCleaner.Clean(data), 500);
                }

                sw.Stop();
                var final = session.ActivityState;
                var status = final switch
                {
                    ActivityState.Idle or ActivityState.WaitingForInput => "idle",
                    ActivityState.Exited => "failed",
                    _ => "timeout",
                };
                return new FanoutResult { SessionId = sid, DirectorId = directorId, Status = status, Output = output, ElapsedMs = sw.ElapsedMilliseconds };
            }).ToList();

            var results = await Task.WhenAll(tasks);

            return Results.Json(new FanoutResponse
            {
                Results = results.ToList(),
                StartedAt = startedAt,
                FinishedAt = DateTime.UtcNow,
            });
        });

        // ===== REST: Repos (for the New Session picker) =====
        // Gateway Cleanup Phase 0 (wave 3): the read runs through the shared CatalogReadExecutor core so this
        // REST path and the Gateway stream down-channel are identical and cannot drift. The live registry -
        // the one dependency the tunnel command surface did not carry - is passed in through the services.
        // A null registry lists nothing (an empty array), exactly as before. Phase 1 deletes this route.
        app.MapGet("/repos", async () =>
        {
            var command = new DirectorCommand { Verb = "repos-list", SessionId = "" };
            var services = new SessionCommandServices { Repositories = repositoryRegistry };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, services);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Content(result.BodyJson ?? "[]", "application/json"),
                _ => Results.Problem(result.Error ?? "repos-list command failed"),
            };
        });

        // ===== REST: Remove a repository from the recent list =====
        app.MapDelete("/repos", (string? path) =>
        {
            FileLog.Write($"[ControlEndpoints] DELETE /repos: path={path}");
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path is required" });
            if (repositoryRegistry is null)
                return Results.Json(new { removed = false });

            var removed = repositoryRegistry.Remove(path);
            return Results.Json(new { removed });
        });

        // ===== REST: Register a repository explicitly (no session needed) =====
        app.MapPost("/repos", (RepoAddRequest req) =>
        {
            FileLog.Write($"[ControlEndpoints] POST /repos: path={req?.Path}, name=\"{req?.Name}\"");
            if (string.IsNullOrWhiteSpace(req?.Path))
                return Results.BadRequest(new { error = "path is required" });
            if (repositoryRegistry is null)
                return Results.BadRequest(new { error = "repository registry not available" });
            if (!Directory.Exists(req.Path))
                return Results.BadRequest(new { error = $"directory not found: {req.Path}" });

            var added = repositoryRegistry.TryAdd(req.Path);
            if (!string.IsNullOrWhiteSpace(req.Name))
                repositoryRegistry.Rename(req.Path, req.Name);

            var wanted = NormalizeRepoPath(req.Path);
            var repo = repositoryRegistry.Repositories.First(r => NormalizeRepoPath(r.Path) == wanted);
            var dto = new RepositoryDto
            {
                Name = string.IsNullOrEmpty(repo.Name) ? Path.GetFileName(repo.Path.TrimEnd('\\', '/')) : repo.Name,
                Path = repo.Path,
                LastUsed = repo.LastUsed,
            };
            return Results.Json(new { added, repo = dto },
                statusCode: added ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        });

        // ===== REST: Rename a registered repository =====
        app.MapPatch("/repos", (RepoRenameRequest req) =>
        {
            FileLog.Write($"[ControlEndpoints] PATCH /repos: path={req?.Path}, name=\"{req?.Name}\"");
            if (string.IsNullOrWhiteSpace(req?.Path))
                return Results.BadRequest(new { error = "path is required" });
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "name is required" });
            if (repositoryRegistry is null)
                return Results.BadRequest(new { error = "repository registry not available" });

            if (!repositoryRegistry.Rename(req.Path, req.Name))
                return Results.NotFound(new { error = "repository not registered" });

            var wanted = NormalizeRepoPath(req.Path);
            var repo = repositoryRegistry.Repositories.First(r => NormalizeRepoPath(r.Path) == wanted);
            return Results.Json(new RepositoryDto
            {
                Name = repo.Name,
                Path = repo.Path,
                LastUsed = repo.LastUsed,
            });
        });

        // ===== REST: Enriched per-repo overview (repositories page) =====
        app.MapGet("/repos/overview", () =>
        {
            FileLog.Write("[ControlEndpoints] GET /repos/overview");
            if (repositoryRegistry is null)
                return Results.Json(Array.Empty<RepoOverviewDto>());

            // Aggregate every per-repo data source once, keyed by normalized path.
            var liveByRepo = sessionManager.ListSessions()
                .Where(s => s.ActivityState != ActivityState.Exited)
                .GroupBy(s => NormalizeRepoPath(s.RepoPath))
                .ToDictionary(g => g.Key, g => g.Select(s => s.CustomName ?? ProjectNameOf(s.RepoPath)).ToList());

            var historyByRepo = new SessionHistoryStore().LoadAll()
                .Where(h => !string.IsNullOrEmpty(h.RepoPath))
                .GroupBy(h => NormalizeRepoPath(h.RepoPath))
                .ToDictionary(g => g.Key, g => g.ToList());

            var claudeByRepo = ClaudeSessionReader.ScanAllProjects()
                .Where(m => !string.IsNullOrEmpty(m.ProjectPath))
                .GroupBy(m => NormalizeRepoPath(m.ProjectPath!))
                .ToDictionary(g => g.Key, g => g.ToList());

            var handoversByRepo = HandoverScanner.ScanAll()
                .SelectMany(h => h.RepoPaths.Select(p => (Repo: NormalizeRepoPath(p), Handover: h)))
                .GroupBy(x => x.Repo)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Handover).ToList());

            var overview = repositoryRegistry.Repositories.Select(r =>
            {
                var key = NormalizeRepoPath(r.Path);
                liveByRepo.TryGetValue(key, out var liveNames);
                historyByRepo.TryGetValue(key, out var history);
                claudeByRepo.TryGetValue(key, out var claude);
                handoversByRepo.TryGetValue(key, out var handovers);

                var latestHistory = history?.OrderByDescending(h => h.LastUsedAt).FirstOrDefault();
                var latestClaude = claude?.OrderByDescending(m => m.Modified).FirstOrDefault();

                var lastHistoryAt = latestHistory is null || latestHistory.LastUsedAt == default
                    ? (DateTime?)null
                    : latestHistory.LastUsedAt.UtcDateTime;
                var lastClaudeAt = latestClaude is null || latestClaude.Modified == DateTime.MinValue
                    ? (DateTime?)null
                    : latestClaude.Modified;

                // Most recent activity wins; its summary describes the last session.
                var lastSessionAt = (lastHistoryAt ?? DateTime.MinValue) >= (lastClaudeAt ?? DateTime.MinValue)
                    ? lastHistoryAt ?? lastClaudeAt
                    : lastClaudeAt;
                var lastSummary = lastSessionAt == lastClaudeAt
                    ? latestClaude?.Summary ?? latestClaude?.FirstPrompt ?? latestHistory?.FirstPromptSnippet
                    : latestHistory?.FirstPromptSnippet ?? latestClaude?.Summary ?? latestClaude?.FirstPrompt;

                return new RepoOverviewDto
                {
                    Name = string.IsNullOrEmpty(r.Name) ? Path.GetFileName(r.Path.TrimEnd('\\', '/')) : r.Name,
                    Path = r.Path,
                    LastUsed = r.LastUsed,
                    PathExists = Directory.Exists(r.Path),
                    LiveSessionCount = liveNames?.Count ?? 0,
                    LiveSessionNames = liveNames ?? new List<string>(),
                    ResumableSessionCount = claude?.Count ?? 0,
                    HistorySessionCount = history?.Count ?? 0,
                    LastSessionAtUtc = lastSessionAt,
                    LastSessionSummary = lastSummary,
                    GitBranch = claude?.OrderByDescending(m => m.Modified)
                        .FirstOrDefault(m => !string.IsNullOrEmpty(m.GitBranch))?.GitBranch,
                    HandoverCount = handovers?.Count ?? 0,
                    LastHandoverUtc = handovers?.Max(h => h.DateUtc),
                };
            })
            .OrderByDescending(r => r.LastUsed ?? DateTime.MinValue)
            .ToList();

            return Results.Json(overview);
        });

        // ===== REST: Coaching quick-launch categories (Assistant / Coach cards) =====
        // Gateway Cleanup Phase 0 (Worker R2): the read runs through the shared CatalogReadExecutor core so
        // this REST path and the Gateway stream down-channel are identical and cannot drift. Phase 1 deletes
        // this route.
        app.MapGet("/coaching/categories", async () =>
        {
            var command = new DirectorCommand { Verb = "coaching-categories" };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<List<CoachingCategoryDto>>(result.BodyJson)),
                _ => Results.Problem(result.Error ?? "coaching-categories command failed"),
            };
        });

        // ===== REST: Resumable Claude Code sessions (Resume Session tab) =====
        // Gateway Cleanup Phase 0 (Worker R2): the read runs through the shared CatalogReadExecutor core so
        // this REST path and the Gateway stream down-channel are identical and cannot drift. Phase 1 deletes
        // this route. The ?repo= filter rides in the command payload as a ClaudeSessionsRequest.
        app.MapGet("/claude-sessions", async (string? repo) =>
        {
            var command = new DirectorCommand
            {
                Verb = "claude-sessions",
                PayloadJson = SessionCommandExecutor.Serialize(new ClaudeSessionsRequest { Repo = repo }),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<List<ClaudeSessionDto>>(result.BodyJson)),
                _ => Results.Problem(result.Error ?? "claude-sessions command failed"),
            };
        });

        // ===== REST: Handover documents (Handovers tab) =====
        app.MapGet("/handovers", (string? repo) =>
        {
            FileLog.Write($"[ControlEndpoints] GET /handovers: repo={repo}");
            var infos = HandoverScanner.ScanAll();
            if (!string.IsNullOrWhiteSpace(repo))
            {
                var wanted = NormalizeRepoPath(repo);
                infos = infos.Where(h => h.RepoPaths.Any(p => NormalizeRepoPath(p) == wanted)).ToList();
            }
            var dtos = infos.Select(h => new HandoverDto
            {
                Path = h.Path,
                Title = h.Title,
                DateDisplay = h.DateDisplay,
                DateUtc = h.DateUtc,
                RepoPath = h.RepoPath,
                RepoPaths = h.RepoPaths,
                SessionName = h.SessionName,
            }).ToList();
            return Results.Json(dtos);
        });

        app.MapPost("/handovers", (HandoverCreateRequest req) =>
        {
            // Standalone handover document: written to the vault handover folder so it
            // shows up in the Handovers tab and GET /handovers. Unlike POST /handover,
            // no target session is involved.
            FileLog.Write($"[ControlEndpoints] POST /handovers: title=\"{req?.Title}\"");
            if (string.IsNullOrWhiteSpace(req?.Title))
                return Results.BadRequest(new { error = "title is required" });
            if (string.IsNullOrWhiteSpace(req.Content))
                return Results.BadRequest(new { error = "content is required" });

            var path = HandoverScanner.WriteNew(req.Title, req.Content, req.RepoPaths, req.SessionName);
            var h = HandoverScanner.Parse(path);
            return Results.Json(new HandoverDto
            {
                Path = h.Path,
                Title = h.Title,
                DateDisplay = h.DateDisplay,
                DateUtc = h.DateUtc,
                RepoPath = h.RepoPath,
                RepoPaths = h.RepoPaths,
                SessionName = h.SessionName,
            }, statusCode: StatusCodes.Status201Created);
        });

        app.MapDelete("/handovers", (string? path) =>
        {
            FileLog.Write($"[ControlEndpoints] DELETE /handovers: path={path}");
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path is required" });
            try
            {
                HandoverScanner.Delete(path);
                return Results.Json(new { removed = true });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound(new { error = "handover not found" });
            }
        });

        app.MapGet("/handovers/content", (string? path) =>
        {
            FileLog.Write($"[ControlEndpoints] GET /handovers/content: path={path}");
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path is required" });
            try
            {
                var content = HandoverScanner.ReadContent(path);
                return Results.Json(new HandoverContentDto { Path = path, Content = content });
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound(new { error = "handover not found" });
            }
        });

        // ===== REST: Remote folder browser (Browse... button) =====
        // Gateway Cleanup Phase 0 (Worker R2): the list-directory read runs through the shared
        // CatalogReadExecutor core so this REST path and the Gateway stream down-channel are identical and
        // cannot drift. Phase 1 deletes this route. The ?path= argument rides in the command payload as an
        // FsListRequest; the core preserves the source route's try/catch, so a bad path is still a 400.
        app.MapGet("/fs/list", async (string? path) =>
        {
            var command = new DirectorCommand
            {
                Verb = "fs-list",
                PayloadJson = SessionCommandExecutor.Serialize(new FsListRequest { Path = path }),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<DirectoryListingDto>(result.BodyJson)),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "fs-list command failed"),
            };
        });

        // ===== REST: Create a session =====
        app.MapPost("/sessions", async (NewSessionRequest req) =>
        {
            FileLog.Write($"[ControlEndpoints] POST /sessions: repo={req?.RepoPath}, agent={req?.Agent}");

            // Issue #1177 (Phase 1): the whole create - agent parse/construct, default-args, name-at-birth
            // validation, CreateSession, Wingman opt-in, and the fire-and-forget PrePrompt - runs through
            // the shared SessionCommandExecutor so this REST path and the Gateway stream down-channel
            // create identically. The Director's own 201 response is still built with the identity-stamped
            // MapWithIdentity (unchanged), so this endpoint stays byte-identical; the executor returns the
            // plain stream DTO that the Gateway stamps during aggregation. The Machine routing field (if any)
            // is advisory here - a POST /sessions always creates on THIS Director.
            return await CreateLocalSessionAsync(req);
        });

        // ===== REST: Create a GitHub Actions remote session =====
        // Gateway Cleanup Phase 0 (Worker W2): the whole create-from-github - the validation guards and the
        // CreateGitHubActionsSession call - runs through the shared QueueGitExecutor so this REST path and the
        // Gateway tunnel down-channel create identically. The Director's own 201 response is still built with
        // the identity-stamped MapWithIdentity (unchanged), so this endpoint stays byte-identical; the executor
        // returns the plain stream DTO. Mirrors CreateLocalSessionAsync for the local `create` verb.
        app.MapPost("/sessions/github", async (GitHubSessionRequest req) =>
        {
            FileLog.Write($"[ControlEndpoints] POST /sessions/github: {req?.Owner}/{req?.Repo} mode={req?.TriggerMode}");

            var command = new DirectorCommand
            {
                Verb = "create-from-github",
                SessionId = "",
                PayloadJson = req is null ? "" : SessionCommandExecutor.Serialize(req),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            if (result.Status == DirectorCommandStatus.BadRequest)
                return Results.BadRequest(new { error = result.Error });
            if (result.Status != DirectorCommandStatus.Ok)
                return Results.Problem(result.Error ?? "create-from-github failed", statusCode: 500);

            var created = SessionCommandExecutor.Deserialize<SessionDto>(result.BodyJson);
            if (created is null || !Guid.TryParse(created.SessionId, out var newGuid))
                return Results.Problem("created session id missing", statusCode: 500);
            var session = sessionManager.GetSession(newGuid);
            return session is null
                ? Results.Problem("created session not found", statusCode: 500)
                : Results.Json(MapWithIdentity(session, turnSummaryCache), statusCode: 201);
        });

        // ===== REST: Kill a session =====
        app.MapDelete("/sessions/{sid}", async (HttpContext ctx, string sid) =>
        {
            // Name the local caller (issue #212 L3): a loopback DELETE could be any agent
            // on this machine, and "127.0.0.1" alone is useless for forensics.
            var caller = Core.Network.LoopbackPeerResolver.Describe(ctx.Connection.RemotePort, ctx.Connection.LocalPort);
            FileLog.Write($"[ControlEndpoints] DELETE /sessions/{sid} caller={caller}");

            // Issue #1177 (Phase 1): the kill+remove runs through the shared SessionCommandExecutor (same
            // best-effort-kill-then-remove semantics as before), so this REST path and the Gateway stream
            // down-channel are identical. The boundary try-catch preserves the endpoint's 500 on an
            // unexpected fault (an executor verb is not a boundary, so it lets such faults bubble here).
            try
            {
                var command = new DirectorCommand { Verb = "kill", SessionId = sid };
                var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

                return result.Status switch
                {
                    DirectorCommandStatus.Ok => Results.Json(new { killed = true, removed = true }),
                    DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                    DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                    _ => Results.Problem(result.Error ?? "kill command failed", statusCode: 500),
                };
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] DELETE FAILED: {ex.Message}");
                return Results.Problem(ex.Message, statusCode: 500);
            }
        });

        // ===== REST: Ask to be deleted (self-requested teardown) =====
        // Flag a session for asynchronous removal instead of killing it now. The owning Director's
        // deletion reaper removes it on its next ~30s sweep, once a short grace has elapsed and the
        // session is no longer Working. This is the SAFE self-delete: an agent flags its OWN session
        // (id = CC_SESSION_ID) and then finishes its turn - unlike DELETE /sessions/{sid}, it does not
        // kill the caller's process mid-request. Body is optional ({ "reason": "..." }).
        // Gateway Cleanup Phase 0 (Worker W1): the deletion flag runs through the shared SessionWriteExecutor
        // core. The caller-identity log stays here at the HTTP boundary (it reads the loopback connection).
        app.MapPost("/sessions/{sid}/request-deletion", async (HttpContext ctx, string sid, SessionDeletionRequest? body) =>
        {
            var caller = Core.Network.LoopbackPeerResolver.Describe(ctx.Connection.RemotePort, ctx.Connection.LocalPort);
            FileLog.Write($"[ControlEndpoints] POST /sessions/{sid}/request-deletion caller={caller} reason=\"{body?.Reason}\"");

            var command = new DirectorCommand
            {
                Verb = "request-deletion",
                SessionId = sid,
                PayloadJson = body is null ? "" : SessionCommandExecutor.Serialize(body),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Content(result.BodyJson ?? "", "application/json"),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "request-deletion command failed"),
            };
        });

        // Cancel a pending deletion during the grace window (operator changed their mind).
        // Gateway Cleanup Phase 0 (Worker W1): routed through the shared SessionWriteExecutor core; the
        // caller-identity log stays here at the HTTP boundary.
        app.MapDelete("/sessions/{sid}/request-deletion", async (HttpContext ctx, string sid) =>
        {
            var caller = Core.Network.LoopbackPeerResolver.Describe(ctx.Connection.RemotePort, ctx.Connection.LocalPort);
            FileLog.Write($"[ControlEndpoints] DELETE /sessions/{sid}/request-deletion caller={caller}");

            var command = new DirectorCommand { Verb = "cancel-deletion", SessionId = sid };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Content(result.BodyJson ?? "", "application/json"),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "cancel-deletion command failed"),
            };
        });

        // ===== Crash recovery (issue #212 W3) =====
        // This machine's claimed dirty crash journals - Directors that died abnormally here,
        // with their recoverable session rosters. The Gateway aggregates these across the fleet
        // for the Cockpit's Interrupted sessions list.
        // Gateway Cleanup Phase 0 (Worker R2): the read runs through the shared CatalogReadExecutor core so
        // this REST path and the Gateway stream down-channel are identical and cannot drift. Phase 1 deletes
        // this route.
        app.MapGet("/interrupted", async () =>
        {
            var command = new DirectorCommand { Verb = "interrupted-list" };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<List<Core.Sessions.DirectorCrashJournalData>>(result.BodyJson)),
                _ => Results.Problem(result.Error ?? "interrupted-list command failed"),
            };
        });

        // Dismiss one claimed dirty journal once its sessions are recovered or no longer wanted.
        app.MapDelete("/interrupted/{deadDirectorId}/{deadPid:int}", (HttpContext ctx, string deadDirectorId, int deadPid) =>
        {
            var caller = Core.Network.LoopbackPeerResolver.Describe(ctx.Connection.RemotePort, ctx.Connection.LocalPort);
            FileLog.Write($"[ControlEndpoints] DELETE /interrupted/{deadDirectorId}/{deadPid} caller={caller}");
            var removed = Core.Sessions.DirectorCrashJournal.Dismiss(deadDirectorId, deadPid);
            return removed ? Results.Json(new { dismissed = true }) : Results.NotFound(new { error = "no such interrupted journal" });
        });

        // Remove ONE session from a claimed dirty journal after it has been restored
        // (issue #212 W4): the rest of the journal stays in the Interrupted sessions list.
        app.MapDelete("/interrupted/{deadDirectorId}/{deadPid:int}/sessions/{sessionId}",
            (HttpContext ctx, string deadDirectorId, int deadPid, string sessionId) =>
        {
            var caller = Core.Network.LoopbackPeerResolver.Describe(ctx.Connection.RemotePort, ctx.Connection.LocalPort);
            FileLog.Write($"[ControlEndpoints] DELETE /interrupted/{deadDirectorId}/{deadPid}/sessions/{sessionId} caller={caller}");
            var removed = Core.Sessions.DirectorCrashJournal.RemoveSession(deadDirectorId, deadPid, sessionId);
            return removed ? Results.Json(new { removed = true }) : Results.NotFound(new { error = "no such interrupted session" });
        });

        // ===== Admin: session-number backfill (issue #846) =====
        // Trigger SessionManager.BackfillNumbers() on a RUNNING Director - no restart - so an
        // operator can number sessions that predate #820 (or were restored without a number).
        // Returns the count newly numbered. Idempotent: a second call returns assigned=0 because
        // BackfillNumbers skips sessions that already carry a number; numbers stay unique among
        // this Director's active sessions and within 100-999 (the existing SessionNumberAllocator).
        // Not in DirectorAuth.PublicPaths, so the Bearer token (or login cookie) is required when
        // auth is enabled. Reached through the Gateway via POST /directors/{id}/backfill-numbers,
        // which forwards here (a Director-id-keyed route, since the per-session /sessions/{sid}
        // catch-all would treat a non-session path segment as a session id).
        app.MapPost("/admin/backfill-numbers", (HttpContext ctx) =>
        {
            var caller = Core.Network.LoopbackPeerResolver.Describe(ctx.Connection.RemotePort, ctx.Connection.LocalPort);
            FileLog.Write($"[ControlEndpoints] POST admin/backfill-numbers requested caller={caller}");
            var assigned = sessionManager.BackfillNumbers();
            FileLog.Write($"[ControlEndpoints] admin/backfill-numbers assigned {assigned} number(s)");
            return Results.Json(new { assigned });
        });

        // ===== Shutdown =====
        app.MapPost("/shutdown", (HttpContext ctx) =>
        {
            // Name the local caller (issue #212 L3). This endpoint stops the whole Director
            // and every claude.exe under it; the 2026-06-06 post-mortem could not tell whether
            // an agent had triggered a shutdown because the caller was never recorded.
            var caller = Core.Network.LoopbackPeerResolver.Describe(ctx.Connection.RemotePort, ctx.Connection.LocalPort);
            FileLog.Write($"[ControlEndpoints] POST shutdown requested caller={caller}");
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                try { await requestShutdownAsync(); }
                catch (Exception ex) { FileLog.Write($"[ControlEndpoints] Shutdown FAILED: {ex.Message}"); }
            });
            return Results.Json(new { accepted = true });
        });

        // ===== Execute-action verb (issue #327, plan 1B) =====
        // The DUMB execute leg of the wingman decide/execute split: the caller (the Gateway
        // brain in Phase 3) supplies the complete structured WingmanAction in the body and
        // this handler carries it out EXACTLY as passed via WingmanActionExecutor - the
        // single write chokepoint - with zero decision logic and no LLM involvement.
        // All executor invariants apply unchanged (enforcement, not intelligence): audit
        // trail in Session.RecentWingmanActions, 3s same-screen idempotency cooldown,
        // self-injection suppression window, exited/failed-session guard. Contrast
        // POST /sessions/{sid}/wingman/act above, which DECIDES the action before acting;
        // that endpoint stays until the Phase-3 split removes its decide leg.
        // Gateway Cleanup Phase 0 (Worker W1): the mechanical execute runs through the shared
        // SessionWriteExecutor core so this REST path and the Gateway stream down-channel are identical and
        // cannot drift. The core's guards map to the same 400 / 404 the lambda returned; the executor's own
        // outcome rides back inside the WingmanActResult, and the mechanical status -> HTTP mapping (a gone
        // session -> 410, a malformed action -> 400, ok / suppressed -> 200) is reproduced here unchanged.
        app.MapPost("/sessions/{sid}/execute-action", async (string sid, WingmanAction? action) =>
        {
            var command = new DirectorCommand
            {
                Verb = "execute-action",
                SessionId = sid,
                PayloadJson = action is null ? "" : SessionCommandExecutor.Serialize(action),
            };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            switch (result.Status)
            {
                case DirectorCommandStatus.Ok:
                    var actResult = SessionCommandExecutor.Deserialize<WingmanActResult>(result.BodyJson)!;
                    if (actResult.Status == WingmanActResult.StatusSessionGone)
                        return Results.Json(actResult, statusCode: StatusCodes.Status410Gone);
                    if (actResult.Status == WingmanActResult.StatusBadRequest)
                        return Results.Json(actResult, statusCode: StatusCodes.Status400BadRequest);
                    return Results.Json(actResult);
                case DirectorCommandStatus.BadRequest:
                    return Results.Json(new WingmanActResult { Status = WingmanActResult.StatusBadRequest, Error = result.Error }, statusCode: StatusCodes.Status400BadRequest);
                case DirectorCommandStatus.NotFound:
                    return Results.NotFound(new { error = result.Error });
                default:
                    return Results.Problem(result.Error ?? "execute-action command failed");
            }
        });
    }

    /// <summary>Folder name of a repo path, for display fallback. Empty path -> "Unknown Project".</summary>
    // Gateway Cleanup Phase 0 (Worker R2): widened to internal so CatalogReadExecutor's claude-sessions core
    // (lifted verbatim from GET /claude-sessions) calls the SAME helper - no drift, no duplication.
    internal static string ProjectNameOf(string repoPath)
    {
        if (string.IsNullOrEmpty(repoPath))
            return "Unknown Project";
        return Path.GetFileName(repoPath.TrimEnd('\\', '/'));
    }

    /// <summary>Display-cap for brief text blocks. Null-safe; marks the cut explicitly.</summary>
    private static string? TruncateForDisplay(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max ? s : s[..max] + "... [truncated]";
    }

    /// <summary>
    /// Canonical form for repo-path comparison across endpoints (?repo= filters, overview
    /// grouping): full path, trailing separators trimmed, lowercased (Windows paths are
    /// case-insensitive). Callers must filter null/empty first; Path.GetFullPath throws
    /// only on empty or embedded-NUL input, which the global error envelope surfaces loudly.
    /// </summary>
    // Gateway Cleanup Phase 0 (Worker R2): widened to internal so CatalogReadExecutor's claude-sessions core
    // (lifted verbatim from GET /claude-sessions) calls the SAME helper - no drift, no duplication.
    internal static string NormalizeRepoPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();
    }

    /// <summary>
    /// List sub-directories of <paramref name="path"/> for the remote folder browser. A null or
    /// empty path returns the drive roots. Solo-tailnet: no path sandboxing (see remote-experience-plan.md).
    /// </summary>
    // Gateway Cleanup Phase 0 (Worker R2): widened to internal so CatalogReadExecutor's fs-list core (the
    // list-directory read, lifted from GET /fs/list) calls the SAME helper - no drift, no duplication.
    internal static DirectoryListingDto ListDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new DirEntryDto
                {
                    Name = d.Name.TrimEnd('\\', '/'),
                    Path = d.RootDirectory.FullName,
                    IsDrive = true,
                })
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new DirectoryListingDto { CurrentPath = null, ParentPath = null, Entries = drives };
        }

        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"directory not found: {full}");

        var parent = Directory.GetParent(full.TrimEnd('\\', '/'))?.FullName;

        var entries = Directory.EnumerateDirectories(full)
            .Select(d =>
            {
                try
                {
                    // Skip hidden / system directories so the picker stays clean.
                    var attr = File.GetAttributes(d);
                    if (attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System))
                        return null;
                }
                catch { return null; }
                return new DirEntryDto { Name = Path.GetFileName(d), Path = d, IsDrive = false };
            })
            .Where(e => e is not null)
            .Select(e => e!)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DirectoryListingDto { CurrentPath = full, ParentPath = parent, Entries = entries };
    }

    /// <summary>
    /// The session's ENTIRE terminal buffer, ANSI stripped, for the "Ask the Wingman"
    /// answer path. Unlike WingmanContextBuilder's tail (capped at 4000 chars for a
    /// one-shot prompt), this returns everything: the read-only session writes it to a
    /// snapshot file and reads only as much as it needs, so "read me the whole article"
    /// can reach content that scrolled past a tail. Read-only inspection; never mutates.
    /// </summary>
    internal static string ReadFullCleanedBuffer(Session session)
    {
        try
        {
            var bytes = session.Buffer?.DumpAll();
            if (bytes is null || bytes.Length == 0) return "";
            return AnsiCleaner.Clean(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ControlEndpoints] ReadFullCleanedBuffer FAILED: {ex.Message}");
            return "";
        }
    }

    /// <summary>The session driver's capability flags as names, for UIs to render
    /// action buttons from (no per-agent special cases client-side).</summary>
    private static List<string> CapabilityNames(Session s)
    {
        var caps = s.Driver.Capabilities;
        return Enum.GetValues<Core.Drivers.DriverCapabilities>()
            .Where(f => f != Core.Drivers.DriverCapabilities.None && caps.HasFlag(f))
            .Select(f => f.ToString())
            .ToList();
    }

    /// <summary>
    /// Map a session to its DTO. Issue #335: machineName, user, tailnetEndpoint, and viewUrl
    /// are now populated by the Director itself (not patched in by the Gateway), so every
    /// consumer of the Director-local /sessions and /sessions/{sid} endpoints always sees
    /// the four identity fields. The Gateway aggregation pass still enriches DTOs from OLD
    /// Directors that send empty fields (back-compat for mixed-version fleets) but must NOT
    /// overwrite non-empty Director-supplied values.
    /// </summary>
    // Issue #1176 (Phase 1a): internal so the Director's stream client builds its pushed snapshots and
    // deltas through the EXACT same mapper the local /sessions endpoint uses (review #6), rather than a
    // second, divergent builder. Callers outside /sessions pass only (session, directorId); the Gateway
    // aggregator stamps machine/user/tailnet/view-url during aggregation, for pushed and pulled alike.
    /// <summary>Map a Mission record onto its wire DTO (mission-as-first-class-unit-of-work).</summary>
    internal static MissionDto ToMissionDto(Mission m) => new()
    {
        MissionId = m.MissionId,
        MissionName = m.MissionName,
        ParentMissionId = m.ParentMissionId,
    };

    internal static SessionDto Map(Session s, string directorId, TurnSummaryCache? cache = null,
        string machineName = "", string user = "", string tailnetEndpoint = "", string? gatewayUrl = null)
    {
        // Phase 3: StatusColor and LastStatusReason are owned by the SessionStatusWingman
        // and live on the Session itself. Map() reads them directly - no derivation, no
        // recomputation from TurnSummaryCache, no fallback. The `cache` argument is kept for
        // other endpoints that surface raw summaries; it is not consulted for color.
        // Idle clock source. Most agents go byte-silent when idle, so the raw last-write time is
        // the honest "how long since output" measure. Agents with an animated idle footer (Grok)
        // never go byte-silent - their footer repaints forever - so the raw clock would read ~0
        // even when the agent is done and waiting. For those, measure idle from the last screen-
        // BODY change instead (Session.LastBodyActivityAtUtc), matching the detector's content rule
        // so the idle seconds and the WaitingForInput badge agree.
        DateTime lastActivity;
        if (s.Driver.EmitsContinuousIdleOutput)
        {
            lastActivity = s.LastBodyActivityAtUtc;
        }
        else
        {
            var lastWrite = s.Buffer?.LastWriteAtUtc ?? DateTime.MinValue;
            lastActivity = lastWrite == DateTime.MinValue ? s.CreatedAt.UtcDateTime : lastWrite;
        }
        var idleSeconds = Math.Max(0, (DateTime.UtcNow - lastActivity).TotalSeconds);

        // Issue #335: build the viewUrl from the resolved tailnetEndpoint and the configured
        // gatewayUrl. Format: {tailnetEndpoint}/sessions/{sid}/view?gw={gatewayBase}
        // Only set when a real (non-empty) tailnetEndpoint resolved - never a loopback lie.
        var viewUrl = "";
        if (!string.IsNullOrEmpty(tailnetEndpoint))
        {
            var sidStr = s.Id.ToString();
            var base64 = tailnetEndpoint.TrimEnd('/');
            viewUrl = !string.IsNullOrEmpty(gatewayUrl)
                ? $"{base64}/sessions/{sidStr}/view?gw={Uri.EscapeDataString(gatewayUrl)}"
                : $"{base64}/sessions/{sidStr}/view";
        }

        return new()
        {
            SessionId = s.Id.ToString(),
            DirectorId = directorId,
            Agent = s.AgentKind.ToString(),
            GroupId = s.GroupId?.ToString(),
            GroupRole = s.GroupRole,
            RepoPath = s.RepoPath,
            Status = s.Status.ToString(),
            ActivityState = s.ActivityState.ToString(),
            // SessionDto.AssessedState is intentionally left null (defaults null): the Gateway-pushed
            // "assessed state" display annotation (issue #186) was retired with the Director's overlay
            // fold (issue #1177, Phase 2.3). Readers still do "AssessedState ?? ActivityState", so a null
            // here means they fall through to the raw ActivityState - the documented steady state.
            CreatedAt = s.CreatedAt.UtcDateTime,
            TotalBufferBytes = s.Buffer?.TotalBytesWritten ?? 0,
            IsAlternateScreen = s.IsAlternateScreen,
            ClaudeSessionId = s.ClaudeSessionId,
            ClaudeTranscriptPath = s.ClaudeTranscriptPath,
            BackendType = s.BackendType.ToString(),
            DriverCapabilities = CapabilityNames(s),
            Name = s.CustomName,
            Number = s.Number,
            // Mission attachment (mission-as-first-class-unit-of-work): the link + cached display name flow
            // straight through the Gateway aggregation on the SessionDto; the RESOLVED Mission record lives
            // in the Director's MissionStore.
            MissionId = s.MissionId,
            MissionName = s.MissionName,
            SortOrder = s.SortOrder,
            StatusColor = s.StatusColor,
            LastStatusReason = s.LastStatusReason,
            BriefingState = s.BriefingState.ToString(),
            RailLine = s.LatestBriefRailLine,
            LastActivityAt = lastActivity,
            IdleSeconds = idleSeconds,
            QuietThresholdSeconds = CcDirector.Core.Wingman.TerminalStateDetector.QuietThreshold.TotalSeconds,
            VoiceMode = s.VoiceMode,
            OnHold = s.OnHold,
            PendingDeletion = s.PendingDeletion,
            DeletionReason = s.DeletionReason,
            WingmanEnabled = s.WingmanEnabled,
            // Scheduled-run auto-dismiss (issue #1200): the flag + the agent's parsed verdict ride the
            // snapshot/delta path up to the Gateway's auto-dismiss sweep, which closes the session over the
            // stream on "done". Reported straight from the Session; the Gateway is the actor.
            AutoDismiss = s.AutoDismiss,
            DismissVerdict = s.DismissVerdict,
            // Raw local facts for the Gateway color fold (issue #1177, Phase 2). Reported straight from
            // the Session; the Director does NOT fold them into a color here (StatusColor is unchanged).
            IsBrandNew = s.IsBrandNew,
            IsControlled = s.IsControlled,
            ControllerSessionId = s.ControllerSessionId?.ToString(),
            // Automatic session roles (chunk 2.5): the sticky explicit role, so the Gateway aggregation can
            // apply the explicit-wins precedence. The RESOLVED SessionRole is computed at the aggregation.
            ExplicitRole = s.ExplicitRole,
            // Chunk 3: the auto-vs-explicit name marker (a future auto-rename gates on it).
            IsAutoNamed = s.IsAutoNamed,
            IsBackgroundRunning = s.IsBackgroundRunning,
            // Issue #1177 (Phase 2): the two Director-baked overlays that previously reached the Gateway
            // ONLY via the cooked StatusColor. Now reported as raw facts so the Gateway fold reproduces
            // them (desktop-dictation orange, legacy auto-explain yellow) without reading StatusColor.
            IsTranscribing = s.IsTranscribing,
            IsAutoExplaining = s.IsExplaining,
            // DevThrottle Stats: the per-session input tally, taken at the choke point. Null when empty so a
            // fresh session does not bloat every snapshot; the Gateway aggregates the non-null tallies.
            InputStats = s.InputStats.IsEmpty ? null : s.InputStats.Snapshot(),
            RemoteRepo = s.RemoteRepo ?? "",
            RemoteThreadUrl = s.RemoteThreadUrl ?? "",
            RemoteRunUrl = s.RemoteRunUrl ?? "",
            RemoteRunStatus = s.RemoteRunStatus ?? "",
            // Issue #335: identity fields populated by the Director (not patched by the Gateway).
            MachineName = machineName,
            User = user,
            TailnetEndpoint = tailnetEndpoint,
            ViewUrl = viewUrl,
        };
    }

    /// <summary>
    /// Issue #335: resolve the Director's own identity (machineName, user, tailnetEndpoint)
    /// at request time. MachineName and User come from the environment (always known).
    /// TailnetEndpoint comes from the provided resolver - empty when unresolved (Tailscale
    /// not running, no override configured). Never throws.
    /// </summary>
    private static (string MachineName, string User, string TailnetEndpoint) ResolveDirectorIdentity(
        Func<TailnetEndpointResolution> resolver)
    {
        var machineName = Environment.MachineName;
        var user = Environment.UserName;
        var resolution = resolver();
        var tailnetEndpoint = resolution.IsResolved ? resolution.Endpoint : "";
        return (machineName, user, tailnetEndpoint);
    }

    /// <summary>
    /// Convert the agent-agnostic conversation history into the legacy turn widget shape consumed by
    /// the Gateway Wingman voice path. Assistant text must stay Kind="Text": that is the contract
    /// WingmanTranslator uses to find the latest reply to summarize and speak.
    /// </summary>
    // Internal (not private) so the extracted SessionReadExecutor.Turns core can call the SAME widget
    // builder the REST route used, keeping the tunnel verb and the route byte-identical (Gateway Cleanup
    // mission, Phase 0).
    internal static List<TurnWidgetDto> BuildTurnWidgetsFromHistory(ConversationHistory history)
    {
        var widgets = new List<TurnWidgetDto>();
        foreach (var message in history.Messages)
        {
            foreach (var part in message.Parts)
            {
                var text = part.Text?.Trim();
                if (string.IsNullOrEmpty(text)) continue;

                widgets.Add(part.Kind switch
                {
                    ConversationPartKind.Text when message.Role == ConversationRole.Assistant => new TurnWidgetDto
                    {
                        Kind = "Text",
                        Header = "Agent",
                        Content = text,
                    },
                    ConversationPartKind.Text => new TurnWidgetDto
                    {
                        Kind = "UserMessage",
                        Header = "You",
                        Content = text,
                    },
                    ConversationPartKind.Thinking => new TurnWidgetDto
                    {
                        Kind = "Thinking",
                        Header = "Thinking",
                        Content = text,
                    },
                    ConversationPartKind.ToolUse => new TurnWidgetDto
                    {
                        Kind = "GenericTool",
                        Header = part.ToolName ?? "Tool",
                        Content = text,
                        ToolUseId = part.ToolId ?? "",
                    },
                    ConversationPartKind.ToolResult => new TurnWidgetDto
                    {
                        Kind = "GenericTool",
                        Header = "Tool result",
                        Content = text,
                        ToolUseId = part.ToolId ?? "",
                    },
                    _ => new TurnWidgetDto
                    {
                        Kind = part.Kind.ToString(),
                        Header = part.Kind.ToString(),
                        Content = text,
                    },
                });
            }
        }

        return widgets;
    }

    /// <summary>
    /// Compute the current turn count from the session's linked JSONL file.
    /// Returns 0 if the session isn't linked or the file isn't there yet.
    /// Used by the recap endpoints to compute the IsStale flag.
    /// </summary>
    // Gateway Cleanup Phase 0: internal so the shared SessionReadExecutor.Recap core (which the tunnel
    // recap verb and the re-pointed GET /sessions/{sid}/recap route both call) reads the SAME turn count.
    internal static int ComputeTurnCount(Session session)
    {
        if (string.IsNullOrEmpty(session.ClaudeSessionId)) return 0;
        try
        {
            var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
            if (!File.Exists(jsonl)) return 0;
            var messages = StreamMessageParser.ParseFile(jsonl);
            return WidgetBuilder.BuildFromMessages(messages).Count;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ControlEndpoints] ComputeTurnCount FAILED: {ex.Message}");
            return 0;
        }
    }

    /// <summary>Only allow same-origin path redirects (defense against open-redirect).</summary>
    private static bool IsSafeRedirect(string next)
    {
        return !string.IsNullOrEmpty(next)
            && next.StartsWith("/", StringComparison.Ordinal)
            && !next.StartsWith("//", StringComparison.Ordinal);
    }

    // ===== Screenshots gallery helpers =====

    private static readonly string[] ScreenshotExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    /// <summary>
    /// Newest-N cap applied to GET /screenshots when the client sends no ?count. The gallery
    /// only ever shows the newest few screenshots; older files are deliberately never listed.
    /// Internal (not private) so the endpoint tests assert against the same number.
    /// </summary>
    internal const int DefaultScreenshotCount = 100;

    /// <summary>Image files in the screenshots folder, newest first. Empty if the folder is missing.</summary>
    /// <summary>
    /// All image files in the screenshots folder, newest first, in ONE filesystem pass.
    /// DirectoryInfo.EnumerateFiles yields FileInfo objects pre-populated from the directory
    /// enumeration data, so sorting and reading LastWriteTime/Length costs no extra stat
    /// calls. The previous string-path version stat'ed every file twice (sort + FileInfo),
    /// which took ~100ms on a 1400-file folder; this takes ~1-2ms.
    /// </summary>
    // Internal (Gateway Cleanup Phase 0) so the screenshots-list byte verb enumerates the same folder this
    // REST route does - one enumerator, no drift.
    internal static List<FileInfo> ScreenshotFiles(string directory)
    {
        if (!Directory.Exists(directory))
            return new List<FileInfo>();
        return new DirectoryInfo(directory)
            .EnumerateFiles()
            .Where(f => ScreenshotExtensions.Contains(f.Extension.ToLowerInvariant()))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
    }

    /// <summary>
    /// Resolve a client-supplied screenshot name to an absolute path INSIDE the screenshots
    /// folder, or null if it escapes the folder, is not a bare file name, has a non-image
    /// extension, or does not exist. Defends GET/DELETE /screenshots/file against traversal.
    /// </summary>
    // Internal (Gateway Cleanup Phase 0) so the screenshot-file stream verb resolves the same traversal-safe
    // path this REST route does - one resolver, no drift.
    internal static string? ResolveScreenshot(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        // Reject anything that isn't a bare file name (no separators, no "..").
        if (Path.GetFileName(name) != name)
            return null;
        if (!ScreenshotExtensions.Contains(Path.GetExtension(name).ToLowerInvariant()))
            return null;

        var dir = CcStorage.Screenshots();
        var full = Path.GetFullPath(Path.Combine(dir, name));
        // Confirm the resolved path is still under the screenshots folder.
        var dirFull = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.StartsWith(dirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(full) ? full : null;
    }

    internal static string ScreenshotContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "application/octet-stream",
    };

    /// <summary>
    /// The single extension -> HTTP content-type map shared by the top-level <c>GET /file</c> and the
    /// session-scoped <c>GET /sessions/{sid}/file</c> (Local Files mission). One map, two routes, so a
    /// new type is added in exactly one place. The octet-stream fallback means an unknown extension is
    /// served as an opaque download, never guessed as text or an image.
    /// </summary>
    internal static string FileContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".pdf"            => "application/pdf",
        ".png"            => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif"            => "image/gif",
        ".svg"            => "image/svg+xml",
        ".css"            => "text/css; charset=utf-8",
        ".js"             => "text/javascript; charset=utf-8",
        ".json"           => "application/json; charset=utf-8",
        ".csv"            => "text/csv; charset=utf-8",
        ".md" or ".txt" or ".log" => "text/plain; charset=utf-8",
        _                 => "application/octet-stream",
    };
}
