using CcDirector.Core.GatewayConnection;
using CcDirector.Core.Network;
using Xunit;

namespace CcDirector.Core.Tests.GatewayConnection;

/// <summary>
/// Proves the pure candidate-ordering of the Step 1 scan (design spec section 5): the issue-1233 discovery
/// order (this machine first, then the tailnet), de-duplicated, with recognizable labels. The reachability
/// probe is I/O and is proven by the running-app screenshots in the Phase 1 proof; this pins the ordering
/// logic that decides which candidates are tried and in what priority.
/// </summary>
public sealed class GatewayScanServiceTests
{
    [Fact]
    public void BuildCandidates_PutsThisMachineBeforeTailnet()
    {
        var candidates = GatewayScanService.BuildCandidates(
            machineName: "SOREN_NORTH",
            tailnetHosts: new[] { "soren-north.tailnet.ts.net" },
            gatewayPort: 7878);

        // The first candidate is always a this-machine (loopback) pick; the tailnet host follows.
        Assert.Equal(GatewayLocationKind.ThisMachine, candidates[0].Kind);
        Assert.Equal(GatewayLocationKind.Tailnet, candidates[^1].Kind);
        Assert.Contains(candidates, c => c.Kind == GatewayLocationKind.Tailnet
            && c.Url == "http://soren-north.tailnet.ts.net:7878");
    }

    [Fact]
    public void BuildCandidates_ThisMachineLabelNamesTheMachine()
    {
        var candidates = GatewayScanService.BuildCandidates("SOREN_NORTH", Array.Empty<string>());

        Assert.All(candidates, c => Assert.Equal(GatewayLocationKind.ThisMachine, c.Kind));
        Assert.All(candidates, c => Assert.Contains("SOREN_NORTH", c.Label));
        Assert.All(candidates, c => Assert.Contains("this machine", c.Label));
    }

    [Fact]
    public void BuildCandidates_DropsDuplicateUrls_KeepingFirst()
    {
        // A tailnet host that happens to resolve to a loopback URL already present must not appear twice.
        var loopback = EndpointProbe.LocalGatewayCandidates(7878)[0];
        var dupHost = new Uri(loopback).Host; // e.g. "localhost" or "127.0.0.1"

        var candidates = GatewayScanService.BuildCandidates(
            machineName: "M",
            tailnetHosts: new[] { dupHost },
            gatewayPort: 7878);

        var matching = candidates.Where(c => c.Url.Equals(loopback, System.StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(matching);
        // The surviving entry is the higher-priority this-machine one, not the tailnet duplicate.
        Assert.Equal(GatewayLocationKind.ThisMachine, matching[0].Kind);
    }

    [Fact]
    public void BuildCandidates_NullOrBlankTailnetHosts_AreIgnored()
    {
        var candidates = GatewayScanService.BuildCandidates(
            machineName: "M",
            tailnetHosts: new[] { "", "  ", "real.ts.net" },
            gatewayPort: 7878);

        Assert.Single(candidates, c => c.Kind == GatewayLocationKind.Tailnet);
        Assert.Contains(candidates, c => c.Url == "http://real.ts.net:7878");
    }
}
