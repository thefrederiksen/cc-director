using CcDirector.Core.Agents;
using CcDirector.Core.Claude;
using CcDirector.Core.History;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (spine): the SESSION READ area of the tunnel command surface. It owns
/// the per-session read verbs (the reply body is the resource DTO). The spine seeds it with ONE exemplar -
/// <c>turns</c> - to fix the pattern a worker copies; Worker R1 fills in the rest (snapshot, buffer, summary,
/// handover, brief, recap, and so on) by adding each verb to <see cref="Verbs"/> and a core method here.
///
/// The core is extracted verbatim from the Director's <c>GET /sessions/{sid}/turns</c> lambda. That REST
/// route now calls this SAME core, so the tunnel verb and the route cannot drift (the core is the single
/// source of truth); Phase 1 deletes the route and leaves the core reached only over the tunnel.
/// </summary>
internal sealed class SessionReadExecutor : ISessionCommandArea
{
    public IReadOnlyCollection<string> Verbs { get; } = new[] { "turns" };

    public Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        var result = command.Verb switch
        {
            "turns" => Turns(context.SessionManager, command),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"verb '{command.Verb}' is not handled by the session read area"),
        };
        return Task.FromResult(result);
    }

    /// <summary>
    /// The <c>turns</c> verb: the agent-agnostic conversation history widgets for a session. Mirrors the
    /// Director's <c>GET /sessions/{sid}/turns</c> lambda exactly - invalid id -&gt; BadRequest, missing
    /// session -&gt; NotFound - and returns a serialized <see cref="TurnsResponse"/> on success. Every
    /// non-error branch (unsupported agent, not-yet-linked, missing JSONL, a parse failure) is a 200 status
    /// with a <see cref="TurnsResponse.Status"/> string, exactly as the REST route returned; only a bad id
    /// or a missing session are true error statuses. The parse try/catch is preserved from the source
    /// because a parse failure is a DOMAIN state ("parse_error") the caller reads, not a fault to bubble.
    /// </summary>
    internal static DirectorCommandResult Turns(SessionManager sessionManager, DirectorCommand command)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var resp = new TurnsResponse
        {
            SessionId = command.SessionId,
            ClaudeSessionId = session.ClaudeSessionId,
        };

        if (session.AgentKind != AgentKind.ClaudeCode)
        {
            if (!SessionHistoryReader.IsSupported(session))
            {
                resp.Status = "unsupported";
                resp.Error = $"Agent {session.AgentKind} does not expose conversation history.";
                return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(resp));
            }

            var history = SessionHistoryReader.Read(session);
            resp.JsonlPath = SessionHistoryReader.ResolveTranscriptPath(session);
            resp.LineCount = history.Messages.Count;
            resp.Widgets = ControlEndpoints.BuildTurnWidgetsFromHistory(history);
            resp.Status = "ok";
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(resp));
        }

        if (string.IsNullOrEmpty(session.ClaudeSessionId))
        {
            resp.Status = "no_session_id";
            resp.Error = "Session has not been linked to a Claude session id yet.";
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(resp));
        }

        try
        {
            var jsonl = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
            resp.JsonlPath = jsonl;

            if (!File.Exists(jsonl))
            {
                resp.Status = "no_jsonl";
                resp.Error = $"JSONL file not found at {jsonl}";
                return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(resp));
            }

            var messages = StreamMessageParser.ParseFile(jsonl);
            resp.LineCount = messages.Count;
            resp.Widgets = WidgetBuilder.BuildFromMessages(messages);
            resp.Status = "ok";
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(resp));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionReadExecutor] turns FAILED: {ex.Message}");
            resp.Status = "parse_error";
            resp.Error = ex.Message;
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(resp));
        }
    }
}
