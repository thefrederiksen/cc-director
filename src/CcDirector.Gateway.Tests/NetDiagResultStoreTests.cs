using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The durable network-results store (Network Diagnostics mission, Phase 1): newest-first, bounded,
/// and persisted to JSON with the CronRunHistoryStore atomic-write + quarantine contract, so results
/// survive a Gateway restart (the history the per-device baseline is built from).
/// </summary>
public sealed class NetDiagResultStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "cc-director-tests", "netdiag-" + Guid.NewGuid().ToString("N"), "diagnostics-results.json");

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path);
        if (dir is not null && Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    private static NetDiagResultDto Result(string tag) => new() { Verdict = tag };

    [Fact]
    public void Recent_IsNewestFirst()
    {
        var store = new NetDiagResultStore(_path);
        store.Add(Result("a"));
        store.Add(Result("b"));
        store.Add(Result("c"));
        Assert.Equal(new[] { "c", "b", "a" }, store.Recent().Select(r => r.Verdict));
    }

    [Fact]
    public void Add_EvictsOldestBeyondCapacity()
    {
        var store = new NetDiagResultStore(_path);
        for (int i = 0; i < NetDiagResultStore.MaxRecords + 5; i++)
            store.Add(Result($"r{i}"));

        var recent = store.Recent();
        Assert.Equal(NetDiagResultStore.MaxRecords, recent.Count);
        Assert.Equal($"r{NetDiagResultStore.MaxRecords + 4}", recent[0].Verdict);
        Assert.DoesNotContain(recent, r => r.Verdict == "r0");
    }

    [Fact]
    public void Results_SurviveReload()
    {
        var store = new NetDiagResultStore(_path);
        store.Add(new NetDiagResultDto { Verdict = "persisted", Route = "tailscale", LatencyMedianMs = 44, DownloadMbps = 90 });

        // A fresh store over the SAME file (simulating a Gateway restart) must see the persisted result.
        var reopened = new NetDiagResultStore(_path);
        var recent = reopened.Recent();
        var only = Assert.Single(recent);
        Assert.Equal("persisted", only.Verdict);
        Assert.Equal("tailscale", only.Route);
        Assert.Equal(44, only.LatencyMedianMs);
        Assert.Equal(90, only.DownloadMbps);
    }

    [Fact]
    public void CorruptFile_IsQuarantined_NotCrashing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, "{ this is not valid json");

        // Load must not throw; it quarantines the bad file and starts empty.
        var store = new NetDiagResultStore(_path);
        Assert.Empty(store.Recent());
        Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(_path)!, "*.corrupt-*"));

        // And it is usable afterward.
        store.Add(Result("after-quarantine"));
        Assert.Single(store.Recent());
    }

    [Fact]
    public void Recent_RespectsCount()
    {
        var store = new NetDiagResultStore(_path);
        for (int i = 0; i < 10; i++) store.Add(Result($"r{i}"));
        Assert.Equal(3, store.Recent(3).Count);
        Assert.Empty(store.Recent(0));
    }
}
