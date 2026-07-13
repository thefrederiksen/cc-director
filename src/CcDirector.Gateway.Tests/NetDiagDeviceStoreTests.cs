using CcDirector.Gateway.Api;
using Xunit;
using static CcDirector.Gateway.Api.NetDiagDrift;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The durable per-device store (Network Diagnostics mission, Phase 1 / Architect guardrail C): persists
/// baseline good-samples + the (IP, MAC) presence identity across a restart - and DELIBERATELY not the
/// drift state - so a restart seeds baselines instantly (no re-warmup) while the drift-episode clock starts
/// clean.
/// </summary>
public sealed class NetDiagDeviceStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
    private const string PhoneIp = "100.86.144.11";

    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "cc-director-tests", "netdiagdev-" + Guid.NewGuid().ToString("N"), "netdiag-devices.json");

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static List<GoodSample> GoodSamples(int n) =>
        Enumerable.Range(0, n).Select(_ => new GoodSample(true, true, true, 44)).ToList();

    [Fact]
    public void SamplesAndPresenceIdentity_SurviveReload()
    {
        var store = new NetDiagDeviceStore(_path);
        store.Save(PhoneIp, GoodSamples(5), "192.168.1.15", "aa-bb-cc-dd-ee-ff");

        var reopened = new NetDiagDeviceStore(_path).LoadAll();
        Assert.True(reopened.ContainsKey(PhoneIp));
        Assert.Equal(5, reopened[PhoneIp].Samples.Count);
        Assert.Equal("192.168.1.15", reopened[PhoneIp].LanIp);
        Assert.Equal("aa-bb-cc-dd-ee-ff", reopened[PhoneIp].Mac);
    }

    [Fact]
    public void Save_KeepsOnlyHomeDirectLanSamples()
    {
        var mixed = new List<GoodSample>
        {
            new(true, true, true, 40),    // good
            new(true, true, true, 44),    // good
            new(false, true, true, 40),   // away
            new(true, false, false, 200), // relay
        };
        var store = new NetDiagDeviceStore(_path);
        store.Save(PhoneIp, mixed, "192.168.1.15", "aa");
        Assert.Equal(2, store.LoadAll()[PhoneIp].Samples.Count); // only the two good ones
    }

    [Fact]
    public void CorruptFile_IsQuarantined_NotCrashing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ not valid json");
        var store = new NetDiagDeviceStore(_path);
        Assert.Empty(store.LoadAll());
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(_path)!, "*.corrupt-*"));
    }

    // The payoff: seeding baselines from the store means a restart does NOT re-warmup - a device with a
    // persisted baseline that is relaying (and present) accrues drift on the FIRST tick instead of sitting
    // in Unknown for ~6 minutes. And the drift clock starts fresh (this first bad tick is only Suspect).
    [Fact]
    public void MonitorSeededFromStore_HasInstantBaseline_AndFreshDriftClock()
    {
        var store = new NetDiagDeviceStore(_path);
        store.Save(PhoneIp, GoodSamples(NetDiagDrift.MinBaselineSamples), "192.168.1.15", "aa-bb-cc-dd-ee-ff");

        var monitor = new NetDiagMonitor(() => throw new InvalidOperationException("unused"), _ => null, store);
        var diag = new TailscaleDiagnostics.NetworkDiag
        {
            TailscaleAvailable = true,
            BackendState = "Running",
            Peers = new()
            {
                new TailscaleDiagnostics.PeerDiag
                {
                    Name = "phone", TailscaleIp = PhoneIp, Os = "android",
                    Online = true, Direct = false, Path = "DERP(tor)", LatencyMs = null,
                },
            },
        };

        // First tick after a "restart": baseline is already known (seeded), device is present (MAC matches),
        // and it is relaying -> Suspect immediately (NOT Unknown-warmup), but only Suspect (fresh clock).
        var d = monitor.Tick(diag, _ => "aa-bb-cc-dd-ee-ff", T0).Single().decision;
        Assert.Equal("suspect", d.Status);
        Assert.False(d.ShouldAlert);
    }
}
