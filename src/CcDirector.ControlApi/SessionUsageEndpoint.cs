using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// GET /sessions/{sid}/usage - the session's token usage, computed mechanically from its
/// Claude Code JSONL transcript (every assistant line carries a usage block). Feeds the
/// Cockpit's session story panel: session totals, current context size, per-turn deltas.
///
/// Gateway Cleanup Phase 0: the computation now lives in the shared <see cref="SessionReadExecutor"/>
/// core (verb <c>usage</c>); this route just dispatches to it, so the REST path and the Gateway stream
/// down-channel are identical and cannot drift.
/// </summary>
internal static class SessionUsageEndpoint
{
    public static void Map(IEndpointRouteBuilder app, SessionManager sessionManager, string directorId)
    {
        app.MapGet("/sessions/{sid}/usage", async (string sid) =>
        {
            var command = new DirectorCommand { Verb = "usage", SessionId = sid };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<SessionUsageDto>(result.BodyJson)),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "usage command failed"),
            };
        });
    }
}
