using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

public sealed class RepoHistoryStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "ccd-repohist-" + Guid.NewGuid().ToString("N") + ".jsonl");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static RepoStatusDto Repo(string name, int worktrees = 0, int safe = 0, long bytes = 0,
        int uncommitted = 0, DateTime? dirtySince = null, bool provisional = false) => new()
    {
        Name = name,
        MachineName = "M1",
        Path = $@"D:\repos\{name}",
        WorktreeCount = worktrees,
        WorktreesSafeToReap = safe,
        WorktreeBytes = bytes,
        UncommittedCount = uncommitted,
        IsClean = uncommitted == 0,
        DirtySinceUtc = dirtySince,
        Provisional = provisional,
    };

    [Fact]
    public void Observe_IsIdempotentPerDay_LastWriteWins_AndPersists()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 23);

        var store = new RepoHistoryStore(_path);
        store.ObserveSnapshot(tenant, new[] { Repo("a", worktrees: 6, safe: 2) }, day);
        store.ObserveSnapshot(tenant, new[] { Repo("a", worktrees: 4, safe: 1) }, day); // same day again

        // A fresh store (new process) reads the persisted file: one row for the day, the LAST values.
        var reloaded = new RepoHistoryStore(_path);
        var trends = reloaded.WeeklyTrends(tenant, weeks: 1, today: day);
        Assert.Equal(4, trends[^1].MaxWorktrees);
        Assert.Equal(1, trends[^1].MaxSafeToReap);
    }

    [Fact]
    public void WeeklyTrends_PeaksPerWeek_AndEmptyWeeksAreZero()
    {
        var tenant = TenantId.Local;
        var store = new RepoHistoryStore(_path);
        var monday = new DateOnly(2026, 07, 20); // a Monday

        // Two weeks ago: heavy. This week: light. Last week: nothing.
        store.ObserveSnapshot(tenant, new[] { Repo("a", worktrees: 30, safe: 9, bytes: 9_000) }, monday.AddDays(-14));
        store.ObserveSnapshot(tenant, new[] { Repo("a", worktrees: 3, safe: 1, bytes: 1_000) }, monday);

        var trends = store.WeeklyTrends(tenant, weeks: 3, today: monday);
        Assert.Equal(3, trends.Count);
        Assert.Equal(30, trends[0].MaxWorktrees);   // oldest week
        Assert.Equal(0, trends[1].MaxWorktrees);    // the silent week reads as zero, not carried over
        Assert.Equal(3, trends[2].MaxWorktrees);    // this week
    }

    [Fact]
    public void DirtyOverThreshold_ListsOnlyTodaysOffenders()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 23);
        var store = new RepoHistoryStore(_path);
        store.ObserveSnapshot(tenant, new[]
        {
            Repo("old-mess", uncommitted: 434, dirtySince: DateTime.UtcNow.AddDays(-12)),
            Repo("fresh", uncommitted: 3, dirtySince: DateTime.UtcNow.AddDays(-1)),
        }, day);

        var dirty = store.DirtyOverThreshold(tenant, day);
        Assert.Equal("old-mess", Assert.Single(dirty).Name);
        Assert.True(dirty[0].DirtyDays >= 12);
    }

    [Fact]
    public void ProvisionalRows_NeverBecomeHistory()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 23);
        var store = new RepoHistoryStore(_path);
        store.ObserveSnapshot(tenant, new[] { Repo("cached", worktrees: 99, provisional: true) }, day);

        Assert.Equal(0, store.WeeklyTrends(tenant, 1, day)[^1].MaxWorktrees);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F13): the history key is the repository PATH, not the leaf
    // name. Two repositories sharing a folder name on one machine keep separate daily rows.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void SameLeafName_DifferentPaths_DoNotOverwriteEachOther()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 24);
        var store = new RepoHistoryStore(_path);

        var first = Repo("widget", worktrees: 5, safe: 2);
        first.Path = @"D:\repos\widget";
        var second = Repo("widget", worktrees: 3, safe: 1);
        second.Path = @"D:\other\widget";

        store.ObserveSnapshot(tenant, new[] { first, second }, day);

        // Both rows survive: the day's fleet totals SUM the two same-named repositories.
        var trends = store.WeeklyTrends(tenant, weeks: 1, today: day);
        Assert.Equal(8, trends[^1].MaxWorktrees);
        Assert.Equal(3, trends[^1].MaxSafeToReap);
    }

    // ---------------------------------------------------------------------------------------
    // REGRESSION (inspection finding F13): legacy rows without a path (the pre-path file
    // format, unreleased) cannot be keyed and are ignored on load - no migration.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void Load_IgnoresLegacyRowsWithoutAPath()
    {
        var day = new DateOnly(2026, 07, 24);
        File.WriteAllLines(_path, new[]
        {
            // A legacy line: no Path property at all.
            "{\"Tenant\":\"local\",\"Date\":\"2026-07-24\",\"MachineName\":\"M1\",\"Name\":\"widget\",\"WorktreeCount\":9}",
        });

        var store = new RepoHistoryStore(_path);
        Assert.Equal(0, store.WeeklyTrends(TenantId.Local, 1, day)[^1].MaxWorktrees);
    }

    [Fact]
    public void PathlessPushedRow_IsIgnored_NotKeyed()
    {
        var tenant = TenantId.Local;
        var day = new DateOnly(2026, 07, 24);
        var store = new RepoHistoryStore(_path);

        var pathless = Repo("ghost", worktrees: 9);
        pathless.Path = "";
        store.ObserveSnapshot(tenant, new[] { pathless }, day);

        Assert.Equal(0, store.WeeklyTrends(tenant, 1, day)[^1].MaxWorktrees);
    }

    [Fact]
    public void TenantPartition_TrendsNeverMix()
    {
        var a = new TenantId("tenant-a");
        var b = new TenantId("tenant-b");
        var day = new DateOnly(2026, 07, 23);
        var store = new RepoHistoryStore(_path);
        store.ObserveSnapshot(a, new[] { Repo("a-repo", worktrees: 7) }, day);
        store.ObserveSnapshot(b, new[] { Repo("b-repo", worktrees: 2) }, day);

        Assert.Equal(7, store.WeeklyTrends(a, 1, day)[^1].MaxWorktrees);
        Assert.Equal(2, store.WeeklyTrends(b, 1, day)[^1].MaxWorktrees);
    }
}
