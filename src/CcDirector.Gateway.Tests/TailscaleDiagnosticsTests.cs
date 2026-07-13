using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The server-side network diagnostic (Network Diagnostics mission). The parse helpers turn raw
/// tailscale CLI output into the direct-vs-relay + latency signal an agent reads; Collect stitches
/// status + ping + netcheck together with an injected CLI runner so no real tailscale is shelled.
/// </summary>
public sealed class TailscaleDiagnosticsTests
{
    [Fact]
    public void ParsePingResult_DirectLan_IsDirectWithLatency()
    {
        var (answered, direct, path, ms) = TailscaleDiagnostics.ParsePingResult(
            "pong from sorens-z-flip4 (100.86.144.11) via 192.168.1.15:52091 in 11ms");
        Assert.True(answered);
        Assert.True(direct);
        Assert.Equal("192.168.1.15:52091", path);
        Assert.Equal(11, ms);
    }

    [Fact]
    public void ParsePingResult_Derp_IsRelayed()
    {
        var (answered, direct, path, ms) = TailscaleDiagnostics.ParsePingResult(
            "pong from sorens-z-flip4 (100.86.144.11) via DERP(tor) in 84ms");
        Assert.True(answered);
        Assert.False(direct);
        Assert.Equal("DERP(tor)", path);
        Assert.Equal(84, ms);
    }

    [Fact]
    public void ParsePingResult_UsesLastPongLine()
    {
        // ping emits several lines; the settled path is the last one (relay -> upgraded to direct).
        var (answered, direct, path, _) = TailscaleDiagnostics.ParsePingResult(
            "pong from host (100.0.0.1) via DERP(tor) in 90ms\npong from host (100.0.0.1) via 192.168.1.15:41641 in 9ms\n");
        Assert.True(answered);
        Assert.True(direct);
        Assert.Equal("192.168.1.15:41641", path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ping 'host' did not respond after 3 attempts")]
    [InlineData("no matching peer")]
    public void ParsePingResult_NoPong_NotAnswered(string stdout)
    {
        var (answered, _, _, _) = TailscaleDiagnostics.ParsePingResult(stdout);
        Assert.False(answered);
    }

    [Fact]
    public void ParseNetcheckText_ReadsUdpNatAndDerp()
    {
        var text = string.Join("\n", new[]
        {
            "Report:",
            "\t* UDP: true",
            "\t* IPv4: yes, 66.185.205.57:63769",
            "\t* MappingVariesByDestIP: false",
            "\t* PortMapping: UPnP, NAT-PMP",
            "\t* Nearest DERP: Toronto",
        });
        var (udp, mappingVaries, derp) = TailscaleDiagnostics.ParseNetcheckText(text);
        Assert.True(udp);
        Assert.False(mappingVaries);
        Assert.Equal("Toronto", derp);
    }

    [Fact]
    public void ParseNetcheckText_HardNat_UdpBlocked()
    {
        var text = "\t* UDP: false\n\t* MappingVariesByDestIP: true\n";
        var (udp, mappingVaries, derp) = TailscaleDiagnostics.ParseNetcheckText(text);
        Assert.False(udp);
        Assert.True(mappingVaries);
        Assert.Null(derp);
    }

    [Fact]
    public void Collect_StitchesStatusPingAndNetcheck()
    {
        const string statusJson = """
        {
          "BackendState": "Running",
          "Self": { "DNSName": "soren-north.taildb08ed.ts.net.", "TailscaleIPs": ["100.100.0.1"] },
          "Peer": {
            "nodekey:aaa": { "DNSName": "sorens-z-flip4.taildb08ed.ts.net.", "HostName": "sorens-z-flip4", "OS": "android", "Online": true, "TailscaleIPs": ["100.86.144.11"], "Relay": "tor", "CurAddr": "" }
          }
        }
        """;

        (bool, string, string) Run(string args)
        {
            if (args.StartsWith("status")) return (true, statusJson, "ok");
            if (args.StartsWith("ping")) return (true, "pong from sorens-z-flip4 (100.86.144.11) via 192.168.1.15:52091 in 11ms", "ok");
            if (args.StartsWith("netcheck")) return (true, "\t* UDP: true\n\t* MappingVariesByDestIP: false\n\t* Nearest DERP: Toronto", "ok");
            return (false, "", "unexpected");
        }

        var diag = TailscaleDiagnostics.Collect(Run);

        Assert.True(diag.TailscaleAvailable);
        Assert.Equal("Running", diag.BackendState);
        Assert.Equal("soren-north.taildb08ed.ts.net", diag.SelfName);
        Assert.True(diag.UdpOk);
        Assert.False(diag.MappingVariesByDestIp);
        Assert.Equal("Toronto", diag.NearestDerp);

        var peer = Assert.Single(diag.Peers);
        Assert.Equal("sorens-z-flip4.taildb08ed.ts.net", peer.Name);
        Assert.Equal("100.86.144.11", peer.TailscaleIp);
        Assert.True(peer.Online);
        // The live ping settled on a direct LAN path even though the status snapshot had only a relay.
        Assert.True(peer.Direct);
        Assert.Equal("192.168.1.15:52091", peer.Path);
        Assert.Equal(11, peer.LatencyMs);
    }

    [Fact]
    public void Collect_StatusFails_ReportsNote()
    {
        var diag = TailscaleDiagnostics.Collect(_ => (false, "", "tailscaled not running"));
        Assert.True(diag.TailscaleAvailable); // CLI exists on this box; the call itself failed
        Assert.Contains(diag.Notes, n => n.Contains("status failed"));
        Assert.Empty(diag.Peers);
    }
}
