using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests for <see cref="GatewayClient"/>'s priority-order candidate selection (issue #1233):
/// before registering, the client narrows its active address to the first entry of
/// <see cref="GatewayConfig.CandidateUrls"/> that answers. The reachability probe is injected via
/// <see cref="GatewayClient.ProbeGatewayCandidate"/> so these run with no live gateway.
/// </summary>
public class GatewayClientCandidateSelectionTests
{
    private const string MachineUrl = "http://MACHINE:7878";
    private const string TailscaleUrl = "https://machine.tail.ts.net:7878";
    private const string IpUrl = "http://192.168.1.20:7878";

    private static GatewayClient NewClient(GatewayConfig cfg, params string[] reachable)
    {
        var ok = new HashSet<string>(reachable, StringComparer.OrdinalIgnoreCase);
        var client = new GatewayClient(cfg, Guid.NewGuid().ToString(), "9.9.9-test");
        client.ProbeGatewayCandidate = (url, _) =>
            Task.FromResult<string?>(ok.Contains(url) ? null : $"unreachable: {url}");
        return client;
    }

    [Fact]
    public async Task SelectActiveUrlAsync_SingleCandidate_IsNoOp()
    {
        using var client = NewClient(new GatewayConfig { Url = MachineUrl });

        await client.SelectActiveUrlAsync(CancellationToken.None);

        Assert.Equal(MachineUrl, client.ActiveUrl);
    }

    [Fact]
    public async Task SelectActiveUrlAsync_FirstCandidateReachable_KeepsIt()
    {
        var cfg = new GatewayConfig { Url = MachineUrl, Urls = new[] { TailscaleUrl, IpUrl } };
        using var client = NewClient(cfg, MachineUrl);

        await client.SelectActiveUrlAsync(CancellationToken.None);

        Assert.Equal(MachineUrl, client.ActiveUrl);
    }

    [Fact]
    public async Task SelectActiveUrlAsync_MachineNameDown_SwitchesToTailscale()
    {
        var cfg = new GatewayConfig { Url = MachineUrl, Urls = new[] { TailscaleUrl, IpUrl } };
        using var client = NewClient(cfg, TailscaleUrl, IpUrl);

        await client.SelectActiveUrlAsync(CancellationToken.None);

        Assert.Equal(TailscaleUrl, client.ActiveUrl);
    }

    [Fact]
    public async Task SelectActiveUrlAsync_OnlyIpReachable_SwitchesToIp()
    {
        var cfg = new GatewayConfig { Url = MachineUrl, Urls = new[] { TailscaleUrl, IpUrl } };
        using var client = NewClient(cfg, IpUrl);

        await client.SelectActiveUrlAsync(CancellationToken.None);

        Assert.Equal(IpUrl, client.ActiveUrl);
    }

    [Fact]
    public async Task SelectActiveUrlAsync_NoneReachable_LeavesActiveUrlUnchanged()
    {
        var cfg = new GatewayConfig { Url = MachineUrl, Urls = new[] { TailscaleUrl, IpUrl } };
        using var client = NewClient(cfg /* nothing reachable */);

        await client.SelectActiveUrlAsync(CancellationToken.None);

        Assert.Equal(MachineUrl, client.ActiveUrl);
    }
}
