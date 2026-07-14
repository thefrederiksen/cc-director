using System.Net;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1548: a mission-scoped spawn failed with "unknown mission '&lt;id&gt;'. Create it first with
/// POST /missions." for a mission that already existed. Missions live at the GATEWAY (the source of truth,
/// what `cc-devthrottle mission list` reads). The Director's LOCAL spawn leg never consulted it: the CLI can
/// only send a mission id, so the create request reached the Director floor with the NAME blank, fell through
/// to the Director's TEMPORARY local-store bridge, and was rejected against the wrong store. The REMOTE leg
/// never had this bug - it exits through the Gateway, whose POST /machines/{machine}/sessions resolves the
/// name for it. <see cref="GatewayClient.GetMissionAsync"/> is what closes that asymmetry.
///
/// These tests drive the REAL <see cref="GatewayClient"/> over a REAL HTTP connection to a Kestrel stub on a
/// loopback port (the pattern the token-refresh tests use), so the wire behavior - status codes, the 404, a
/// server error - is exercised rather than a hand-written fake that is politer than the transport.
///
/// The last fact is the point of the whole issue: an unreachable or broken Gateway must THROW, never return
/// null. Null means "the Gateway answered, and it genuinely has no such mission." Collapsing a transport
/// failure into null would put the original lie back - telling a human to create a mission that exists.
/// </summary>
public sealed class GatewayClientMissionLookupTests
{
    /// <summary>
    /// A Kestrel stub standing in for the Gateway's GET /missions/{mid}, on an OS-assigned loopback port.
    /// <paramref name="handler"/> returns the response for a requested mission id.
    /// </summary>
    private static async Task<(WebApplication app, string url)> StartMissionStubAsync(Func<string, IResult> handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));   // OS-assigned free port
        var app = builder.Build();
        app.MapGet("/missions/{mid}", (string mid) => handler(mid));
        await app.StartAsync();
        var url = app.Urls.First();
        return (app, url);
    }

    private static GatewayClient ClientFor(string gatewayUrl) =>
        new(new GatewayConfig { Url = gatewayUrl }, Guid.NewGuid().ToString(), 7879, "1.0.0");

    [Fact]
    public async Task GetMissionAsync_knownMission_resolvesTheName()
    {
        var missionId = Guid.NewGuid();
        var (app, url) = await StartMissionStubAsync(_ => Results.Json(new MissionDto
        {
            MissionId = missionId,
            MissionName = "Stable Release",
        }));

        try
        {
            var mission = await ClientFor(url).GetMissionAsync(missionId);

            Assert.NotNull(mission);
            Assert.Equal(missionId, mission!.MissionId);
            Assert.Equal("Stable Release", mission.MissionName);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task GetMissionAsync_unknownMission_returnsNull_soTheCallerCanSayUnknown()
    {
        var (app, url) = await StartMissionStubAsync(_ => Results.NotFound(new { error = "mission not found" }));

        try
        {
            Assert.Null(await ClientFor(url).GetMissionAsync(Guid.NewGuid()));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task GetMissionAsync_gatewayError_throws_neverNull_soAFailureIsNotReportedAsUnknownMission()
    {
        // The heart of #1548. A 500 means the Gateway could not answer - it does NOT mean the mission is
        // absent. Returning null here would make the caller say "unknown mission", the exact lie.
        var (app, url) = await StartMissionStubAsync(_ => Results.StatusCode(500));

        try
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ClientFor(url).GetMissionAsync(Guid.NewGuid()));
            Assert.Contains("could not look up mission", ex.Message);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task GetMissionAsync_unreachableGateway_throws_neverNull()
    {
        // Nothing is listening on this port: a transport failure, again not an absent mission.
        var client = ClientFor("http://127.0.0.1:1");

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetMissionAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetMissionAsync_noGatewayConfigured_throws_ratherThanClaimingTheMissionIsUnknown()
    {
        var client = new GatewayClient(new GatewayConfig(), Guid.NewGuid().ToString(), 7879, "1.0.0");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetMissionAsync(Guid.NewGuid()));
        Assert.Contains("Gateway is not configured", ex.Message);
    }
}
