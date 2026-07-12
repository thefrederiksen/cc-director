using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps the machine-facts surface (issue #330, plan 1B):
///
///   GET /facts -> the cc-* tool inventory (names + versions) and the launcher
///                 presence/port fact, served deterministically at request time.
///
/// This is the fleet-facing inventory the Gateway pulls through its proxy leg
/// (GET /directors/{id}/facts) - the "Director emits/serves everything the hub will
/// need" half of Phase 1. Loopback-only and subject to the host's auth middleware,
/// exactly like the other routes.
///
/// Gateway Cleanup mission, Phase 0 (wave 3): the inventory is built by the shared
/// <see cref="CatalogReadExecutor.Facts"/> core, reached here through the tunnel dispatch, so this REST
/// route and the Gateway stream down-channel are byte-identical and cannot drift. The Director version -
/// the one dependency the tunnel command surface did not carry - is passed in through
/// <see cref="SessionCommandServices.DirectorVersion"/>. Phase 1 deletes this route and leaves the core
/// reached only over the tunnel.
/// </summary>
internal static class FactsEndpoint
{
    public static void Map(IEndpointRouteBuilder app, SessionManager sessionManager, string directorId, string version)
    {
        app.MapGet("/facts", async () =>
        {
            FileLog.Write("[FactsEndpoint] GET /facts");
            var command = new DirectorCommand { Verb = "facts", SessionId = "" };
            var result = await SessionCommandExecutor.DispatchAsync(sessionManager, directorId, command,
                new SessionCommandServices { DirectorVersion = version });

            return result.Status switch
            {
                DirectorCommandStatus.Ok => Results.Content(result.BodyJson ?? "{}", "application/json"),
                _ => Results.Problem(result.Error ?? "facts command failed"),
            };
        });
    }
}
