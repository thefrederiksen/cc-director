using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The hourly quality rollup (Network Diagnostics mission, Phase 1 / Decision 2b): one bucket per UTC hour
/// with a HOME (LAN-direct) vs AWAY (relay) split keyed on the MEASURED path, sums (not medians) so folds
/// are associative, 90-day retention, and durable across a restart.
/// </summary>
public sealed class NetDiagRollupStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 7, 13, 12, 30, 0, DateTimeKind.Utc);

    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "cc-director-tests", "netdiagroll-" + Guid.NewGuid().ToString("N"), "netdiag-rollup.json");

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Fold_HomeSample_LandsInLanSubSums()
    {
        var store = new NetDiagRollupStore(_path);
        store.Fold(TenantId.Local, T0, latencyMs: 44, direct: true, isLanPath: true, downMbps: null, upMbps: null);

        var b = Assert.Single(store.All(TenantId.Local));
        Assert.Equal(1, b.Count);
        Assert.Equal(1, b.DirectCount);
        Assert.Equal(0, b.RelayCount);
        Assert.Equal(1, b.LanCount);
        Assert.Equal(0, b.AwayCount);
        Assert.Equal(44, b.SumLatencyLan);
        Assert.Equal(44, b.MinLatencyLan);
    }

    [Fact]
    public void Fold_AwaySample_LandsInAwaySubSums_AndCountsRelay()
    {
        var store = new NetDiagRollupStore(_path);
        store.Fold(TenantId.Local, T0, latencyMs: 150, direct: false, isLanPath: false, downMbps: null, upMbps: null);

        var b = Assert.Single(store.All(TenantId.Local));
        Assert.Equal(1, b.RelayCount);
        Assert.Equal(1, b.AwayCount);
        Assert.Equal(0, b.LanCount);
        Assert.Equal(150, b.SumLatencyAway);
    }

    [Fact]
    public void Fold_ClientThroughput_AccumulatesDownUpInTheRightSplit()
    {
        var store = new NetDiagRollupStore(_path);
        store.Fold(TenantId.Local, T0, 40, true, isLanPath: true, downMbps: 90, upMbps: 10);
        store.Fold(TenantId.Local, T0, 42, true, isLanPath: true, downMbps: 80, upMbps: 8);

        var b = Assert.Single(store.All(TenantId.Local)); // same hour -> accumulates
        Assert.Equal(2, b.LanCount);
        Assert.Equal(170, b.SumDownLan);
        Assert.Equal(18, b.SumUpLan);
        Assert.Equal(82, b.SumLatencyLan);
        Assert.Equal(40, b.MinLatencyLan);
    }

    [Fact]
    public void Fold_SurvivesReload()
    {
        new NetDiagRollupStore(_path).Fold(TenantId.Local, T0, 44, true, true, null, null);
        var reopened = new NetDiagRollupStore(_path).All(TenantId.Local);
        Assert.Single(reopened);
        Assert.Equal(1, reopened[0].LanCount);
    }

    [Fact]
    public void Monitor_FoldsJudgedObservationIntoRollup()
    {
        var rollup = new NetDiagRollupStore(_path);
        var monitor = new NetDiagMonitor(
            () => throw new InvalidOperationException("unused"), _ => "aa-bb-cc-dd-ee-ff", deviceStore: null, rollup: rollup);
        var diag = new TailscaleDiagnostics.NetworkDiag
        {
            TailscaleAvailable = true,
            BackendState = "Running",
            Peers = new()
            {
                new TailscaleDiagnostics.PeerDiag
                {
                    Name = "phone", TailscaleIp = "100.86.144.11", Os = "android",
                    Online = true, Direct = true, Path = "192.168.1.15:52091", LatencyMs = 44,
                },
            },
        };

        monitor.Tick(diag, _ => "aa-bb-cc-dd-ee-ff", T0);

        var b = Assert.Single(rollup.All(TenantId.Local));
        Assert.Equal(1, b.LanCount);
        Assert.Equal(1, b.DirectCount);
        Assert.Equal(44, b.SumLatencyLan);
    }

    [Fact]
    public void Prune_DropsBucketsBeyondNinetyDays()
    {
        var store = new NetDiagRollupStore(_path);
        store.Fold(TenantId.Local, T0 - TimeSpan.FromDays(100), 44, true, true, null, null); // ancient
        store.Fold(TenantId.Local, T0, 44, true, true, null, null);                          // now -> prunes the ancient one

        var all = store.All(TenantId.Local);
        Assert.Single(all);
        Assert.Equal(NetDiagRollupStore.HourKey(T0), all[0].Hour);
    }
}
