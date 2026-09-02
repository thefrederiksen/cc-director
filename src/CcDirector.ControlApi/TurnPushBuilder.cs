using CcDirector.Core.Agents;
using CcDirector.Core.History;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Everything the Director knows about one session's conversation at one moment, in the shape the
/// Gateway stores (the turn-push mission, <c>docs/missions/turn-push-2026-09-01/brief.md</c>): the
/// generation (the identity of the transcript source), the per-session facts, and EVERY message as a
/// <see cref="PushedTurn"/> in order. <see cref="TurnPusher"/> slices this from the Gateway's watermark;
/// the Director reads the whole source once per turn instead of once per 2.5-second poll.
/// </summary>
public sealed record TurnSnapshot(
    string SessionId,
    string Generation,
    string Agent,
    bool IsSupported,
    bool IsRawText,
    string? HistoryState,
    IReadOnlyList<PushedTurn> Turns);

/// <summary>
/// Reads one session's conversation THROUGH THE ONE RESOLVER (<see cref="SessionHistoryReader"/>, which
/// follows the hook-reported transcript pointer) and shapes it for the push. This is the only place the
/// Director turns a transcript into pushed turns; the Gateway never asks for the transcript again.
/// </summary>
public static class TurnPushBuilder
{
    /// <summary>The session's conversation right now. Never throws for an unsupported agent - it answers a
    /// head-only snapshot so the Gateway can say "unsupported" exactly as the Director's own history read
    /// did.</summary>
    public static TurnSnapshot Snapshot(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var sessionId = session.Id.ToString();
        var agent = session.AgentKind.ToString();
        if (!SessionHistoryReader.IsSupported(session))
            return new TurnSnapshot(sessionId, GenerationFor(session, null), agent, IsSupported: false, IsRawText: false, HistoryState: null, Turns: Array.Empty<PushedTurn>());

        // Resolved ONCE and used three times - as the generation, as the file the messages are read from,
        // and as the file the history state is derived from - so a pointer that moves mid-snapshot cannot
        // label one file's messages with another file's identity (found in review).
        var path = SessionHistoryReader.ResolveTranscriptPath(session);
        var history = SessionHistoryReader.Read(session, path);
        // The transcript-derived history state (the background-agent lifecycle signal) lives in the Claude
        // transcript format and needs process liveness, which only the Director has - so it is computed HERE,
        // at push time, and carried on the batch for the Gateway to serve verbatim.
        string? historyState = null;
        if (session.AgentKind == AgentKind.ClaudeCode)
            historyState = HistoryStateDeriver.DeriveFromFile(path, session.Backend.IsRunning).ToString();

        return new TurnSnapshot(
            sessionId,
            GenerationFor(session, path),
            agent,
            IsSupported: true,
            IsRawText: session.AgentKind == AgentKind.Gemini,
            historyState,
            Map(history.Messages));
    }

    /// <summary>
    /// The generation: the transcript source's identity. For a file-backed agent that is the resolved
    /// path - it changes on /clear and when the agent moves into a worktree, which is exactly when the
    /// conversation the Gateway holds must start over. Agents whose conversation is not a per-session file
    /// (Gemini reads its own terminal buffer; Copilot and OpenCode read a store by repository) have one
    /// generation for the life of the session, named by the session id.
    /// </summary>
    internal static string GenerationFor(Session session, string? transcriptPath)
        => session.AgentKind is AgentKind.Gemini or AgentKind.Copilot or AgentKind.OpenCode || string.IsNullOrEmpty(transcriptPath)
            ? "session:" + session.Id
            : transcriptPath;

    /// <summary>Shape the reader's messages as pushed turns, ordinal = position. Pure, so it is tested
    /// without a session.</summary>
    internal static IReadOnlyList<PushedTurn> Map(IReadOnlyList<ConversationMessage> messages)
    {
        var turns = new List<PushedTurn>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            var turn = new PushedTurn
            {
                Ordinal = i,
                Role = m.Role.ToString(),
                Timestamp = m.Timestamp,
                ContextId = m.ContextId,
                IsMeta = m.IsMeta,
                IsSidechain = m.IsSidechain,
            };
            foreach (var p in m.Parts)
                turn.Parts.Add(new HistoryPartDto { Kind = p.Kind.ToString(), Text = p.Text, ToolName = p.ToolName, ToolId = p.ToolId });
            turns.Add(turn);
        }
        return turns;
    }
}
