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

        // ===== Fleet messaging (issue #705) - CUT RESTORATION (SB-4a) =====
        // Gateway Cleanup mission: these four routes were deleted at the cut but are RESTORED to the Director's
        // LOOPBACK floor, Phase-4-DEFERRED exactly like the config surface. cc-devthrottle (tools/cc-devthrottle)
        // calls them on the LOCAL Director to coordinate the fleet, so the cut broke our own channel. They are
        // loopback-only and relay OUTBOUND (Director -> Gateway) for non-local targets, so the INBOUND port
        // stays closed - the security win is preserved. A session only ever reaches its OWN Director
        // (CC_DIRECTOR_API); it never holds the Gateway URL or the fleet token, so this Director forwards to the
        // Gateway using the token it already holds. Restored verbatim from the pre-cut ControlEndpoints; only
        // /fleet/broadcast is NOT restored (Gateway-native + Hub-gated, issue #1229).

        // Resolve the sender's display name from THIS Director's own session record (never trusted from the
        // request body) and build the framed message the recipient sees.
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

        // Identity-stamp a session DTO with this Director's machine/user/tailnet endpoint (fleet directory rows).
        SessionDto MapWithIdentity(Session s, TurnSummaryCache? cache = null)
        {
            var (mn, usr, ep) = resolveTailnetEndpoint is not null
                ? ResolveDirectorIdentity(resolveTailnetEndpoint)
                : (string.Empty, string.Empty, string.Empty);
            return Map(s, directorId, cache, mn, usr, ep, gatewayUrl);
        }

        // Create a session LOCALLY through the shared SessionCommandExecutor (issue #1177 Phase 1), then build
        // the identity-stamped 201 response. Used by the local branch of POST /fleet/spawn; the Machine routing
        // field is advisory here (the routing decision is made before the request reaches this Director).
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

        // Deliver a framed message to a LOCAL session, wait for it to settle back to Idle, and return its answer
        // (transcript-first, buffer-scrape fallback). Used by the standalone branch of POST /fleet/ask.
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
            // scrape of the repainting TUI buffer. A transcript can flush several seconds after Idle, so
            // poll for an assistant message BEYOND preAssistantCount for up to ~25 s; fall back to the
            // buffer scrape only if no new answer appears (e.g. a turn that produced only tool calls).
            if (supportsTranscript)
            {
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

        // POST /fleet/rename - rename a session anywhere in the fleet (issue #1490). A local target is renamed
        // directly; a remote target is relayed through the Gateway (PATCH /sessions/{sid}), which routes the
        // rename to the owning Director over the tunnel. Restores the CLI `session rename` off the PATCH
        // /sessions/{sid} route the tunnel-only cut removed from the Director floor. Fails loud (404) for an
        // unknown target with no Gateway - never a silent no-op.
        app.MapPost("/fleet/rename", async (FleetRenameRequest req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.ToSessionId))
                return Results.BadRequest(new { error = "toSessionId is required" });
            if (!Guid.TryParse(req.ToSessionId, out var toGuid))
                return Results.BadRequest(new { error = "invalid toSessionId format" });

            var local = sessionManager.GetSession(toGuid);
            if (local is not null)
            {
                if (!sessionManager.RenameSession(toGuid, req.Name))
                    return Results.Json(
                        new FleetRenameResponse { Renamed = false, SessionId = req.ToSessionId, Error = "rename failed" },
                        statusCode: StatusCodes.Status500InternalServerError);
                var dto = MapWithIdentity(local, turnSummaryCache);
                return Results.Json(new FleetRenameResponse { Renamed = true, SessionId = dto.SessionId ?? "", Name = dto.Name ?? "" });
            }

            var gw = gatewayClientProvider?.Invoke();
            if (gw is not { IsEnabled: true })
                return Results.Json(new FleetRenameResponse
                {
                    Renamed = false, SessionId = req.ToSessionId,
                    Error = "Session not found on this Director and no Gateway is configured.",
                }, statusCode: StatusCodes.Status404NotFound);

            try
            {
                var dto = await gw.RenameFleetAsync(req.ToSessionId, req.Name, ct);
                return Results.Json(new FleetRenameResponse { Renamed = true, SessionId = dto.SessionId ?? "", Name = dto.Name ?? "" });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] /fleet/rename relay to {toGuid} FAILED: {ex.Message}");
                return Results.Json(new FleetRenameResponse
                {
                    Renamed = false, SessionId = req.ToSessionId,
                    Error = $"Cannot rename the target via the Gateway: {ex.Message}",
                }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // POST /fleet/done - flag a session anywhere in the fleet for teardown (issue #1490). A local target is
        // flagged directly (its Director's reaper removes it after the grace window); a remote target is relayed
        // through the Gateway (POST /sessions/{sid}/request-deletion). Restores `cc-devthrottle session done` -
        // how an unattended run tears ITSELF down - off the route the tunnel-only cut removed. Fails loud (404)
        // for an unknown target with no Gateway.
        app.MapPost("/fleet/done", async (FleetDoneRequest req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.ToSessionId))
                return Results.BadRequest(new { error = "toSessionId is required" });
            if (!Guid.TryParse(req.ToSessionId, out var toGuid))
                return Results.BadRequest(new { error = "invalid toSessionId format" });

            var local = sessionManager.GetSession(toGuid);
            if (local is not null)
            {
                local.MarkForDeletion(req.Reason);
                return Results.Json(new FleetDoneResponse { Accepted = true });
            }

            var gw = gatewayClientProvider?.Invoke();
            if (gw is not { IsEnabled: true })
                return Results.Json(new FleetDoneResponse
                {
                    Accepted = false,
                    Error = "Session not found on this Director and no Gateway is configured.",
                }, statusCode: StatusCodes.Status404NotFound);

            try
            {
                await gw.RequestDeletionFleetAsync(req.ToSessionId, req.Reason, ct);
                return Results.Json(new FleetDoneResponse { Accepted = true });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] /fleet/done relay to {toGuid} FAILED: {ex.Message}");
                return Results.Json(new FleetDoneResponse
                {
                    Accepted = false,
                    Error = $"Cannot reach the target via the Gateway: {ex.Message}",
                }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

    }

    // Gateway Cleanup CUT RESTORATION (SB-4a): the fleet-directory identity stamp. Restored with the /fleet
    // routes (MapWithIdentity uses it) so a fleet/sessions row carries this Director's machine/user/tailnet
    // endpoint. The tailnet endpoint is resolved through the injected resolver (empty when unresolved).
    private static (string MachineName, string User, string TailnetEndpoint) ResolveDirectorIdentity(
        Func<TailnetEndpointResolution> resolver)
    {
        var machineName = Environment.MachineName;
        var user = Environment.UserName;
        var resolution = resolver();
        var tailnetEndpoint = resolution.IsResolved ? resolution.Endpoint : "";
        return (machineName, user, tailnetEndpoint);
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
