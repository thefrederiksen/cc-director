using CcDirector.Core.Sessions;
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
}
