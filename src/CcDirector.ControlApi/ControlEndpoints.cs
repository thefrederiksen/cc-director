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
using CcDirector.Core.Security;
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
    public static void Map(IEndpointRouteBuilder app, SessionManager sessionManager, string directorId, string version, Func<Task> requestShutdownAsync, bool authEnabled = false, RepositoryRegistry? repositoryRegistry = null, TurnSummaryCache? turnSummaryCache = null, string? gatewayUrl = null, ProactiveExplainService? proactiveExplain = null, GatewayConnectionMonitor? gatewayMonitor = null, Func<TailnetEndpointResolution>? resolveTailnetEndpoint = null, Func<GatewayClient?>? gatewayClientProvider = null, MessageSteward? messageSteward = null, MissionStore? missionStore = null, Func<CancellationToken, Task<SignedInUser?>>? signedInUserResolver = null, Core.Git.RepositoryMonitor? repositoryMonitor = null)
    {
        // ===== Healthz =====
        // The one route reachable without a credential, so its unauthenticated answer says ONLY that
        // something is alive here. It used to answer everybody with this Director's identifier, the
        // machine's name, the product version and a live session count - configuration, handed out
        // on the single route designed to ask for nothing, and readable by any local process or any
        // page that could reach loopback.
        //
        // A caller that DID authenticate still gets the full answer, because the launcher's update
        // check reads the version and the session count from here to decide whether a swap would
        // interrupt live work, and the startup self-probe reads the identifier to prove no other
        // service is shadowing the bound port. Trimming the body for everyone would have broken both
        // silently; trimming it for callers who present nothing costs neither of them anything.
        app.MapGet("/healthz", (HttpContext ctx) =>
        {
            // With authentication switched off there is no credential to present, so there is no
            // caller to trim for: the host is embedded somewhere that already established who it is
            // talking to, and trimming would only break that caller.
            var callerHasFullAuthority = !authEnabled
                || DirectorAuth.PrincipalOf(ctx) is { Scope: ControlApiScope.Full };
            if (!callerHasFullAuthority)
                return Results.Json(new { status = "ok" });

            return Results.Json(new HealthDto
            {
                Status = "ok",
                Directors = 1,
                Sessions = sessionManager.ListSessions().Count,
                Version = version,
                ServerTime = DateTime.UtcNow,
                DirectorId = directorId,
                MachineName = Environment.MachineName,
            });
        });

        // ===== Prompts that did not go (issue internal#811) =====
        // The question the issue is named after - "how often is this happening?" - answered without
        // grepping a log file. Every session's row already carries its own counts and shows the loud
        // badge; this is the FLEET view for a person or an agent asking the Director directly.
        //
        // In-memory and process-lifetime, exactly like the browser error ring: the FileLog line remains
        // the durable record, and an empty list means nothing has failed since this Director started - it
        // never means nothing ever failed. Carries no prompt text, only its length.
        app.MapGet("/prompt-delivery-failures", (int? count) =>
        {
            var max = count is > 0 ? count.Value : 50;
            var recent = CcDirector.Core.Input.PromptDeliveryFailures.Recent(max);
            return Results.Json(new
            {
                failedDeliveries = recent.Count(r => r.Kind == "failed-delivery"),
                composerEchoMisses = recent.Count(r => r.Kind == "composer-echo-miss"),
                note = "In memory since this Director started. The durable record is the Director log.",
                recent = recent.Select(r => new
                {
                    atUtc = r.AtUtc,
                    sessionId = r.SessionId.ToString(),
                    kind = r.Kind,
                    source = r.Source,
                    reason = r.Reason,
                    textLength = r.TextLength,
                }),
            });
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
            //
            // BuildForSession, not Build: this is a live launch, so it must honour the user's choice
            // of whose text is injected. Build renders the DevThrottle default regardless, which is
            // only correct for previewing ours.
            try
            {
                var text = FleetPreamble.BuildForSession(
                    session.Id.ToString(), name, Environment.MachineName, session.RepoPath, user,
                    workflowIndex: new WorkflowIndexStore(), skillIndex: new SkillIndexStore());
                text = AppendSeatParagraph(text, session);
                return Results.Text(text, "text/plain");
            }
            catch (Exception ex) when (ex is InjectedTextUnavailableException or FleetPreambleTemplateException)
            {
                // The user's text is live but unreadable, or was edited on disk into something that
                // cannot render. Do NOT substitute ours - they turned ours off. An empty body means the
                // hook injects nothing, so the session still starts and the agent simply has no
                // preamble, rather than silently receiving the text (and the policy) they declined.
                //
                // This catches the render failure too, not just the read failure: an uncaught one would
                // become a 500 WITH A BODY, and the macOS/Linux hook pipes this response straight to
                // stdout - so an error page would arrive in the agent's context dressed as the
                // preamble. An empty body is the only thing that reliably means "nothing".
                FileLog.Write($"[ControlEndpoints] fleet-preamble unavailable for {sid}: {ex.Message}");
                return Results.Text("", "text/plain");
            }
        });

        // The fleet preamble pre-wrapped as ready-to-print SessionStart hook output. The
        // macOS/Linux shell hook cannot safely BUILD JSON (escaping arbitrary preamble text in
        // POSIX shell), so the Director serializes the whole hookSpecificOutput envelope and the
        // script just prints this response body to stdout. Empty body when there is no preamble,
        // so the hook emits nothing rather than an empty envelope.
        app.MapGet("/sessions/{sid}/fleet-preamble-hook-output", async (string sid, CancellationToken ct) =>
        {
            if (!Guid.TryParse(sid, out var guid))
                return Results.BadRequest(new { error = "invalid session id format" });

            var session = sessionManager.GetSession(guid);
            if (session is null)
                return Results.NotFound(new { error = "session not found" });

            var name = SessionName.DisplayName(session.CustomName,
                SessionName.FolderName(session.RepoPath),
                SessionName.Disambiguator(session.Id));

            // Issue #1357: this path silently omitted the signed-in user, so Claude on macOS and Linux
            // never got the identity line that Windows got - the same text built two ways, one of them
            // wrong. Resolving the user here (which is why this handler is now async) makes the two
            // platforms agree.
            SignedInUser? user = signedInUserResolver is null ? null : await signedInUserResolver(ct);

            string text;
            try
            {
                text = FleetPreamble.BuildForSession(
                    session.Id.ToString(), name, Environment.MachineName, session.RepoPath, user,
                    workflowIndex: new WorkflowIndexStore(), skillIndex: new SkillIndexStore());
                text = AppendSeatParagraph(text, session);
            }
            catch (Exception ex) when (ex is InjectedTextUnavailableException or FleetPreambleTemplateException)
            {
                // See the sibling endpoint: the user's text is live but unreadable or unrenderable, so
                // we inject nothing rather than substituting the text they declined - and an empty body
                // rather than an error body, because this response is piped straight to the hook's
                // stdout.
                FileLog.Write($"[ControlEndpoints] fleet-preamble-hook-output unavailable for {sid}: {ex.Message}");
                return Results.Text("", "text/plain");
            }

            // BuildForSession already collapses whitespace-only text to empty, so this agrees with the
            // sibling endpoint and with Pi by construction rather than by coincidence.
            if (string.IsNullOrEmpty(text))
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

        // ===== Update status (issue #1030) =====
        //
        // What this machine knows about its own updates: the running version, when it last looked, what
        // that look concluded, and what the launcher decided to do about anything it downloaded. The
        // same folded answer the desktop window renders - one fold, so nothing can say two things.
        //
        // The body carries FINISHED text and the finished list of available actions, not raw state for a
        // caller to interpret (critical rule 7). A caller that wanted to group machines can key on
        // "state"; a caller that wants to SHOW something uses the words that are already here.
        app.MapGet("/update/status", () =>
        {
            var status = CcDirector.Core.Update.UpdateStatusBoard.Current();
            if (status is null)
            {
                // Startup has not built the updater yet. Say that, rather than answering "up to date"
                // for a machine that has not looked - the exact confusion this endpoint exists to end.
                FileLog.Write("[ControlEndpoints] GET update/status before the updater was registered");
                return Results.Json(new { ready = false, reason = "the updater has not started yet" }, statusCode: 503);
            }

            return Results.Json(new
            {
                ready = true,
                state = status.State,
                headline = status.Headline,
                detail = status.Detail,
                tooltip = status.Tooltip,
                accent = status.Accent,
                background = status.Background,
                border = status.Border,
                icon = status.Icon,
                busy = status.Busy,
                percentComplete = status.PercentComplete,
                canCheckNow = status.CanCheckNow,
                checkNowLabel = status.CheckNowLabel,
                canInstallNow = status.CanInstallNow,
                installNowLabel = status.InstallNowLabel,
            });
        });

        // Run a check on demand and answer with what it CONCLUDED plus the refolded status, so the
        // caller learns the result of the thing it asked for rather than having to poll and guess.
        app.MapPost("/update/check", async (CancellationToken ct) =>
        {
            FileLog.Write("[ControlEndpoints] POST update/check");
            var outcome = await CcDirector.Core.Update.UpdateStatusBoard.CheckNowAsync(ct);
            if (outcome is null)
                return Results.Json(new { ok = false, reason = "the updater has not started yet" }, statusCode: 503);

            var status = CcDirector.Core.Update.UpdateStatusBoard.Current();
            return Results.Json(new
            {
                ok = true,
                outcome = outcome.Value.ToString(),
                state = status?.State,
                headline = status?.Headline,
                detail = status?.Detail,
            });
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
        async Task<IResult> CreateLocalSessionAsync(NewSessionRequest? req, Func<Session, Task>? onCreated = null)
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
            if (session is null)
                return Results.Problem("created session not found", statusCode: 500);
            // Post-create hook (Workflows mission, phase 5b): lets the caller run follow-up work that
            // needs the CREATED session - recording the run participant - without re-plumbing the
            // create result. The session exists whether or not the hook succeeds; the hook owns its
            // own error posture.
            if (onCreated is not null)
                await onCreated(session);
            return Results.Json(MapWithIdentity(session, turnSummaryCache), statusCode: 201);
        }

        // Deliver a framed message to a LOCAL session, wait for the turn to SETTLE, and return its answer
        // (transcript-first, buffer-scrape fallback). Used by the standalone branch of POST /fleet/ask.
        //
        // Defect 10, the surviving half. This used to wait on ActivityState.Idle - a state NOTHING has ever
        // written. The auto-drain that Idle was invented for was deleted by #1564 (its own tests had passed
        // for fourteen months by calling ApplyTerminalActivityState(Idle) directly, injecting a state
        // production never emits), but THIS reader was left behind, still waiting for it. So the loop always
        // ran the full timeout and `timedOut` was always true: the ask-and-wait verb - "ask a session and
        // wait for its answer" - could never observe the answer it was waiting for, burned its whole timeout
        // every single time, and always reported a timeout.
        //
        // THE LESSON, because it is the one this codebase keeps re-learning: #1564 fixed the half it could
        // see and left a reader waiting on a state it had just finished proving dead. A DEAD STATE IS NOT
        // DEAD UNTIL ITS LAST READER IS GONE.
        //
        // It now waits on what the sensor ACTUALLY emits (docs/new_architecture/session-state.html, the
        // sensor's state machine): a turn ends by settling to WaitingForInput. Exited ends the wait too, and
        // deliberately - a target that dies mid-answer never reaches WaitingForInput, so without it the loop
        // would burn the full timeout again for exactly the same reason, which is this defect wearing a new
        // state's name.
        //
        // THE WIRE WORD "idle" STAYS, and that is not an oversight. It is the established value of
        // FleetAskResponse.Status ("idle (answered) | timeout | failed | not_found", FleetMessageRequest.cs)
        // and the Gateway's own relayed ask already produces it - GatewayEndpoints maps
        // `"Idle" or "WaitingForInput" => "idle"` and `"Exited" or "Failed" => "failed"`. So on the wire
        // "idle" has always meant "the turn settled", NOT ActivityState.Idle, and the Gateway had already
        // converged on WaitingForInput meaning exactly that. Renaming it here alone would make a local ask
        // and a relayed ask answer differently for the same outcome - one more disagreement of the kind this
        // whole mission exists to remove. The states below mirror that relay mapping deliberately.
        static string WaitOutcome(Session s) => s.ActivityState switch
        {
            ActivityState.WaitingForInput => "idle",   // the turn settled; the answer is there to read
            ActivityState.Exited => "failed",          // died before answering - matches the relay's mapping
            _ => "timeout",                            // still working when the clock ran out
        };
        static bool Settled(Session s) => WaitOutcome(s) != "timeout";

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

            await target.SendTextAsync(framed, SendSource.Agent);

            // Give the target a moment to start working, then wait for the turn to settle. Both the loop and
            // the verdict read the SAME predicate, so they cannot drift apart - which is how the old pair
            // managed to disagree with reality in lockstep.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Task.Delay(300, ct);
            while (sw.ElapsedMilliseconds < timeoutMs && !Settled(target))
                await Task.Delay(250, ct);

            var outcome = WaitOutcome(target);
            var answer = "";

            // Prefer the transcript: a clean, parsed answer (the NEW assistant message) instead of a
            // scrape of the repainting TUI buffer. A transcript can flush several seconds after the turn
            // settles, so
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

            return (answer, outcome);
        }

        // GET /fleet/sessions - the fleet directory, and the roster EVERY cc-devthrottle verb resolves a
        // target against before it sends anything. That makes it the reap floor: a session this roster
        // omits cannot be named, so it cannot be interrupted, messaged or marked done. Issue #1019 - a card
        // that no tool could remove, with restarting the Director as the only remedy - was two independent
        // omissions from this one list.
        //
        // Issue #1051 - the other end of the same defect. The relayed roster DROPS an unreachable Director's
        // sessions and still answers 200, so a caller cannot tell "that Director has no sessions" from "I
        // could not reach it": absent reads identical to empty. `?envelope=true` answers with the roster AND
        // a finished completeness verdict, so a caller that must not mistake a partial list for a whole one
        // can say so. The bare array stays the default, because a Director that has not restarted yet still
        // serves the old shape and the tools must keep working against it.
        app.MapGet("/fleet/sessions", async (bool? envelope, CancellationToken ct) =>
        {
            // Omission one: EVERY session this Director is still holding belongs on the roster, including
            // crashed and exited-pending-reap rows. This store is the same store the desktop rail renders,
            // and a crashed session is deliberately KEPT in it (issue #959) so the user sees that work
            // stopped - in ActivityState.Exited, because a crash was never modelled as its own state. The
            // filter that used to sit here dropped exactly that state, so the one row the user could SEE
            // and most needed to clear was the one row the CLI could not NAME. The reaper on the other
            // side was always willing: /fleet/done finds a session in this store and ReapPendingDeletions
            // removes an Exited one. Only the naming was impossible.
            var own = sessionManager.ListSessions()
                .Select(s => MapWithIdentity(s, turnSummaryCache))
                .ToList();

            var gw = gatewayClientProvider?.Invoke();
            if (gw is { IsEnabled: true })
            {
                try
                {
                    // Omission two: the relayed list silently DROPS the sessions of a Director the Gateway
                    // cannot reach while still returning 200 (see GatewayClient.ListFleetSessionsAsync).
                    // A Director whose registration is failing therefore vanishes from its OWN fleet
                    // listing, which is how three live local sessions came to be denied by the CLI and the
                    // Control API at once while the rail still showed them. A Director is first-hand
                    // authority on its own machine, so its rows go back in. The Gateway's copy WINS for any
                    // session it already knows: the session numbers and identity stamping it hands out are
                    // never overwritten here.
                    //
                    // Read posture, NOT the reaper's. The strict ListFleetSessionsWithReachabilityAsync throws
                    // when the Gateway cannot vouch for completeness, which is correct for something that
                    // deletes directories and wrong here: it would take `session list`, every target resolve,
                    // cc-status and cc-history down against a version-skewed Gateway. This one returns null
                    // reachability for "the Gateway did not say" and still serves the rows.
                    var (fleet, reachability) = await gw.ReadFleetSessionsWithOptionalReachabilityAsync(ct);
                    var (roster, restored) = UnionOwnSessions(fleet, own);
                    if (restored.Count > 0)
                        FileLog.Write($"[ControlEndpoints] /fleet/sessions: the Gateway roster omitted {restored.Count} of this Director's own session(s); served from the local store: {string.Join(", ", restored)}");

                    if (envelope != true)
                        return Results.Json(roster);

                    // The verdict is FOLDED HERE, not in the client. A caller must never have to decide what
                    // "offline" means for completeness - that is the Gateway-owns-ruling rule, and the reason
                    // the desktop rail renders pushed display state verbatim. The tools print these strings.
                    //
                    // Null reachability means the Gateway would not say, so rosterComplete is reported as NULL
                    // - explicitly UNKNOWN, never true. Coalescing unknown to complete here would rebuild the
                    // exact defect this issue is about, one layer up: absent reading identical to empty.
                    bool? complete = null;
                    string? incompleteReason = null;
                    if (reachability is not null)
                    {
                        var folded = RosterCompleteness.Fold(reachability);
                        complete = folded.Complete;
                        incompleteReason = folded.Reason;
                    }
                    else
                    {
                        FileLog.Write("[ControlEndpoints] /fleet/sessions?envelope=true: the Gateway supplied no reachability; reporting completeness as UNKNOWN");
                    }

                    return Results.Json(new
                    {
                        sessions = roster,
                        directors = reachability,
                        rosterComplete = complete,
                        rosterIncompleteReason = incompleteReason,
                        // The caution a tool prints ONLY when its own answer came back empty - a machine that
                        // is connected but has not reported recently could be hiding what was asked for. It is
                        // folded here like every other verdict, and it is a separate field rather than part of
                        // rosterIncompleteReason precisely because it must not be printed on a positive answer.
                        rosterStaleAnswerCaution = RosterCompleteness.StaleAnswerCaution(reachability),
                    });
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[ControlEndpoints] /fleet/sessions relay FAILED: {ex.Message}");
                    return Results.Json(new { error = $"Cannot reach the Gateway: {ex.Message}" },
                        statusCode: StatusCodes.Status502BadGateway);
                }
            }

            if (envelope != true)
                return Results.Json(own);

            // Standalone: this Director is the whole fleet and it can see all of itself, so the roster is
            // complete by construction. Saying so positively matters - a caller that treats "no reachability
            // reported" as "might be incomplete" would otherwise warn forever on a single-machine setup.
            return Results.Json(new
            {
                sessions = own,
                directors = Array.Empty<DirectorReachabilityDto>(),
                rosterComplete = true,
                rosterIncompleteReason = (string?)null,
            });
        });

        // GET /fleet/repositories + /fleet/worktrees - the repository/worktree directory (#510 phase C).
        // With a Gateway, relay its fleet-wide aggregation; standalone, serve this Director's own
        // monitor model. Read-only: reaping always runs on the owning Director with a live re-verify.
        List<RepoStatusDto> LocalRepoDtos() =>
            (repositoryMonitor?.Snapshot() ?? (IReadOnlyList<Core.Git.RepositoryStatus>)Array.Empty<Core.Git.RepositoryStatus>())
                .Select(s => RepositoryDtoMapper.Map(s, directorId, Environment.MachineName))
                .ToList();

        app.MapGet("/fleet/repositories", async (CancellationToken ct) =>
        {
            var gw = gatewayClientProvider?.Invoke();
            if (gw is { IsEnabled: true })
            {
                try
                {
                    var fleet = await gw.ListFleetRepositoriesAsync(ct);
                    // null = the Gateway is older and has no /repositories yet - serve this
                    // Director's own model (version tolerance, not outage-hiding: a DOWN Gateway
                    // still fails loud below).
                    if (fleet != null)
                        return Results.Json(fleet);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[ControlEndpoints] /fleet/repositories relay FAILED: {ex.Message}");
                    return Results.Json(new { error = $"Cannot reach the Gateway: {ex.Message}" },
                        statusCode: StatusCodes.Status502BadGateway);
                }
            }
            return Results.Json(LocalRepoDtos());
        });

        app.MapGet("/fleet/worktrees", async (CancellationToken ct) =>
        {
            var gw = gatewayClientProvider?.Invoke();
            if (gw is { IsEnabled: true })
            {
                try
                {
                    var fleet = await gw.ListFleetWorktreesAsync(ct);
                    if (fleet != null)
                        return Results.Json(fleet);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[ControlEndpoints] /fleet/worktrees relay FAILED: {ex.Message}");
                    return Results.Json(new { error = $"Cannot reach the Gateway: {ex.Message}" },
                        statusCode: StatusCodes.Status502BadGateway);
                }
            }
            return Results.Json(RepositoryDtoMapper.Flatten(LocalRepoDtos()));
        });

        // GET /fleet/machines - the machines this tenant can search and start things on.
        // GET /fleet/machines/{machine}/apps  - what is installed there.
        // GET /fleet/machines/{machine}/files - a filename search across its drives.
        // POST /fleet/machines/{machine}/launch - start something there.
        //
        // All four are Gateway relays with NO local fallback, and that is the point. Everything they describe
        // lives on another computer, so a Director with no Gateway has nothing truthful to answer with - an
        // empty list here would read as "you have no other machines" rather than "I cannot see them from
        // here". They fail loud instead, naming the Gateway as the missing piece.
        //
        // The two query routes pass the Gateway's answer through as raw text at its own status. This Director
        // is a hop, not a reader: it does not interpret a catalogue or a search result, so it does not
        // deserialise one and cannot drop a field a newer launcher added.
        app.MapGet("/fleet/machines", async (CancellationToken ct) =>
        {
            var gw = gatewayClientProvider?.Invoke();
            if (gw is not { IsEnabled: true })
                return Results.Json(new { error = "No Gateway is configured, so this Director cannot see other machines." },
                    statusCode: StatusCodes.Status502BadGateway);
            try
            {
                var machines = await gw.ListMachinesAsync(ct);
                if (machines is null)
                    return Results.Json(new { error = "This Gateway does not offer machine control." },
                        statusCode: StatusCodes.Status502BadGateway);
                return Results.Json(machines);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] /fleet/machines relay FAILED: {ex.Message}");
                return Results.Json(new { error = $"Cannot reach the Gateway: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        async Task<IResult> RelayMachineQueryAsync(string machine, string verb, HttpContext ctx, CancellationToken ct)
        {
            var gw = gatewayClientProvider?.Invoke();
            if (gw is not { IsEnabled: true })
                return Results.Json(new { error = $"No Gateway is configured, so this Director cannot reach '{machine}'." },
                    statusCode: StatusCodes.Status502BadGateway);

            var query = ctx.Request.Query["q"].ToString();
            _ = int.TryParse(ctx.Request.Query["limit"].ToString(), out var limit);
            _ = int.TryParse(ctx.Request.Query["timeoutMilliseconds"].ToString(), out var timeout);

            try
            {
                var (status, body) = await gw.QueryMachineAsync(machine, verb, query, limit, timeout, ct);
                return Results.Content(body, "application/json; charset=utf-8", statusCode: status);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] /fleet/machines/{machine}/{verb} relay FAILED: {ex.Message}");
                return Results.Json(new { error = $"Cannot reach the Gateway: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        }

        app.MapGet("/fleet/machines/{machine}/apps", (string machine, HttpContext ctx, CancellationToken ct) =>
            RelayMachineQueryAsync(machine, "apps", ctx, ct));

        app.MapGet("/fleet/machines/{machine}/files", (string machine, HttpContext ctx, CancellationToken ct) =>
            RelayMachineQueryAsync(machine, "files", ctx, ct));

        app.MapPost("/fleet/machines/{machine}/launch", async (string machine, MachineLaunchRequest req, CancellationToken ct) =>
        {
            var gw = gatewayClientProvider?.Invoke();
            if (gw is not { IsEnabled: true })
                return Results.Json(new { error = $"No Gateway is configured, so this Director cannot reach '{machine}'." },
                    statusCode: StatusCodes.Status502BadGateway);
            try
            {
                var (status, body) = await gw.LaunchOnMachineAsync(
                    machine, req?.Path, req?.App, req?.Args, req?.Cwd, req?.Headless ?? false, ct);
                return Results.Content(body, "application/json; charset=utf-8", statusCode: status);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] /fleet/machines/{machine}/launch relay FAILED: {ex.Message}");
                return Results.Json(new { error = $"Cannot reach the Gateway: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
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
                    // Fleet message delivery is agent-driven, not a human racing the dictation, so it is
                    // exempt from the dictation lock (issue #1181, Task 3b).
                    await local.SendTextAsync(framed, SendSource.Agent);
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
                var resp = await gw.SendPromptToFleetAsync(req.ToSessionId, framed, ct: ct);
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
                // Defect 10: the wait can now END on a target that EXITED mid-answer - before the predicate
                // was fixed it could only ever run out the clock, so this outcome was unreachable and the
                // caller never had to consider it. It is not an answer, so it must not be reported as one:
                // without this arm a dead target would return Answered=true carrying whatever the buffer
                // scrape happened to catch. Mirrors the Gateway relay's "failed" for the same state.
                if (status == "failed")
                    return Results.Json(new FleetAskResponse
                    {
                        Answered = false, Status = "failed",
                        Error = $"Session {req.ToSessionId} exited before answering.",
                    }, statusCode: StatusCodes.Status500InternalServerError);
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

            // Session origin (devthrottle_internal issue #982). This route IS the command line: it is
            // loopback-only and cc-devthrottle's `session spawn` is what reaches it, so a request that
            // named no surface came from the CLI and saying so is a measurement, not a guess. The KIND
            // is left exactly as the caller stated it - only the caller knows whether a session or a
            // person ran the command (the CLI reads CC_SESSION_ID to decide), and inventing a kind here
            // would fabricate the one number this field exists to produce. Applied to BOTH legs, since
            // the remote leg forwards this same request object through the Gateway.
            if (string.IsNullOrWhiteSpace(req.OriginSurface))
                req.OriginSurface = SessionOriginSurfaces.Cli;

            var machine = req.Machine?.Trim();
            var isLocal = string.IsNullOrEmpty(machine)
                || string.Equals(machine, "local", StringComparison.OrdinalIgnoreCase)
                || string.Equals(machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

            if (isLocal)
            {
                FileLog.Write($"[ControlEndpoints] POST /fleet/spawn: LOCAL, repo={req.RepoPath}, agent={req.Agent}");

                // Issue #1548: a mission-scoped LOCAL spawn resolves the mission NAME against the Gateway's
                // mission store - the source of truth - before handing the create request to the floor. The
                // REMOTE leg below already gets this for free: it goes out through the Gateway, whose
                // POST /machines/{machine}/sessions stamps the name for it. The local leg never touched the
                // Gateway, so a caller sending only a mission id (which is all the CLI's `session spawn
                // --mission <id>` can send) fell through to the Director's TEMPORARY local-store bridge and
                // was rejected against the wrong store - telling the human to create a mission that already
                // existed. Resolving here makes both legs consult the same store and leaves the floor
                // stamping only what create carries, which is the documented end state.
                if (req.MissionId is Guid localMissionId && string.IsNullOrWhiteSpace(req.MissionName))
                {
                    var missionGw = gatewayClientProvider?.Invoke();
                    if (missionGw is { IsEnabled: true })
                    {
                        MissionDto? mission;
                        try
                        {
                            mission = await missionGw.GetMissionAsync(localMissionId, ct);
                        }
                        catch (Exception ex)
                        {
                            // Fail loud. An unreachable Gateway is NOT an unknown mission, and reporting it as
                            // one is the exact lie this issue is about.
                            FileLog.Write($"[ControlEndpoints] POST /fleet/spawn: mission lookup {localMissionId} FAILED: {ex.Message}");
                            return Results.Json(
                                new { error = $"Cannot attach the new session to mission '{localMissionId}': the Gateway could not be reached to look up its name. {ex.Message}" },
                                statusCode: StatusCodes.Status502BadGateway);
                        }

                        if (mission is null)
                            return Results.BadRequest(new
                            {
                                error = $"unknown mission '{localMissionId}'. The Gateway has no mission with that id. "
                                      + "List the missions with: cc-devthrottle mission list",
                            });

                        FileLog.Write($"[ControlEndpoints] POST /fleet/spawn: mission {localMissionId} resolved to \"{mission.MissionName}\"");
                        req.MissionName = mission.MissionName;
                    }
                    // No Gateway configured: leave the name blank and let the floor's local-store bridge
                    // resolve it exactly as it does today. That is the only store a Gateway-less Director has.
                }

                // Workflows mission (phase 5b): resolve the SEAT for a local spawn against the Gateway's
                // run store, the same way the Gateway relay stamps a remote spawn - an explicit run id is
                // validated (unknown -> 400, unreachable Gateway -> 502, never conflated), and a
                // mission-scoped spawn with no explicit run auto-seats onto the mission's run. The run's
                // workflow id + pinned version ride the create request; the floor stamps only what create
                // carries. A Gateway-less Director seats nothing - runs live only on the Gateway.
                WorkflowRunDto? seatRun = null;
                var seatGw = gatewayClientProvider?.Invoke();
                if (seatGw is { IsEnabled: true })
                {
                    try
                    {
                        if (req.WorkflowRunId is Guid explicitRunId)
                        {
                            seatRun = await seatGw.GetWorkflowRunAsync(explicitRunId, ct);
                            if (seatRun is null)
                                return Results.BadRequest(new
                                {
                                    error = $"unknown workflow run '{explicitRunId}'. "
                                          + "List runs with: cc-devthrottle workflow runs",
                                });
                        }
                        else if (req.MissionId is Guid seatMissionId)
                        {
                            seatRun = await seatGw.GetMissionWorkflowRunAsync(seatMissionId, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        FileLog.Write($"[ControlEndpoints] POST /fleet/spawn: workflow-run lookup FAILED: {ex.Message}");
                        return Results.Json(
                            new { error = $"Cannot seat the new session on its workflow run: the Gateway could not be reached. {ex.Message}" },
                            statusCode: StatusCodes.Status502BadGateway);
                    }

                    if (seatRun is not null && !seatRun.WorkflowEnabled)
                    {
                        // The owner turned this workflow OFF: no new seats; spawn unseated, loudly.
                        FileLog.Write($"[ControlEndpoints] POST /fleet/spawn: workflow " +
                                      $"'{seatRun.WorkflowId}' is OFF - spawning UNSEATED");
                        seatRun = null;
                    }
                    if (seatRun is not null)
                    {
                        req.WorkflowRunId = seatRun.Id;
                        req.WorkflowId = seatRun.WorkflowId;
                        req.WorkflowVersion = seatRun.WorkflowVersion;
                    }
                }

                return await CreateLocalSessionAsync(req, onCreated: seatRun is null || seatGw is null
                    ? null
                    : async createdSession =>
                    {
                        // Record the created session as a run participant (persisted run-to-session
                        // membership, issue #1771). The spawn has already succeeded; a failed record is
                        // reported LOUDLY in the log rather than failing the session under the caller.
                        try
                        {
                            await seatGw.AddWorkflowRunParticipantAsync(seatRun.Id, new WorkflowRunParticipantDto
                            {
                                SessionId = createdSession.Id.ToString(),
                                AgentKind = req.Agent,
                                Role = createdSession.ExplicitRole ?? "",
                                Machine = Environment.MachineName,
                            }, ct);
                        }
                        catch (Exception ex)
                        {
                            FileLog.Write($"[ControlEndpoints] POST /fleet/spawn: run-participant record FAILED for " +
                                          $"session {createdSession.Id} on run {seatRun.Id}: {ex.Message}. The session " +
                                          "is seated and running; governance is missing this membership row.");
                        }
                    });
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

        // The session-control verbs below restore what the old REST API offered agents and the tunnel-only cut
        // took off the loopback surface. They are NOT new logic: a local target is dispatched through the SAME
        // SessionCommandExecutor the tunnel dispatches into, so the loopback path and the Gateway path run
        // identical code. A target this Director does not host is relayed through the Gateway, which routes it
        // to the owning Director over the tunnel. No Gateway and not local is a loud 404 - never a silent no-op.

        // POST /fleet/prompt - send text into a session (the old POST /sessions/{sid}/prompt). Unlike
        // /fleet/send this does NOT frame the text with a sender: it is a raw prompt, exactly what a human
        // typing into the session would produce.
        app.MapPost("/fleet/prompt", async (FleetPromptRequest req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.ToSessionId))
                return Results.BadRequest(new { error = "toSessionId is required" });
            if (string.IsNullOrEmpty(req.Text))
                return Results.BadRequest(new { error = "text is required" });
            if (!Guid.TryParse(req.ToSessionId, out var toGuid))
                return Results.BadRequest(new { error = "invalid toSessionId format" });

            if (sessionManager.GetSession(toGuid) is not null)
            {
                var command = new DirectorCommand
                {
                    Verb = "prompt",
                    SessionId = req.ToSessionId,
                    PayloadJson = JsonSerializer.Serialize(new PromptRequest { Text = req.Text, AppendEnter = req.AppendEnter }),
                };
                var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, cancellationToken: ct);
                return CommandResultToHttp(result);
            }

            var gw = gatewayClientProvider?.Invoke();
            if (gw is not { IsEnabled: true })
                return Results.Json(new { error = "Session not found on this Director and no Gateway is configured." },
                    statusCode: StatusCodes.Status404NotFound);
            try
            {
                // AppendEnter travels with the prompt: the local path above honors it, and a target on
                // another Director must not behave differently just for being there.
                await gw.SendPromptToFleetAsync(req.ToSessionId, req.Text, appendEnter: req.AppendEnter, ct: ct);
                return Results.Json(new { accepted = true, sessionId = req.ToSessionId });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] /fleet/prompt relay to {toGuid} FAILED: {ex.Message}");
                return Results.Json(new { error = $"Cannot prompt the target via the Gateway: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // POST /fleet/interrupt - stop what a session is doing (the old POST /sessions/{sid}/interrupt).
        app.MapPost("/fleet/interrupt", async (FleetTargetRequest req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.ToSessionId))
                return Results.BadRequest(new { error = "toSessionId is required" });
            if (!Guid.TryParse(req.ToSessionId, out var toGuid))
                return Results.BadRequest(new { error = "invalid toSessionId format" });

            if (sessionManager.GetSession(toGuid) is not null)
            {
                var command = new DirectorCommand { Verb = "interrupt", SessionId = req.ToSessionId };
                var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, cancellationToken: ct);
                return CommandResultToHttp(result);
            }

            var gw = gatewayClientProvider?.Invoke();
            if (gw is not { IsEnabled: true })
                return Results.Json(new { error = "Session not found on this Director and no Gateway is configured." },
                    statusCode: StatusCodes.Status404NotFound);
            try
            {
                await gw.InterruptFleetAsync(req.ToSessionId, ct);
                return Results.Json(new { accepted = true, sessionId = req.ToSessionId });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] /fleet/interrupt relay to {toGuid} FAILED: {ex.Message}");
                return Results.Json(new { error = $"Cannot interrupt the target via the Gateway: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // POST /fleet/compact - compact a session's context, and continue it (issue #2150). The rescue for a
        // session whose window is full: it can no longer read anything sent to it, so no message, no
        // interrupt and no amount of waiting reaches it - only compaction does. The call BLOCKS until the
        // tool reports the compaction finished (bounded by Session.CompactionWaitTimeout) so the answer is
        // what happened, not what was attempted; the optional continuation is submitted at that moment.
        app.MapPost("/fleet/compact", async (FleetCompactRequest req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.ToSessionId))
                return Results.BadRequest(new { error = "toSessionId is required" });
            if (!Guid.TryParse(req.ToSessionId, out var toGuid))
                return Results.BadRequest(new { error = "invalid toSessionId format" });

            if (sessionManager.GetSession(toGuid) is not null)
            {
                var command = new DirectorCommand
                {
                    Verb = "compact-context",
                    SessionId = req.ToSessionId,
                    PayloadJson = JsonSerializer.Serialize(new CompactContextRequest { ContinuePrompt = req.ContinuePrompt }),
                };
                var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, cancellationToken: ct);
                return CommandResultToHttp(result);
            }

            var gw = gatewayClientProvider?.Invoke();
            if (gw is not { IsEnabled: true })
                return Results.Json(new { error = "Session not found on this Director and no Gateway is configured." },
                    statusCode: StatusCodes.Status404NotFound);
            try
            {
                var compacted = await gw.CompactFleetAsync(req.ToSessionId, req.ContinuePrompt, ct);
                return Results.Json(compacted);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] /fleet/compact relay to {toGuid} FAILED: {ex.Message}");
                return Results.Json(new { error = $"Cannot compact the target via the Gateway: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // POST /fleet/hold - park a session, or release it (the old POST /sessions/{sid}/hold). A hold asked
        // for mid-turn is DEFERRED and applies when the turn settles (see HoldState) - the response's pending
        // flag says so, which is why it is surfaced rather than swallowed.
        //
        // THE GATEWAY OWNS HOLD. When one is configured, EVERY hold - a session holding ITSELF (always a
        // local session), a manager holding a worker, local or remote - is routed to the Gateway, because
        // only the Gateway's SnoozeRegistry records the hold, defers it while the turn runs, lands it when
        // the work settles, and expires it on the clock. A hold that stops at this Director only tints the
        // local rail mirror, which the Gateway's roster fold then overwrites back to "not held" - so it
        // evaporates within a poll. That was the agent-self-hold defect: `cc-devthrottle session hold` on a
        // LOCAL session short-circuited to a mirror-only write and never reached the registry, so it never
        // landed and never held. Only when there is NO Gateway does the local mirror become the sole owner
        // worth writing, and only then do we dispatch the hold verb here.
        app.MapPost("/fleet/hold", async (FleetHoldRequest req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.ToSessionId))
                return Results.BadRequest(new { error = "toSessionId is required" });
            if (!Guid.TryParse(req.ToSessionId, out var toGuid))
                return Results.BadRequest(new { error = "invalid toSessionId format" });

            var gw = gatewayClientProvider?.Invoke();
            var route = ChooseHoldRoute(gw is { IsEnabled: true }, sessionManager.GetSession(toGuid) is not null);

            if (route == HoldRoute.Gateway)
            {
                try
                {
                    var held = await gw!.HoldFleetAsync(req.ToSessionId, req.OnHold, req.SnoozeMinutes, ct);
                    return Results.Json(held);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[ControlEndpoints] /fleet/hold relay to {toGuid} FAILED: {ex.Message}");
                    return Results.Json(new { error = $"Cannot hold the target via the Gateway: {ex.Message}" },
                        statusCode: StatusCodes.Status502BadGateway);
                }
            }

            if (route == HoldRoute.LocalMirror)
            {
                var command = new DirectorCommand
                {
                    Verb = "hold",
                    SessionId = req.ToSessionId,
                    PayloadJson = JsonSerializer.Serialize(new HoldRequest { OnHold = req.OnHold, SnoozeMinutes = req.SnoozeMinutes }),
                };
                var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, cancellationToken: ct);
                return CommandResultToHttp(result);
            }

            return Results.Json(new { error = "Session not found on this Director and no Gateway is configured." },
                statusCode: StatusCodes.Status404NotFound);
        });

        // GET /fleet/buffer?sessionId=... - read what a session's terminal is showing (the old GET
        // /sessions/{sid}/buffer). How a manager sees what a worker is actually doing.
        app.MapGet("/fleet/buffer", async (string? sessionId, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return Results.BadRequest(new { error = "sessionId is required" });
            if (!Guid.TryParse(sessionId, out var toGuid))
                return Results.BadRequest(new { error = "invalid sessionId format" });

            if (sessionManager.GetSession(toGuid) is not null)
            {
                var command = new DirectorCommand { Verb = "buffer", SessionId = sessionId };
                var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command, cancellationToken: ct);
                return CommandResultToHttp(result);
            }

            var gw = gatewayClientProvider?.Invoke();
            if (gw is not { IsEnabled: true })
                return Results.Json(new { error = "Session not found on this Director and no Gateway is configured." },
                    statusCode: StatusCodes.Status404NotFound);
            try
            {
                var text = await gw.GetBufferFleetAsync(sessionId, ct);
                return Results.Content(text, "application/json");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlEndpoints] /fleet/buffer relay to {toGuid} FAILED: {ex.Message}");
                return Results.Json(new { error = $"Cannot read the target's buffer via the Gateway: {ex.Message}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // POST /fleet/role - declare a session's EXPLICIT role after birth. Restores the set-role verb off the
        // POST /sessions/{sid}/role route the tunnel-only cut removed, which left a running session stuck with
        // whatever role it was born with - Architect cannot be derived from the spawn graph, so there was no way
        // to make one after the fact. An unknown role is REJECTED (400) so a mistyped role never silently drops;
        // an empty role CLEARS the explicit role back to auto-derivation. A target this Director does not host
        // is relayed through the Gateway (POST /sessions/{sid}/role), which routes it to the owning Director
        // over the tunnel - the same shape as /fleet/rename. Fails loud with no Gateway; never a silent no-op.
        app.MapPost("/fleet/role", async (FleetRoleRequest req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.ToSessionId))
                return Results.BadRequest(new { error = "toSessionId is required" });
            if (!Guid.TryParse(req.ToSessionId, out var toGuid))
                return Results.BadRequest(new { error = "invalid toSessionId format" });

            // Blank clears; a non-blank value must be one of the four known roles.
            var clearing = string.IsNullOrWhiteSpace(req.Role);
            if (!clearing && !SessionRoles.IsValid(req.Role))
                return Results.BadRequest(new
                {
                    error = $"unknown role '{req.Role}'. Valid roles: {string.Join(", ", SessionRoles.All)} (or empty to clear).",
                });

            var normalized = clearing ? null : SessionRoles.Normalize(req.Role);

            var local = sessionManager.GetSession(toGuid);
            if (local is null)
            {
                // Not ours: relay through the Gateway, which routes it to the owning Director over the tunnel.
                var gw = gatewayClientProvider?.Invoke();
                if (gw is not { IsEnabled: true })
                    return Results.Json(new FleetRoleResponse
                    {
                        Applied = false, SessionId = req.ToSessionId,
                        Error = "Session not found on this Director and no Gateway is configured.",
                    }, statusCode: StatusCodes.Status404NotFound);

                try
                {
                    var relayed = await gw.SetRoleFleetAsync(req.ToSessionId, normalized, ct);
                    return Results.Json(new FleetRoleResponse
                    {
                        Applied = true,
                        SessionId = relayed.SessionId ?? req.ToSessionId,
                        ExplicitRole = relayed.ExplicitRole,
                    });
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[ControlEndpoints] /fleet/role relay to {toGuid} FAILED: {ex.Message}");
                    return Results.Json(new FleetRoleResponse
                    {
                        Applied = false, SessionId = req.ToSessionId,
                        Error = $"Cannot set the role via the Gateway: {ex.Message}",
                    }, statusCode: StatusCodes.Status502BadGateway);
                }
            }

            FileLog.Write($"[ControlEndpoints] POST /fleet/role: session={toGuid}, role={normalized ?? "(cleared)"}");
            local.SetExplicitRole(normalized);

            // Only the EXPLICIT role is reported back. The effective role folds in Worker/Manager derivation
            // from the fleet-wide spawn graph, which lives in the Gateway - this Director cannot compute it,
            // so reporting it here would always be null. Callers read it from the roster.
            var dto = MapWithIdentity(local, turnSummaryCache);
            return Results.Json(new FleetRoleResponse
            {
                Applied = true,
                SessionId = dto.SessionId ?? "",
                ExplicitRole = dto.ExplicitRole,
            });
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

        // POST /fleet/broadcast - "message send all": one message to the sender's whole TEAM, or (with
        // Everyone + a human grant + reason) the whole fleet (issue #1229). The Director narrows the recipients
        // to the sender's team HERE using the SHARED BroadcastScope - the same definition the Gateway enforces
        // as the authority, so the two cannot drift - then relays to the Gateway's /fanout. Standalone (no
        // Gateway) it delivers to the in-team sessions this Director can see. Restores `message send all`,
        // which the CLI already calls but which had no Director route on the tunnel-only floor (issue #1490).
        app.MapPost("/fleet/broadcast", async (FleetBroadcastRequest req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.BadRequest(new { error = "text is required" });
            if (string.IsNullOrWhiteSpace(req.FromSessionId))
                return Results.BadRequest(new { error = "fromSessionId is required to resolve your team" });

            var framed = FrameForSender(req.FromSessionId, req.Text);
            var gw = gatewayClientProvider?.Invoke();

            // The candidate fleet: the aggregated fleet via the Gateway, or - standalone - this Director's
            // own live sessions mapped to the same shape so one scope filter serves both paths.
            List<SessionDto> fleet;
            if (gw is { IsEnabled: true })
            {
                try
                {
                    fleet = await gw.ListFleetSessionsAsync(ct);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[ControlEndpoints] /fleet/broadcast list FAILED: {ex.Message}");
                    return Results.Json(new FleetSendResponse { Accepted = false, Error = $"Cannot reach the Gateway: {ex.Message}" },
                        statusCode: StatusCodes.Status502BadGateway);
                }
            }
            else
            {
                fleet = sessionManager.ListSessions()
                    .Where(s => s.ActivityState != ActivityState.Exited)
                    .Select(s => MapWithIdentity(s, turnSummaryCache))
                    .ToList();
            }

            var sender = fleet.FirstOrDefault(s => string.Equals(s.SessionId, req.FromSessionId, StringComparison.OrdinalIgnoreCase));
            if (sender is null)
                return Results.Json(new FleetSendResponse
                {
                    Accepted = false,
                    Error = "The broadcasting session was not found in the fleet, so its team cannot be resolved.",
                }, statusCode: StatusCodes.Status404NotFound);

            var senderScope = BroadcastScope.FromAggregatedSession(sender);
            var targetIds = fleet
                .Where(s => !string.Equals(s.SessionId, req.FromSessionId, StringComparison.OrdinalIgnoreCase)
                            && (req.Everyone || senderScope.Includes(BroadcastScope.FromAggregatedSession(s))))
                .Select(s => s.SessionId)
                .ToList();

            if (targetIds.Count == 0)
                return Results.Json(new FleetSendResponse
                {
                    Accepted = true,
                    DeliveredCount = 0,
                    Warning = req.Everyone ? "No other sessions in the fleet." : "No other sessions on your team.",
                });

            if (gw is { IsEnabled: true })
            {
                try
                {
                    // A plain team broadcast passes no reason/grant (every target is in scope). Everyone carries
                    // the reason + human grant the Hub requires to reach beyond the team.
                    var resp = await gw.FanoutToFleetAsync(targetIds, framed, req.FromSessionId,
                        req.Everyone ? req.Reason : null, req.Everyone ? req.GrantId : null, ct);
                    if (resp.Denied)
                        return Results.Json(new FleetSendResponse { Accepted = false, Error = resp.DeniedReason ?? "The broadcast was refused on scope grounds." });
                    var delivered = resp.Results.Count(r => r.Error is null);
                    return Results.Json(new FleetSendResponse { Accepted = true, DeliveredCount = delivered });
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[ControlEndpoints] /fleet/broadcast relay FAILED: {ex.Message}");
                    return Results.Json(new FleetSendResponse { Accepted = false, Error = $"Cannot broadcast via the Gateway: {ex.Message}" },
                        statusCode: StatusCodes.Status502BadGateway);
                }
            }

            // Standalone: the targets are all local sessions on this Director; deliver directly. Per-session
            // failures are logged and counted out, never silently dropped, and never abort the whole broadcast.
            var count = 0;
            foreach (var tid in targetIds)
            {
                if (Guid.TryParse(tid, out var tguid) && sessionManager.GetSession(tguid) is { } local)
                {
                    try
                    {
                        await local.SendTextAsync(framed, SendSource.Agent);
                        count++;
                    }
                    catch (Exception ex)
                    {
                        FileLog.Write($"[ControlEndpoints] /fleet/broadcast local deliver to {tid} FAILED: {ex.Message}");
                    }
                }
            }
            return Results.Json(new FleetSendResponse { Accepted = true, DeliveredCount = count });
        });

    }

    /// <summary>
    /// Map a <see cref="DirectorCommandResult"/> onto the HTTP shape the OLD REST endpoints returned, so a
    /// caller sees the same status and { error } body it always did: Ok passes the handler's own JSON body
    /// through, BadRequest/NotFound/Conflict keep their meaning, and anything else is a 500. Shared by every
    /// /fleet session-control verb so they cannot drift apart from each other or from the tunnel path.
    /// </summary>
    /// <summary>
    /// Append the workflow SEAT paragraph to a session's preamble (Workflows mission, phase 5b): a
    /// seated session is told, at launch, exactly which run it executes, at which PINNED version to
    /// fetch its conduct, and to stop rather than proceed on remembered rules if the fetch fails
    /// (the no-fallback law applied to conduct). Appended even when the preamble itself is empty -
    /// the seat is the operational fact the session was spawned FOR, not our injectable prose - but
    /// not on the unreadable-user-template failure path, which deliberately injects nothing at all.
    /// Unseated sessions pass through untouched.
    /// </summary>
    private static string AppendSeatParagraph(string preamble, Session session)
    {
        // ONE builder for every delivery channel (WorkflowSeatParagraph) - it also validates the
        // workflow id against the catalog slug shape, so a forged seat renders nothing.
        var paragraph = WorkflowSeatParagraph.Build(
            session.WorkflowRunId, session.WorkflowId, session.WorkflowVersion, session.ExplicitRole);
        if (paragraph is null)
            return preamble;
        return string.IsNullOrEmpty(preamble) ? paragraph : preamble + "\n\n" + paragraph;
    }

    private static IResult CommandResultToHttp(DirectorCommandResult result) => result.Status switch
    {
        DirectorCommandStatus.Ok => Results.Content(result.BodyJson ?? "{}", "application/json"),
        DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
        DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
        DirectorCommandStatus.Conflict => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Json(new { error = result.Error ?? "command failed" }, statusCode: StatusCodes.Status500InternalServerError),
    };

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

    /// <summary>
    /// Where a hold on <c>POST /fleet/hold</c> is decided. Extracted from the endpoint so the one rule
    /// that broke agent self-hold is unit-testable without an HTTP round trip.
    /// </summary>
    internal enum HoldRoute
    {
        /// <summary>Record it in the Gateway's SnoozeRegistry - the only place a hold is enforced.</summary>
        Gateway,
        /// <summary>No Gateway: write the local rail mirror, the only owner standalone has.</summary>
        LocalMirror,
        /// <summary>Unknown session and no Gateway to ask - 404.</summary>
        NotFound,
    }

    /// <summary>
    /// The hold routing rule. The Gateway owns hold, so whenever one is configured EVERY hold goes to it -
    /// including a session holding ITSELF, which is always local. Short-circuiting a local session to the
    /// mirror-only local path (the pre-fix behaviour) never reached the SnoozeRegistry, so the hold never
    /// landed and evaporated on the next roster fold. The local mirror is written only when there is no
    /// Gateway to own the hold.
    /// </summary>
    internal static HoldRoute ChooseHoldRoute(bool gatewayEnabled, bool sessionIsLocal)
        => gatewayEnabled ? HoldRoute.Gateway
         : sessionIsLocal ? HoldRoute.LocalMirror
         : HoldRoute.NotFound;

    /// <summary>
    /// Fold this Director's OWN sessions into the fleet roster relayed from the Gateway (issue #1019), and
    /// report which of them the relay had left out.
    ///
    /// The relay is authoritative for the fleet and STAYS authoritative for every session it knows: a row
    /// already in <paramref name="fleet"/> is returned untouched, so the session numbers and identity
    /// stamping the Gateway hands out are never overwritten by a local copy. This adds only the rows the
    /// relay omitted - which it does silently, while still returning 200, for any Director it cannot reach.
    /// A Director is first-hand authority on its own machine and must never deny a session it is itself
    /// holding, because this roster is what the CLI resolves an id against before it can reap anything.
    ///
    /// A row with no session id cannot be addressed by any caller, so it is never restored on that basis.
    /// </summary>
    internal static (List<SessionDto> Roster, List<string> Restored) UnionOwnSessions(
        IReadOnlyList<SessionDto> fleet, IReadOnlyList<SessionDto> own)
    {
        var roster = new List<SessionDto>(fleet);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in fleet)
        {
            if (!string.IsNullOrEmpty(s.SessionId))
                known.Add(s.SessionId);
        }

        var restored = new List<string>();
        foreach (var s in own)
        {
            if (string.IsNullOrEmpty(s.SessionId) || !known.Add(s.SessionId))
                continue;
            roster.Add(s);
            restored.Add(s.SessionId);
        }

        return (roster, restored);
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
    /// List sub-directories of <paramref name="path"/> for the remote folder browser, contained to
    /// <paramref name="allowedRoots"/> - the working directories of the sessions this Director hosts.
    /// A null or empty path lists the allowed roots themselves; a named path must resolve INSIDE one
    /// of them or the listing is refused. The pre-hardening version listed the drive roots and any
    /// directory on the machine ("solo-tailnet: no path sandboxing"); that stood only while a single
    /// owner held every key, and on a hosted Gateway it handed any active device key a full remote
    /// directory browse. Same containment discipline as <see cref="ResolveSessionFile"/> and
    /// <see cref="ResolveScreenshot"/>: resolve fully, then refuse anything outside an allowed root.
    /// </summary>
    // Gateway Cleanup Phase 0 (Worker R2): widened to internal so CatalogReadExecutor's fs-list core (the
    // list-directory read, lifted from GET /fs/list) calls the SAME helper - no drift, no duplication.
    internal static DirectoryListingDto ListDirectory(string? path, IEnumerable<string> allowedRoots)
    {
        // Normalize the roots once: full paths, trailing separators trimmed, duplicates collapsed.
        var roots = new List<string>();
        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string fullRoot;
            try { fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/'); }
            catch (ArgumentException) { continue; } // a label, not a local directory (remote-thread sessions)
            if (!roots.Contains(fullRoot, StringComparer.OrdinalIgnoreCase))
                roots.Add(fullRoot);
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            var rootEntries = roots
                .Select(r => new DirEntryDto
                {
                    Name = Path.GetFileName(r) is { Length: > 0 } name ? name : r,
                    Path = r,
                    IsDrive = false,
                })
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new DirectoryListingDto { CurrentPath = null, ParentPath = null, Entries = rootEntries };
        }

        var full = Path.GetFullPath(path).TrimEnd('\\', '/');
        var containingRoot = roots.FirstOrDefault(r =>
            string.Equals(full, r, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        if (containingRoot is null)
            throw new UnauthorizedAccessException($"directory is outside this Director's session working directories: {full}");

        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"directory not found: {full}");

        // Never offer navigation above the containing root: the root's own parent is not browsable.
        var parent = string.Equals(full, containingRoot, StringComparison.OrdinalIgnoreCase)
            ? null
            : Directory.GetParent(full)?.FullName;

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
        // The prompt-delivery ledger for this session, read once for the row (issue internal#811).
        var deliveryTally = CcDirector.Core.Input.PromptDeliveryFailures.Tally(s.Id);

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
            // Issue #959: the raw crash fact. ActivityState says only "Exited", so without this the fold
            // cannot tell a crash from a clean exit. This ONE mapper feeds both the Gateway wire and the
            // desktop rail's own fold input, so stamping it here restores the deep-red on the rail, the
            // Cockpit and the phone together.
            Crashed = s.Crashed,
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
            // Workflow seat (Workflows mission, phase 5b): the run this session executes, with its
            // cached workflow id + pinned version - stamped at spawn from the Gateway-validated
            // create request.
            WorkflowRunId = s.WorkflowRunId,
            WorkflowId = s.WorkflowId,
            WorkflowVersion = s.WorkflowVersion,
            SortOrder = s.SortOrder,
            StatusColor = s.StatusColor,
            LastStatusReason = s.LastStatusReason,
            BriefingState = s.BriefingState.ToString(),
            RailLine = s.LatestBriefRailLine,
            LastActivityAt = lastActivity,
            IdleSeconds = idleSeconds,
            QuietThresholdSeconds = CcDirector.Core.Wingman.TerminalStateDetector.QuietThreshold.TotalSeconds,
            VoiceMode = s.VoiceMode,
            // The display mirror the Gateway wrote down to us, echoed back. The Gateway OVERWRITES this in
            // its fold from its own registry without reading it, so this is reported for the loopback
            // readers (the desktop) and not because anyone upstream believes it. A Director does not
            // decide hold.
            HoldState = s.HoldState.ToString(),
            // One of the two facts this Director contributes to hold - the other is ActivityState above.
            // Only a Director can see this: desktop typing never leaves the machine, and the origin is
            // known only at the input choke points. The Gateway rules on what it means.
            LastOwnerTurnAtUtc = s.LastOwnerTurnAtUtc,
            // Prompts that did not go (issue internal#811). Only this machine can see a delivery fail, so
            // the counts and the unresolved flag are reported here as FACTS; the Gateway folds the words.
            // Before this they existed solely as a line in a Director log file, which is how two spoken
            // prompts were lost on 2026-07-15 and nobody found out for two days.
            FailedPromptDeliveries = deliveryTally.FailedDeliveries,
            ComposerEchoMisses = deliveryTally.ComposerEchoMisses,
            LastPromptDeliveryFailureAtUtc = deliveryTally.LastFailureAtUtc,
            LastPromptDeliveryFailureReason = deliveryTally.LastFailureReason,
            PromptDeliveryUnresolved = deliveryTally.Unresolved,
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
            // Session origin and lineage (devthrottle_internal issue #982): the birth facts, reported
            // straight from the Session. They ride every push, not just the first, because the durable
            // history row is written on FIRST SIGHT and a push that omitted them would be the one that
            // created the row. Nothing paints from these - they are the record, and the tree.
            OriginKind = s.OriginKind,
            OriginSurface = s.OriginSurface,
            ParentSessionId = s.ParentSessionId?.ToString(),
            // Automatic session roles (chunk 2.5): the sticky explicit role, so the Gateway aggregation can
            // apply the explicit-wins precedence. The RESOLVED SessionRole is computed at the aggregation.
            ExplicitRole = s.ExplicitRole,
            // Defect 5: the resolved role the GATEWAY computed from the whole fleet and stamped back down
            // onto this Director (Session.GatewayResolvedRole, written only by the set-resolved-role verb).
            // The Director carries it; it does NOT compute it - "is this session's controller still alive?"
            // needs the whole fleet, and the controller may be on another machine.
            //
            // THIS LINE IS WHY THE DESKTOP AGREES WITH THE PHONE. The rail's fold input comes from this same
            // mapper (SessionViewModel.FoldInput), so before this the field was always null on the desktop
            // and SessionOrdering's red-suppression could never fire: a live Worker read slate "Sub-agent"
            // on the phone and red "Needs you" on the rail at the same instant.
            //
            // Null when no Gateway has ever stamped one - the standalone-desktop floor, where a Worker's red
            // surfaces because nothing authoritative has said otherwise. That is the honest answer; a local
            // guess would be the defect. The value also rides back UP to the Gateway on the next delta,
            // where PushedSessionStore DISCARDS it at ingest so this echo can never be mistaken for an
            // authority. (docs/new_architecture/session-state.html, defect 5.)
            SessionRole = s.GatewayResolvedRole,
            // The Gateway's folded DISPLAY STATE, stamped back down onto this Director (Session.Gateway*,
            // written only by the set-display-state verb) and echoed here for the loopback reader that
            // cannot ask upstream: the desktop rail. The rail renders these VERBATIM instead of re-folding
            // from local facts it cannot see (dictation, transcription, voice generation, the snooze clock) -
            // which is exactly why a snoozed session read red "Needs you" on the desktop while the phone and
            // the Cockpit read "Snoozed". Null until a Gateway stamps them (the standalone-desktop floor);
            // the rail shows a neutral waiting-for-gateway placeholder rather than guessing. Like SessionRole
            // these ride back UP on the next delta, where the Gateway OVERWRITES them from its own fold, so
            // the echo can never be mistaken for an authority. (docs/new_architecture/session-state.html.)
            EffectiveColor = s.GatewayEffectiveColor,
            StateLabel = s.GatewayStateLabel,
            TriageBucket = s.GatewayTriageBucket,
            NeedsYouSince = s.GatewayNeedsYouSince,
            SnoozeUntil = s.GatewaySnoozeUntil,
            SnoozeExpired = s.GatewaySnoozeExpired,
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
            // The model producer (issue #1637): the driver-reported model this agent is currently
            // using, stamped at turn-end by SessionRecordsWatcher. Null until a read succeeds.
            CurrentModel = s.CurrentModel,
            // The token producer (issue #1637): this session's cumulative token spend, stamped at the
            // same turn-end from the same records as CurrentModel. Null until a read succeeds or on a
            // driver without the TokenUsage capability. The lean totals only - the on-demand usage view
            // keeps its own per-turn command.
            TokenTotals = s.TokenTotals,
            RemoteRepo = s.RemoteRepo ?? "",
            RemoteThreadUrl = s.RemoteThreadUrl ?? "",
            RemoteRunUrl = s.RemoteRunUrl ?? "",
            RemoteRunStatus = s.RemoteRunStatus ?? "",
            // DevThrottle Stats: the "owner/repo" repo name of this local checkout (GitHub or Azure DevOps),
            // resolved here on the Director because the git repo lives on this machine. Cached per path so
            // this roster mapper never forks git twice for the same checkout; "" when the checkout is on no
            // host we recognize, in which case the Gateway groups it by its folder name instead.
            RepoName = GitHubUrls.ResolveRepoNameCached(s.RepoPath),
            // How many files are changed in this session's working tree, measured on this machine by
            // SessionGitStatusMonitor - the number behind the desktop rail's amber "N chg" badge. Null
            // until a git probe succeeds, and null is UNKNOWN, never "clean": a failed probe leaves the
            // last known count in place rather than reporting a zero every reader would render as a clean
            // tree (issue 516). Nothing is derived here - the Gateway passes the count through and the
            // clients share one formatter.
            UncommittedCount = s.UncommittedCount,
            // Supervision facts (internal#625 Phase 1): the completed-turn counter and the honest
            // waiting clock, kept by the Session at the activity flip. Raw facts; the Gateway
            // passes them through and the clients share one formatter. Always reported by this
            // Director - only an older build leaves them null on the wire.
            TurnCount = s.TurnCount,
            WaitingSince = s.WaitingSince,
            CumulativeIdleSeconds = s.CumulativeIdleSeconds,
            // The interruption COUNT beside the seconds (devthrottle_internal issue #982): how many
            // times this session started waiting on the user. The pair is what makes the clock
            // readable - an hour of waiting spread over twelve interruptions is a different session
            // from one that waited once.
            WaitingStretchCount = s.WaitingStretchCount,
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

    /// <summary>
    /// Resolve a client-supplied path for the session file read (the Local Files viewer) to an absolute
    /// path INSIDE the session's own working directory, or null when it escapes it - an absolute path
    /// elsewhere on the machine, a traversal that resolves outside, or an invalid path. This is
    /// <see cref="ResolveScreenshot"/>'s twin for the read-file stream verb: resolve fully first, then
    /// refuse anything outside the allowed root. The allowed root is the session's working directory -
    /// the one directory the session is about - so a session-scoped file read can never reach
    /// credentials, tokens, or any other file elsewhere on the machine.
    /// </summary>
    internal static string? ResolveSessionFile(string? workingDirectory, string? path)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || string.IsNullOrWhiteSpace(path))
            return null;

        string root;
        string full;
        try
        {
            root = Path.GetFullPath(workingDirectory);
            // A relative path resolves against the session's working directory; an absolute path stays
            // itself. Either way the containment check below runs on the fully resolved result.
            full = Path.GetFullPath(path, root);
        }
        catch (ArgumentException)
        {
            // Invalid characters, an embedded NUL, or a relative working directory (a session with no
            // real local checkout, e.g. a remote-thread session) - nothing here is safe to serve.
            return null;
        }

        var rootTrimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.StartsWith(rootTrimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        return full;
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

/// <summary>
/// The body of POST /fleet/machines/{machine}/launch. Either <see cref="Path"/> or <see cref="App"/> says
/// what to start - a path directly, or a name resolved against that machine's own catalogue.
/// </summary>
internal sealed class MachineLaunchRequest
{
    public string? Path { get; init; }
    public string? App { get; init; }
    public string? Args { get; init; }
    public string? Cwd { get; init; }
    public bool Headless { get; init; }
}
