using CcDirector.Core.Agents;
using CcDirector.Core.History;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// GET /sessions/{sid}/history - the parsed, agent-agnostic conversation history for a session.
/// Reuses Core's <see cref="SessionHistoryReader"/> so every supported agent is covered, then maps
/// the normalized <c>ConversationHistory</c> into the wire <see cref="SessionHistoryDto"/> the
/// Cockpit reads. Also computes the transcript-derived history state (<see cref="HistoryStateDeriver"/>)
/// so the Cockpit can show the same experimental label as the desktop History tab without
/// re-reading the transcript itself. The Gateway forwards this verbatim through its generic
/// <c>/sessions/{sid}/{**rest}</c> proxy, so no Gateway change is needed.
/// </summary>
internal static class SessionHistoryEndpoint
{
    public static void Map(IEndpointRouteBuilder app, SessionManager sessionManager, string directorId)
    {
        // Gateway Cleanup Phase 0: the read runs through the shared SessionReadExecutor core (verb
        // "history"), which calls the SAME BuildHistory mapper below, so this REST path and the Gateway
        // stream down-channel are identical and cannot drift. The route's read-fault 500 is preserved by the
        // core and mapped back here. Phase 1 deletes this route.
        app.MapGet("/sessions/{sid}/history", async (string sid) =>
        {
            FileLog.Write($"[SessionHistoryEndpoint] GET /sessions/{sid}/history");
            var command = new DirectorCommand { Verb = "history", SessionId = sid };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<SessionHistoryDto>(result.BodyJson)),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "history command failed"),
            };
        });
    }

    /// <summary>
    /// Build the wire DTO from a session. Pure mapping over the Core reader and deriver - no I/O
    /// of its own beyond what those perform. Internal so it is unit-testable without the host.
    /// </summary>
    internal static SessionHistoryDto BuildHistory(Session session, string sid)
    {
        var dto = new SessionHistoryDto
        {
            SessionId = sid,
            Agent = session.AgentKind.ToString(),
            IsSupported = SessionHistoryReader.IsSupported(session),
            // Gemini has no structured transcript; its history is raw terminal scrollback that the
            // Cockpit must render verbatim, not as Markdown (mirrors the desktop IsRawText path).
            IsRawText = session.AgentKind == AgentKind.Gemini,
        };

        if (!dto.IsSupported)
        {
            dto.Status = "unsupported";
            return dto;
        }

        // Fail loudly, not silently: a Claude session whose transcript file cannot be located
        // would otherwise return an EMPTY history with Status "ok" - indistinguishable from a
        // genuinely empty conversation, which starves every consumer (Cockpit history, Gateway
        // voice mode) with no diagnosable signal. Typical cause: the session-pointer hook has
        // not reported yet, so the Director only holds the launch-time session id, which goes
        // stale on /clear and auto-compaction.
        if (session.AgentKind == AgentKind.ClaudeCode)
        {
            var transcriptPath = SessionHistoryReader.ResolveTranscriptPath(session);
            if (transcriptPath is null || !File.Exists(transcriptPath))
            {
                dto.Status = "transcript-not-found";
                dto.Error = transcriptPath is null
                    ? "No transcript path is known for this session yet (the session-pointer hook has not reported)."
                    : $"The transcript file this session points at does not exist: {transcriptPath}";
                FileLog.Write($"[SessionHistoryEndpoint] transcript-not-found: sid={sid} triedPath={transcriptPath ?? "(null)"}");
                return dto;
            }
        }

        var history = SessionHistoryReader.Read(session);
        foreach (var message in history.Messages)
        {
            var msg = new HistoryMessageDto
            {
                Role = message.Role.ToString(),
                Timestamp = message.Timestamp,
            };
            foreach (var part in message.Parts)
            {
                msg.Parts.Add(new HistoryPartDto
                {
                    Kind = part.Kind.ToString(),
                    Text = part.Text,
                    ToolName = part.ToolName,
                    ToolId = part.ToolId,
                });
            }
            dto.Messages.Add(msg);
        }

        // Transcript-derived history state (#736 / #741): Claude only - the background-agent
        // lifecycle signal lives in the Claude transcript format. Computed here because
        // process-liveness (Backend.IsRunning) is known only Director-side. This NEVER reads or
        // writes the live byte-based status; it is a separate, additive label.
        if (session.AgentKind == AgentKind.ClaudeCode)
        {
            var path = SessionHistoryReader.ResolveTranscriptPath(session);
            var analysis = HistoryStateDeriver.AnalyzeFile(path);
            dto.HistoryState = HistoryStateDeriver.Derive(analysis, session.Backend.IsRunning).ToString();
        }

        return dto;
    }
}
