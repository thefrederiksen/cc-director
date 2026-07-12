using System.Diagnostics;
using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (spine): the SESSION WRITE area of the tunnel command surface. It owns
/// the per-session and director-level write verbs. The ten verbs that already rode the tunnel before this
/// mission (prompt, interrupt, escape, hold, kill, patch, create, wingman-goal, set-role, attach-mission)
/// keep their extracted cores in <see cref="SessionCommandExecutor"/> - unchanged and already tested - and
/// this area simply routes to them, so there is ONE dispatch path with no legacy switch left behind. The
/// spine adds two new exemplar cores here to fix the pattern a worker copies: <c>resize</c> (a clean
/// representative write) and <c>terminal-input</c> (the unary keystroke write - NOT a stream verb, per
/// Architect ruling A: it needs neither the connection nor the stream registry). Worker W1 fills in the
/// remaining state writes; which of the ten legacy verbs later move their cores into this file is a refinement.
/// </summary>
internal sealed class SessionWriteExecutor : ISessionCommandArea
{
    public IReadOnlyCollection<string> Verbs { get; } = new[]
    {
        // The ten verbs that already rode the tunnel (cores in SessionCommandExecutor, routed here).
        "prompt", "interrupt", "escape", "hold", "kill", "patch", "create", "wingman-goal", "set-role", "attach-mission",
        // New spine exemplars owned here.
        "resize", "terminal-input",
        // Worker W1: the remaining Director session STATE writes, moved onto the tunnel dispatch. Each core
        // below reproduces its old REST lambda's guards and effect verbatim, so the REST path and the tunnel
        // verb share one core and cannot drift.
        "clear-context", "history-picker", "mobile-mode", "voice-mode", "wingman-enabled",
        "relink", "request-deletion", "cancel-deletion", "execute-action",
        // Gateway Cleanup Phase 0 (wave 3): the final deferred write verbs. handover-generate is
        // director-level (creates/targets a session from a source); wingman-ask and recap-generate are
        // per-session writes that call static services (no new dependency). Each core reproduces its old
        // REST lambda verbatim, so the REST route and the tunnel verb share one core and cannot drift.
        "handover-generate", "wingman-ask", "recap-generate",
    };

    public async Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        var sessionManager = context.SessionManager;
        return command.Verb switch
        {
            "prompt" => await SessionCommandExecutor.PromptAsync(sessionManager, command, context.Source),
            "interrupt" => await SessionCommandExecutor.InterruptAsync(sessionManager, command),
            "escape" => await SessionCommandExecutor.EscapeAsync(sessionManager, command),
            "hold" => SessionCommandExecutor.Hold(sessionManager, command),
            "kill" => await SessionCommandExecutor.KillAsync(sessionManager, command),
            "patch" => SessionCommandExecutor.Patch(sessionManager, context.DirectorId, command),
            "create" => SessionCommandExecutor.Create(sessionManager, context.DirectorId, command, context.Services),
            "wingman-goal" => SessionCommandExecutor.WingmanGoal(sessionManager, command, context.Services),
            "set-role" => SessionCommandExecutor.SetRole(sessionManager, context.DirectorId, command),
            "attach-mission" => SessionCommandExecutor.AttachMission(sessionManager, context.DirectorId, command, context.Services),
            "resize" => Resize(sessionManager, command),
            "terminal-input" => TerminalInput(sessionManager, command),
            "clear-context" => await ClearContextAsync(sessionManager, command, cancellationToken),
            "history-picker" => await HistoryPickerAsync(sessionManager, command),
            "mobile-mode" => MobileMode(sessionManager, command, context.Services),
            "voice-mode" => VoiceMode(sessionManager, command, context.Services),
            "wingman-enabled" => WingmanEnabled(sessionManager, command, context.Services),
            "relink" => Relink(sessionManager, command),
            "request-deletion" => RequestDeletion(sessionManager, command),
            "cancel-deletion" => CancelDeletion(sessionManager, command),
            "execute-action" => ExecuteAction(sessionManager, command),
            "handover-generate" => await HandoverGenerateAsync(sessionManager, context.DirectorId, command),
            "wingman-ask" => await WingmanAskAsync(context, command, cancellationToken),
            "recap-generate" => await RecapGenerateAsync(sessionManager, context.DirectorId, command, cancellationToken),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"verb '{command.Verb}' is not handled by the session write area"),
        };
    }

    /// <summary>
    /// The <c>resize</c> verb: set a session's PTY grid so a remote terminal can use the full window width.
    /// Mirrors the Director's <c>POST /sessions/{sid}/resize</c> lambda exactly - invalid id -&gt; BadRequest,
    /// non-positive cols/rows -&gt; BadRequest, missing session -&gt; NotFound - and returns the resulting grid.
    /// </summary>
    internal static DirectorCommandResult Resize(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = SessionCommandExecutor.Deserialize<ResizeRequest>(command.PayloadJson);
        if (request is null || request.Cols <= 0 || request.Rows <= 0)
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "cols and rows must be > 0");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        session.Resize((short)Math.Min(request.Cols, short.MaxValue), (short)Math.Min(request.Rows, short.MaxValue));
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new ResizeResponse
        {
            Accepted = true,
            Cols = session.CurrentCols,
            Rows = session.CurrentRows,
        }));
    }

    /// <summary>
    /// The <c>terminal-input</c> verb: forward a browser keystroke frame to the session's PTY, the same call
    /// the live terminal stream's input pump made (<see cref="Session.SendInput(byte[])"/>). The payload is a
    /// base64 byte blob so control bytes (arrows, Ctrl+C, Esc) survive the JSON envelope. Invalid id -&gt;
    /// BadRequest, missing/undecodable bytes -&gt; BadRequest, missing session -&gt; NotFound. This is a plain
    /// unary write; it is NOT a stream verb (Architect ruling A). The Gateway wires the browser's keystrokes
    /// to this verb in Phase 2; the spine adds the core and makes it dispatchable and testable now.
    /// </summary>
    internal static DirectorCommandResult TerminalInput(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = SessionCommandExecutor.Deserialize<TerminalInputRequest>(command.PayloadJson);
        if (request is null || string.IsNullOrEmpty(request.Bytes))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "bytes are required");

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(request.Bytes);
        }
        catch (FormatException)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "bytes must be base64");
        }

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        session.SendInput(bytes);
        return DirectorCommandResult.Success();
    }

    /// <summary>
    /// The <c>clear-context</c> verb: reset the conversation context in place (Claude's /clear, pi's /new) and,
    /// for transcript-capable drivers, re-link the Director to the NEW agent session id. Mirrors the Director's
    /// <c>POST /sessions/{sid}/clear-context</c> lambda exactly - invalid id -&gt; BadRequest, missing session
    /// -&gt; NotFound, a driver with no reset (NotSupportedException) -&gt; Conflict - and returns the old and
    /// new agent session ids. The re-link runs only when the driver reported a new id.
    /// </summary>
    internal static async Task<DirectorCommandResult> ClearContextAsync(SessionManager sessionManager, DirectorCommand command, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var oldId = session.ClaudeSessionId;
        string? newId;
        try
        {
            newId = await session.ClearContextAsync(cancellationToken);
        }
        catch (NotSupportedException ex)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, ex.Message);
        }

        if (newId is not null)
            sessionManager.RelinkClaudeSession(guid, newId);

        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new
        {
            accepted = true,
            oldAgentSessionId = oldId,
            newAgentSessionId = newId,
        }));
    }

    /// <summary>
    /// The <c>history-picker</c> verb: open the tool's in-terminal history picker (Claude's double-Esc). A
    /// visible-terminal feature. Mirrors the Director's <c>POST /sessions/{sid}/history-picker</c> lambda -
    /// invalid id -&gt; BadRequest, missing session -&gt; NotFound, a driver that has no picker
    /// (NotSupportedException) -&gt; Conflict - and returns <c>{ accepted = true }</c>.
    /// </summary>
    internal static async Task<DirectorCommandResult> HistoryPickerAsync(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        try
        {
            await session.ShowHistoryAsync();
        }
        catch (NotSupportedException ex)
        {
            return DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, ex.Message);
        }
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new { accepted = true }));
    }

    /// <summary>
    /// The <c>mobile-mode</c> verb: toggle a session's mobile (text) view. Mirrors the Director's
    /// <c>POST /sessions/{sid}/mobile-mode</c> lambda - invalid id -&gt; BadRequest, missing session -&gt;
    /// NotFound - setting <see cref="Session.ViewMode"/> to Text (on) or Off, and, when turned on, warming the
    /// briefing cache via <see cref="ProactiveExplainService"/> (a side effect threaded through the services
    /// context, exactly as the wingman-goal core uses <c>TurnSummaryCache</c>). An empty/absent payload
    /// defaults to enabled (the common "watch this one" case), matching the REST endpoint. Returns the
    /// resulting mobile-mode flag.
    /// </summary>
    internal static DirectorCommandResult MobileMode(SessionManager sessionManager, DirectorCommand command, SessionCommandServices? services)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var request = SessionCommandExecutor.Deserialize<MobileModeRequest>(command.PayloadJson);
        var enabled = request?.Enabled ?? true;

        // Session (text) tab: Text when watching, Off when the phone navigates away. MobileMode is derived
        // from ViewMode, so proactive briefings behave identically; we only also distinguish Voice from Text.
        session.ViewMode = enabled ? MobileViewMode.Text : MobileViewMode.Off;
        FileLog.Write($"[SessionWriteExecutor] mobile-mode: session={guid} enabled={enabled} viewMode={session.ViewMode}");
        if (enabled) services?.ProactiveExplain?.TriggerBackgroundExplain(session);
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new { mobileMode = session.MobileMode }));
    }

    /// <summary>
    /// The <c>voice-mode</c> verb: toggle a session's in-car voice view. Mirrors the Director's
    /// <c>POST /sessions/{sid}/voice-mode</c> lambda - invalid id -&gt; BadRequest, missing session -&gt;
    /// NotFound - setting <see cref="Session.ViewMode"/> to Voice (on) or Text (off) and warming the briefing
    /// cache immediately (unconditionally, as the REST endpoint does) so the phone has something to speak.
    /// An empty/absent payload defaults to enabled. Returns the resulting voice-mode and mobile-mode flags.
    /// </summary>
    internal static DirectorCommandResult VoiceMode(SessionManager sessionManager, DirectorCommand command, SessionCommandServices? services)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var request = SessionCommandExecutor.Deserialize<VoiceModeRequest>(command.PayloadJson);
        var enabled = request?.Enabled ?? true;

        session.ViewMode = enabled ? MobileViewMode.Voice : MobileViewMode.Text;
        FileLog.Write($"[SessionWriteExecutor] voice-mode: session={guid} enabled={enabled} viewMode={session.ViewMode} (wingmanEnabled unchanged: {session.WingmanEnabled})");
        services?.ProactiveExplain?.TriggerBackgroundExplain(session);
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new { voiceMode = session.VoiceMode, mobileMode = session.MobileMode }));
    }

    /// <summary>
    /// The <c>wingman-enabled</c> verb: toggle the whole Wingman experience for a session. Mirrors the
    /// Director's <c>POST /sessions/{sid}/wingman-enabled</c> lambda - invalid id -&gt; BadRequest, missing
    /// session -&gt; NotFound - setting <see cref="Session.WingmanEnabled"/>. When flipped ON it warms the
    /// briefing cache; when flipped OFF it clears <see cref="Session.IsExplaining"/> so a yellow dot does not
    /// stick waiting on an in-flight briefing. An empty/absent payload defaults to enabled. Returns the flag.
    /// </summary>
    internal static DirectorCommandResult WingmanEnabled(SessionManager sessionManager, DirectorCommand command, SessionCommandServices? services)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var request = SessionCommandExecutor.Deserialize<WingmanEnabledRequest>(command.PayloadJson);
        var enabled = request?.Enabled ?? true;

        session.WingmanEnabled = enabled;
        FileLog.Write($"[SessionWriteExecutor] wingman-enabled: session={guid} enabled={enabled}");
        if (enabled)
            services?.ProactiveExplain?.TriggerBackgroundExplain(session);
        else
            session.IsExplaining = false;
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new { wingmanEnabled = session.WingmanEnabled }));
    }

    /// <summary>
    /// The <c>relink</c> verb: re-point a Director session at a different Claude session id (recover continuity
    /// when the underlying id changed). Mirrors the Director's <c>POST /sessions/{sid}/relink</c> lambda -
    /// invalid id -&gt; BadRequest, absent/blank claudeSessionId -&gt; BadRequest, missing session -&gt;
    /// NotFound - and returns <c>{ accepted, claudeSessionId }</c>.
    /// </summary>
    internal static DirectorCommandResult Relink(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var request = SessionCommandExecutor.Deserialize<RelinkRequest>(command.PayloadJson);
        if (request is null || string.IsNullOrWhiteSpace(request.ClaudeSessionId))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "claudeSessionId is required");

        if (sessionManager.GetSession(guid) is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        sessionManager.RelinkClaudeSession(guid, request.ClaudeSessionId);
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new { accepted = true, claudeSessionId = request.ClaudeSessionId }));
    }

    /// <summary>
    /// The <c>request-deletion</c> verb: flag a session for asynchronous removal by the owning Director's
    /// deletion reaper (the SAFE self-delete that does not kill the caller mid-request). Mirrors the Director's
    /// <c>POST /sessions/{sid}/request-deletion</c> lambda - invalid id -&gt; BadRequest, missing session -&gt;
    /// NotFound - and returns <c>{ pendingDeletion = true, requestedAt, reason }</c>. The body (a reason) is
    /// optional. The caller-identity boundary log stays on the REST endpoint (it reads the HTTP connection).
    /// </summary>
    internal static DirectorCommandResult RequestDeletion(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var request = SessionCommandExecutor.Deserialize<SessionDeletionRequest>(command.PayloadJson);
        session.MarkForDeletion(request?.Reason);
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new
        {
            pendingDeletion = true,
            requestedAt = session.DeletionRequestedAt,
            reason = session.DeletionReason,
        }));
    }

    /// <summary>
    /// The <c>cancel-deletion</c> verb: cancel a pending deletion during the grace window (operator changed
    /// their mind). Mirrors the Director's <c>DELETE /sessions/{sid}/request-deletion</c> lambda - invalid id
    /// -&gt; BadRequest, missing session -&gt; NotFound - and returns <c>{ pendingDeletion = false }</c>. As
    /// with request-deletion, the caller-identity boundary log stays on the REST endpoint.
    /// </summary>
    internal static DirectorCommandResult CancelDeletion(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        session.CancelDeletion();
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new { pendingDeletion = false }));
    }

    /// <summary>
    /// The <c>execute-action</c> verb (issue #327): the DUMB execute leg of the Wingman decide/execute split -
    /// carry out a fully-formed <see cref="WingmanAction"/> supplied by the caller, EXACTLY as passed, through
    /// the single write chokepoint <see cref="WingmanActionExecutor"/>, with zero decision logic and no LLM.
    /// Mirrors the Director's <c>POST /sessions/{sid}/execute-action</c> lambda - invalid id -&gt; BadRequest,
    /// missing session -&gt; NotFound, absent body -&gt; BadRequest. The executor's own outcome (ok / suppressed
    /// / session_gone / bad_request) rides back inside the serialized <see cref="WingmanActResult"/> so the
    /// REST layer maps it to the same HTTP codes it did before (session_gone -&gt; 410, bad_request -&gt; 400);
    /// the session_gone error text is filled here so both the REST and tunnel callers see it identically.
    /// </summary>
    internal static DirectorCommandResult ExecuteAction(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var action = SessionCommandExecutor.Deserialize<WingmanAction>(command.PayloadJson);
        if (action is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest,
                "body is required: a WingmanAction JSON object (action none|type|send_keys|submit)");

        FileLog.Write($"[SessionWriteExecutor] execute-action: session={guid} action={action.Action}");
        var result = WingmanActionExecutor.Execute(session, action);
        FileLog.Write($"[SessionWriteExecutor] execute-action: session={guid} action={result.Action} performed={result.Performed} status={result.Status}");

        // A gone session gets its explanatory error filled here (the REST lambda used to set it just before
        // returning 410) so the executor outcome is complete regardless of which caller reads it.
        if (result.Status == WingmanActResult.StatusSessionGone)
            result.Error = $"session is {session.Status}; nothing was injected";

        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(result));
    }

    /// <summary>
    /// The <c>handover-generate</c> verb (director-level, no target session on the command): the atomic
    /// "move the work" write. Mirrors the Director's <c>POST /handover</c> lambda verbatim - it reads the
    /// SOURCE session's context, delivers it to a TARGET (an existing session, or a brand-new one created in
    /// a repo), and optionally archives a markdown copy to the vault. The guards return the same statuses the
    /// route did: a bad/blank fromSessionId, the mutually-exclusive/exactly-one target rule, a bad
    /// toSessionId/toRepoPath, or an unknown agent -&gt; BadRequest; a missing source or target -&gt; NotFound;
    /// an Exited/Failed existing target -&gt; Conflict (the route's empty-body 409); a create fault -&gt; Error
    /// (the route's 500). On success it returns a <see cref="HandoverResponse"/> whose <c>TargetSession</c> is
    /// the plain <see cref="ControlEndpoints.Map"/> - the Director's own REST route re-maps it with its
    /// identity-stamped mapper for its 201, exactly as the create verb does. The new-session dispatch (wait for
    /// idle, then send) and the best-effort archive keep their fire-and-forget task and try/catch from the
    /// source, so behaviour is byte-identical.
    /// </summary>
    internal static async Task<DirectorCommandResult> HandoverGenerateAsync(SessionManager sessionManager, string directorId, DirectorCommand command)
    {
        var req = SessionCommandExecutor.Deserialize<HandoverRequest>(command.PayloadJson);
        FileLog.Write($"[SessionWriteExecutor] handover-generate: from={req?.FromSessionId} toSid={req?.ToSessionId} toRepo={req?.ToRepoPath}");

        if (req is null || string.IsNullOrEmpty(req.FromSessionId))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "fromSessionId is required");
        if (string.IsNullOrEmpty(req.ToSessionId) && string.IsNullOrEmpty(req.ToRepoPath))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "exactly one of toSessionId or toRepoPath is required");
        if (!string.IsNullOrEmpty(req.ToSessionId) && !string.IsNullOrEmpty(req.ToRepoPath))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "toSessionId and toRepoPath are mutually exclusive");

        if (!Guid.TryParse(req.FromSessionId, out var fromGuid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid fromSessionId format");

        var source = sessionManager.GetSession(fromGuid);
        if (source is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "source session not found on this director");

        // 1) Build the context text
        SessionSummaryDto summary;
        if (string.IsNullOrEmpty(source.ClaudeSessionId))
        {
            summary = new SessionSummaryDto
            {
                SessionId = req.FromSessionId, DirectorId = directorId,
                Agent = source.AgentKind.ToString(),
                RepoPath = source.RepoPath,
                ActivityState = source.ActivityState.ToString(),
                CreatedAt = source.CreatedAt.UtcDateTime,
            };
        }
        else
        {
            var jsonl = ClaudeSessionReader.GetJsonlPath(source.ClaudeSessionId, source.RepoPath);
            summary = File.Exists(jsonl)
                ? SummaryBuilder.Build(StreamMessageParser.ParseFile(jsonl))
                : new SessionSummaryDto();
            summary.SessionId = req.FromSessionId;
            summary.DirectorId = directorId;
            summary.Agent = source.AgentKind.ToString();
            summary.RepoPath = source.RepoPath;
            summary.ActivityState = source.ActivityState.ToString();
            summary.CreatedAt = source.CreatedAt.UtcDateTime;
        }
        var contextText = SummaryBuilder.FormatAsHandoverPrompt(summary, req.ExtraContext);

        // 2) Find or create the target session
        Session target;
        if (!string.IsNullOrEmpty(req.ToSessionId))
        {
            if (!Guid.TryParse(req.ToSessionId, out var toGuid))
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid toSessionId format");
            var existing = sessionManager.GetSession(toGuid);
            if (existing is null)
                return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "target session not found on this director");
            if (existing.Status is SessionStatus.Exited or SessionStatus.Failed)
                return DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, "target session has exited");
            target = existing;
            await target.SendTextAsync(contextText, SendSource.Internal);
        }
        else
        {
            var repo = req.ToRepoPath!;
            if (!Directory.Exists(repo))
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"toRepoPath does not exist: {repo}");
            if (!Enum.TryParse<AgentKind>(req.ToAgent, ignoreCase: true, out var kind))
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unknown agent: {req.ToAgent}");

            if (!AgentPluginRegistry.Contains(kind))
                return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"agent {kind} is not a built-in plugin target");
            var agent = AgentPluginRegistry.CreateAgent(kind, sessionManager.Options);

            try
            {
                target = sessionManager.CreateSession(repo, agent, userArgs: null, SessionBackendType.ConPty, resumeSessionId: null);
            }
            catch (Exception ex)
            {
                return DirectorCommandResult.Fail(DirectorCommandStatus.Error, "failed to create target session: " + ex.Message);
            }
            // SessionManager.CreateSession now fires OnSessionCreated itself, so no
            // explicit RaiseSessionCreated call is needed here.

            // Dispatch the context after the new session reaches Idle. Fire-and-forget;
            // we return the target DTO immediately so callers can navigate to it.
            var capturedTarget = target;
            var capturedText = contextText;
            _ = Task.Run(async () =>
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(30_000);
                while (DateTime.UtcNow < deadline)
                {
                    var st = capturedTarget.ActivityState;
                    if (st is ActivityState.Idle or ActivityState.WaitingForInput) break;
                    if (st is ActivityState.Exited) { FileLog.Write($"[SessionWriteExecutor] handover-generate target exited before idle, sid={capturedTarget.Id}"); return; }
                    await Task.Delay(500);
                }
                try { await capturedTarget.SendTextAsync(capturedText, SendSource.Internal); }
                catch (Exception ex) { FileLog.Write($"[SessionWriteExecutor] handover-generate dispatch FAILED: {ex.Message}"); }
            });
        }

        // 3) Optionally archive to vault
        string? archivedAt = null;
        if (req.ArchiveToVault)
        {
            try { archivedAt = HandoverArchive.Write(summary, contextText, target.Id.ToString()); }
            catch (Exception ex) { FileLog.Write($"[SessionWriteExecutor] handover-generate archive FAILED: {ex.Message}"); }
        }

        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new HandoverResponse
        {
            Accepted = true,
            TargetSession = ControlEndpoints.Map(target, directorId),
            ContextSent = contextText,
            ArchivedAt = archivedAt,
        }));
    }

    /// <summary>
    /// The <c>wingman-ask</c> verb: the "Ask the Wingman" channel. Mirrors the Director's
    /// <c>POST /sessions/{sid}/wingman/ask</c> lambda - two behaviours, both on the strong model:
    /// <c>mode=explain</c> is a terse "what's happening" briefing over pre-built context; a free-text
    /// question opens a read-only full-power session over the whole terminal + repo that answers faithfully
    /// and reads content VERBATIM. Invalid id -&gt; BadRequest; a missing session -&gt; NotFound. The
    /// question-required guard is NOT an id/session error but the wingman's own <c>bad_request</c> outcome,
    /// so it rides back as a 200 <see cref="WingmanAskResult"/> (Status "bad_request"); the REST route maps
    /// that Status to its original 400 exactly as the execute-action verb maps its executor outcomes. The
    /// turn-summary cache (explain mode's context input) rides in the command services; no new dependency is
    /// introduced because the answer itself comes from the static <see cref="Core.Wingman.WingmanService"/>
    /// methods. Lifted as a plain unary verb (Architect option A); the slow-LLM invocation strategy is a
    /// Phase 2 decision, deliberately unchanged here.
    /// </summary>
    internal static async Task<DirectorCommandResult> WingmanAskAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        var sessionManager = context.SessionManager;
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var req = SessionCommandExecutor.Deserialize<WingmanAskRequest>(command.PayloadJson);
        var explain = string.Equals(req?.Mode, "explain", StringComparison.OrdinalIgnoreCase);
        // Explain mode briefs the whole session and needs no user question; the free-text ask path still
        // requires one. This is the wingman's bad_request outcome, carried in the result (a 200 here, mapped
        // to a 400 at the REST boundary), NOT an id/session guard.
        if (req is null || (!explain && string.IsNullOrWhiteSpace(req.Question)))
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(
                new WingmanAskResult { Status = "bad_request", Error = "question is required" }));

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        // Explain = the terse "what's happening" briefing (strong model over pre-built, length-capped
        // context). Unchanged.
        if (explain)
        {
            var explainCtx = await WingmanContextBuilder.BuildAsync(session, context.Services?.TurnSummaryCache, cancellationToken);
            var explainResult = await Core.Wingman.WingmanService.AskAboutSessionAsync(
                req.Question, explainCtx, sessionManager.Options.ClaudePath, cancellationToken, explain: true);
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(explainResult));
        }

        // Any free-text question = the faithful "Ask the Wingman" channel: a read-only full-power session
        // over the WHOLE terminal + repo, on the strong model, that reads content VERBATIM instead of
        // summarizing.
        var fullTerminal = ControlEndpoints.ReadFullCleanedBuffer(session);
        var result = await Core.Wingman.WingmanService.AnswerViaSessionAsync(
            req.Question, fullTerminal, session.AgentKind.ToString(), session.RepoPath,
            sessionManager.Options.ClaudePath, cancellationToken);
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(result));
    }

    /// <summary>
    /// The <c>recap-generate</c> verb: build (and cache) a fresh recap for a session by running the static
    /// <see cref="RecapGenerator.GenerateAsync"/> over a digest of the session's Claude transcript. Mirrors
    /// the Director's <c>POST /sessions/{sid}/recap</c> lambda - invalid id -&gt; BadRequest, missing session
    /// -&gt; NotFound. The not-yet-linked, missing-transcript, and generation-failure branches are DOMAIN
    /// states the route returned as 200 <see cref="RecapResponse"/> bodies (status strings), so they stay
    /// <see cref="DirectorCommandStatus.Ok"/> here; the successful generation is also an Ok body whose
    /// <c>Status == "ok"</c>, which the REST route maps to its original 201 (a 200 for the domain-state
    /// bodies). The <c>model</c> query argument rides in the <see cref="RecapGenerateRequest"/> payload. A
    /// caller cancellation (the route's 499) is NOT caught here - it is a boundary concern that bubbles to
    /// the REST/stream boundary - while every other generation fault is preserved as the route's
    /// <c>generation_failed</c> 200, exactly as the source lambda's try/catch did.
    /// </summary>
    internal static async Task<DirectorCommandResult> RecapGenerateAsync(SessionManager sessionManager, string directorId, DirectorCommand command, CancellationToken cancellationToken)
    {
        FileLog.Write($"[SessionWriteExecutor] recap-generate: sid={command.SessionId}");
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var request = SessionCommandExecutor.Deserialize<RecapGenerateRequest>(command.PayloadJson);
        var model = request?.Model;
        if (string.IsNullOrWhiteSpace(model))
            model = RecapGenerator.DefaultModel;

        if (string.IsNullOrEmpty(session.ClaudeSessionId))
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new RecapResponse
            {
                SessionId = command.SessionId,
                Model = model,
                Status = "no_session_id",
                Error = "Session has not been linked to a Claude session id yet.",
            }));

        var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
        if (!File.Exists(jsonl))
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new RecapResponse
            {
                SessionId = command.SessionId,
                Model = model,
                Status = "no_jsonl",
                Error = $"JSONL file not found at {jsonl}",
            }));

        SessionSummaryDto summary;
        string digest;
        int currentTurns;
        try
        {
            var messages = StreamMessageParser.ParseFile(jsonl);
            summary = SummaryBuilder.Build(messages);
            summary.SessionId = command.SessionId;
            summary.DirectorId = directorId;
            summary.Agent = session.AgentKind.ToString();
            summary.RepoPath = session.RepoPath;
            summary.ActivityState = session.ActivityState.ToString();
            summary.CreatedAt = session.CreatedAt.UtcDateTime;
            digest = SummaryBuilder.FormatAsHandoverPrompt(summary);
            currentTurns = summary.TurnCount;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionWriteExecutor] recap-generate digest build FAILED: {ex.Message}");
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new RecapResponse
            {
                SessionId = command.SessionId,
                Model = model,
                Status = "generation_failed",
                Error = "Failed to build session digest: " + ex.Message,
            }));
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var recapText = await RecapGenerator.GenerateAsync(
                digest, sessionManager.Options.ClaudePath, model, cancellationToken);
            sw.Stop();

            var entry = new RecapCache.Entry
            {
                Recap = recapText,
                GeneratedAt = DateTime.UtcNow,
                AtTurnCount = currentTurns,
                Model = model,
                ElapsedMs = sw.ElapsedMilliseconds,
            };
            RecapCache.Set(guid, entry);

            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new RecapResponse
            {
                SessionId = command.SessionId,
                Recap = entry.Recap,
                GeneratedAt = entry.GeneratedAt,
                AtTurnCount = entry.AtTurnCount,
                CurrentTurnCount = currentTurns,
                IsStale = false,
                Model = entry.Model,
                ElapsedMs = entry.ElapsedMs,
                Status = "ok",
            }));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[SessionWriteExecutor] recap-generate generation FAILED: {ex.Message}");
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(new RecapResponse
            {
                SessionId = command.SessionId,
                Model = model,
                Status = "generation_failed",
                Error = ex.Message,
            }));
        }
    }
}
