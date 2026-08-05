using CcDirector.ControlApi;
using CcDirector.Core.Network;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The troubleshooting ladder, rebuilt for the outbound-only Director (Remove-the-network-port
/// mission, phase 5). The old ladder diagnosed the inbound model - Tailscale up, Serve mapping
/// present, local listener answering, advertised URL dialling back - and every one of those
/// questions died with that model, so its rungs could only fail on healthy machines. The new
/// ladder asks what can actually be wrong: is a Gateway configured, does it answer from here,
/// and is the tunnel connected. Rungs run in diagnosis order, the FIRST failing rung is the
/// verdict (later rungs skip), and versions stay informational and last. Fully seam-driven -
/// no sockets.
/// </summary>
public sealed class GatewayConnectivitySelfTestTests
{
    private const string DirectorId = "11111111-2222-3333-4444-555555555555";
    private const string GatewayUrl = "http://127.0.0.1:7878";

    private static GatewayConnectivitySelfTest Make(
        string? gatewayUrl = GatewayUrl,
        GatewayConnectionStatus tunnel = GatewayConnectionStatus.Connected,
        Func<string, CancellationToken, Task<(bool, string)>>? httpProbe = null)
        => new(DirectorId, gatewayUrl, () => tunnel)
        {
            HttpProbe = httpProbe ?? ((_, _) => Task.FromResult((true, "{\"status\":\"ok\"}"))),
        };

    private static async Task<List<LadderRung>> RunAll(GatewayConnectivitySelfTest test)
    {
        var rungs = new List<LadderRung>();
        await foreach (var r in test.RunAsync())
            rungs.Add(r);
        return rungs;
    }

    [Fact]
    public async Task EverythingHealthy_AllChecksPass_VersionsInfoLast()
    {
        var rungs = await RunAll(Make());

        Assert.Equal(4, rungs.Count);
        Assert.All(rungs.Take(3), r => Assert.Equal(RungStatus.Pass, r.Status));
        Assert.Equal(RungStatus.Info, rungs[3].Status); // versions, last by design
        Assert.Contains("versions", rungs[3].Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoGatewayConfigured_FailsRungOne_SkipsTheRest()
    {
        var rungs = await RunAll(Make(gatewayUrl: null,
            httpProbe: (_, _) => throw new InvalidOperationException("no probe may run with nothing configured")));

        Assert.Equal(RungStatus.Fail, rungs[0].Status);
        Assert.Contains("Settings", rungs[0].Fix);
        Assert.Equal(RungStatus.Skipped, rungs[1].Status);
        Assert.Equal(RungStatus.Skipped, rungs[2].Status);
    }

    [Fact]
    public async Task GatewayDoesNotAnswer_FailsRungTwo_AsANetworkOrGatewayProblem()
    {
        var rungs = await RunAll(Make(httpProbe: (_, _) => Task.FromResult((false, "timeout after 5s"))));

        Assert.Equal(RungStatus.Pass, rungs[0].Status);
        Assert.Equal(RungStatus.Fail, rungs[1].Status);
        Assert.Contains("timeout after 5s", rungs[1].Found);
        Assert.Equal(RungStatus.Skipped, rungs[2].Status);
    }

    [Fact]
    public async Task GatewayAnswersButTunnelDown_FailsRungThree_NamingTheToken()
    {
        var rungs = await RunAll(Make(tunnel: GatewayConnectionStatus.Failed));

        Assert.Equal(RungStatus.Pass, rungs[1].Status);
        Assert.Equal(RungStatus.Fail, rungs[2].Status);
        Assert.Contains("refusing", rungs[2].Found, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TunnelStillConnecting_FailsRungThree_WithAWaitAndRetryFix()
    {
        var rungs = await RunAll(Make(tunnel: GatewayConnectionStatus.Connecting));

        Assert.Equal(RungStatus.Fail, rungs[2].Status);
        Assert.Contains("CONNECTING", rungs[2].Found);
        Assert.Contains("re-run", rungs[2].Fix);
    }

    [Fact]
    public async Task TheVersionsRung_SaysAFirewallCanNeverBeTheCause()
    {
        // The old ladder ended on a firewall rung because an inbound port could be blocked. There is
        // no inbound anything now; the ladder says so rather than leaving the reader to wonder.
        var rungs = await RunAll(Make());

        Assert.Contains("firewall", rungs[3].Found, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no inbound", rungs[3].Found, StringComparison.OrdinalIgnoreCase);
    }

    // ===== ParseBackendState (pure; TailscaleIdentity is still used by the tailnet identity reads) =====

    [Theory]
    [InlineData("""{ "BackendState": "Running" }""", "Running")]
    [InlineData("""{ "BackendState": "Stopped" }""", "Stopped")]
    [InlineData("""{ "BackendState": "NeedsLogin" }""", "NeedsLogin")]
    [InlineData("""{ "Self": {} }""", null)]
    [InlineData("""{ "BackendState": "" }""", null)]
    [InlineData("""{ "BackendState": 42 }""", null)]
    public void ParseBackendState_ExtractsOrNull(string json, string? expected)
        => Assert.Equal(expected, TailscaleIdentity.ParseBackendState(json));
}
