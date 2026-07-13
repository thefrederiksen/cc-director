using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The server-side monitor's per-device Tick core (Network Diagnostics mission, Phase 1). Verifies the
/// monitor contracts: offline peers gate to Unknown before Decide; direct-vs-relay comes from the peer's
/// ping-derived Direct (never Relay); and HomeLanPresent for a relaying device requires an ARP probe that
/// resolves the SAME cached MAC (with short smoothing) - so a present home device drifts but an absent /
/// mismatched one never alerts.
/// </summary>
public sealed class NetDiagMonitorTests
{
    private static readonly DateTime T0 = new(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
    private const string PhoneIp = "100.86.144.11";

    private static NetDiagMonitor NewMonitor() => new(() => throw new InvalidOperationException("collector unused"), _ => null);

    private static TailscaleDiagnostics.NetworkDiag Diag(bool? direct, string? path, double? latency, bool online = true) =>
        new()
        {
            TailscaleAvailable = true,
            BackendState = "Running",
            Peers = new()
            {
                new TailscaleDiagnostics.PeerDiag
                {
                    Name = "phone", TailscaleIp = PhoneIp, Os = "android",
                    Online = online, Direct = direct, Path = path, LatencyMs = latency,
                },
            },
        };

    // Prime a known baseline: 5 LAN-direct sightings (resolver returns a stable MAC).
    private static void PrimeBaseline(NetDiagMonitor m)
    {
        for (int i = 0; i < NetDiagDrift.MinBaselineSamples; i++)
            m.Tick(Diag(true, "192.168.1.15:52091", 44), _ => "aa-bb-cc-dd-ee-ff", T0);
    }

    // ---- pure helpers ----

    [Theory]
    [InlineData("192.168.1.15:52091", true)]
    [InlineData("10.0.0.5:41641", true)]
    [InlineData("172.16.9.9:1", true)]
    [InlineData("8.8.8.8:443", false)]
    [InlineData("DERP(tor)", false)]
    [InlineData(null, false)]
    public void IsLanPath_ClassifiesPaths(string? path, bool expected)
        => Assert.Equal(expected, NetDiagMonitor.IsLanPath(path));

    [Theory]
    [InlineData("192.168.1.15:52091", "192.168.1.15")]
    [InlineData("10.0.0.5:41641", "10.0.0.5")]
    [InlineData("DERP(tor)", null)]
    public void ExtractIp_StripsPort(string path, string? expected)
        => Assert.Equal(expected, NetDiagMonitor.ExtractIp(path));

    [Fact]
    public void NormalizeMac_FormatsLowerHexDashed()
        => Assert.Equal("82-68-dc-31-50-33", LanPresenceProbe.NormalizeMac(new byte[] { 0x82, 0x68, 0xdc, 0x31, 0x50, 0x33 }));

    // ---- monitor contracts ----

    [Fact]
    public void OfflinePeer_GatesToUnknown()
    {
        var m = NewMonitor();
        PrimeBaseline(m);
        var d = m.Tick(Diag(false, "DERP(tor)", null, online: false), _ => "aa-bb-cc-dd-ee-ff", T0.AddMinutes(1));
        Assert.Equal("unknown", d.Single().decision.Status);
    }

    [Fact]
    public void OnlineButNotPinged_NullDirect_GatesToUnknown()
    {
        var m = NewMonitor();
        PrimeBaseline(m);
        // Online, but no ping verdict (Direct==null) and a status-fallback DERP path: must NOT judge.
        var d = m.Tick(Diag(null, "DERP(tor)", null), _ => "aa-bb-cc-dd-ee-ff", T0.AddMinutes(1));
        Assert.Equal("unknown", d.Single().decision.Status);
    }

    [Fact]
    public void PresentHomeDevice_RelayingPersistently_Drifts()
    {
        var m = NewMonitor();
        PrimeBaseline(m); // baseline now known, LastPresent = T0
        string? Present(string _) => "aa-bb-cc-dd-ee-ff"; // ARP resolves the SAME cached MAC = present

        var t1 = m.Tick(Diag(false, "DERP(tor)", null), Present, T0).Single().decision;
        Assert.Equal("suspect", t1.Status);
        var t2 = m.Tick(Diag(false, "DERP(tor)", null), Present, T0.AddMinutes(3)).Single().decision;
        Assert.Equal("suspect", t2.Status);
        var t3 = m.Tick(Diag(false, "DERP(tor)", null), Present, T0.AddMinutes(6)).Single().decision;
        Assert.Equal("drifted", t3.Status);
        Assert.True(t3.ShouldAlert);
    }

    [Fact]
    public void Relaying_MacAbsent_BeyondSmoothing_NeverDrifts()
    {
        var m = NewMonitor();
        PrimeBaseline(m); // LastPresent = T0
        string? Absent(string _) => null; // ARP no longer resolves = device left the LAN

        // Beyond the 120s smoothing window, so the stale positive-presence cache does not apply.
        var t1 = m.Tick(Diag(false, "DERP(tor)", null), Absent, T0.AddSeconds(200)).Single().decision;
        var t2 = m.Tick(Diag(false, "DERP(tor)", null), Absent, T0.AddMinutes(4)).Single().decision;
        var t3 = m.Tick(Diag(false, "DERP(tor)", null), Absent, T0.AddMinutes(8)).Single().decision;
        Assert.Equal("unknown", t1.Status);
        Assert.Equal("unknown", t2.Status);
        Assert.Equal("unknown", t3.Status);
        Assert.False(t3.ShouldAlert);
    }

    [Fact]
    public void Relaying_DifferentMac_IsNotPresent()
    {
        var m = NewMonitor();
        PrimeBaseline(m);
        // A DIFFERENT device now answers at the cached IP (DHCP reassigned it) - not our device. Beyond the
        // smoothing window so the mismatch is decisive.
        var d = m.Tick(Diag(false, "DERP(tor)", null), _ => "ff-ff-ff-ff-ff-ff", T0.AddSeconds(200)).Single().decision;
        Assert.Equal("unknown", d.Status);
    }

    // Architect-flagged safety invariant: the presence-cache must age a departed device out well before
    // drift could fire, or "the user left the house" silently becomes a false alert.
    [Fact]
    public void PresenceCacheWindow_StaysBelowDriftFloor()
    {
        Assert.True(NetDiagMonitor.PresenceCacheWindow < NetDiagDrift.MinDriftDuration,
            "presence cache must be shorter than the 5-min drift floor");
        Assert.True(NetDiagMonitor.PresenceCacheWindow * 2 <= NetDiagDrift.MinDriftDuration,
            "keep a comfortable margin (>=2x) so a departed device can never reach Drifted+alert");
    }

    [Fact]
    public void CurrentlyLanDirect_WithBaseline_IsOk()
    {
        var m = NewMonitor();
        PrimeBaseline(m);
        var d = m.Tick(Diag(true, "192.168.1.15:52091", 44), _ => "aa-bb-cc-dd-ee-ff", T0.AddMinutes(1)).Single().decision;
        Assert.Equal("ok", d.Status);
    }
}
