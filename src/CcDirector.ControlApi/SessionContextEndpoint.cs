using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// GET /sessions/{sid}/context - how full the session's context window is right now
/// (<see cref="Gateway.Contracts.ContextUsageDto"/>), via the session driver's
/// <see cref="IAgentDriver.ReadContextUsage"/>. This is the always-visible "context gauge" data:
/// used tokens, and where the model's window is known, the window size and percent. Only available
/// for a driver that declares <see cref="DriverCapabilities.ContextUsage"/> (Claude today); any
/// other agent returns 404, mirroring how the desktop gauge is capability-gated.
/// </summary>
internal static class SessionContextEndpoint
{
    public static void Map(IEndpointRouteBuilder app, SessionManager sessionManager, string directorId)
    {
        // Gateway Cleanup Phase 0: the read runs through the shared SessionReadExecutor core (verb
        // "context") so this REST path and the Gateway stream down-channel are identical and cannot drift.
        // The route's capability/no-turn 404s and its read-fault 500 are preserved by the core and mapped
        // back here. Phase 1 deletes this route.
        app.MapGet("/sessions/{sid}/context", async (string sid) =>
        {
            var command = new DirectorCommand { Verb = "context", SessionId = sid };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command);

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Json(SessionCommandExecutor.Deserialize<ContextUsageDto>(result.BodyJson)),
                DirectorCommandStatus.BadRequest => Results.BadRequest(new { error = result.Error }),
                DirectorCommandStatus.NotFound => Results.NotFound(new { error = result.Error }),
                _ => Results.Problem(result.Error ?? "context command failed"),
            };
        });
    }
}
