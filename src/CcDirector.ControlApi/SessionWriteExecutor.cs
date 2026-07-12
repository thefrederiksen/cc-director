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
}
