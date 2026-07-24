using System;
using System.Collections.Generic;
using System.Linq;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Reports;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway's repo-state store (issue #2118). The claims that matter to the morning report:
///
///  - OVERWRITE, NOT APPEND: a re-push replaces the repository's row, so the report reads one moment in
///    time rather than a pile of snapshots it would have to date-sort itself.
///  - TENANT ISOLATION IN BOTH DIRECTIONS: one account cannot write into another's partition even when both
///    register the same repository path on identically-named machines, and cannot read the other's rows.
///  - WHOLE-BATCH VALIDATION: one malformed repository rejects the entire push, because a half-landed batch
///    would leave the report mixing this snapshot with the last one and calling it a single moment.
///  - STALENESS IS EXCLUSION, NOT AGEING: a Director that stopped pushing does not get its week-old picture
///    served as current - that is how a report comes to recommend deleting a worktree someone is using.
/// </summary>
public sealed class RepoStateStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    private static readonly TenantId Alice = new("tenant-alice");
    private static readonly TenantId Bob = new("tenant-bob");
    private static readonly DateTime T0 = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _h.Dispose();

    private RepoStateStore NewStore() => new(_h.Open(new AsyncLocalTenantContext()));

    private static RepoStateSnapshotDto Snapshot(
        string path, string? defaultBranch = "origin/main",
        IEnumerable<RepoStateBranchDto>? branches = null,
        IEnumerable<RepoStateWorktreeDto>? worktrees = null,
        DateTime? collectedAtUtc = null) => new()
    {
        Name = System.IO.Path.GetFileName(path),
        Path = path,
        CollectedAtUtc = collectedAtUtc ?? T0,
        DefaultBranch = defaultBranch,
        CurrentBranch = "main",
        IsDirty = false,
        Branches = branches?.ToList() ?? new List<RepoStateBranchDto>(),
        Worktrees = worktrees?.ToList() ?? new List<RepoStateWorktreeDto>(),
    };

    private static RepoStateBranchDto Branch(string name, bool? merged = false, int ahead = 1) => new()
    {
        Name = name,
        TipCommitUtc = T0.AddDays(-3),
        CommitsAheadOfDefault = ahead,
        MergedIntoDefault = merged,
        CheckedOut = false,
    };

    // ---- store and read back ---------------------------------------------------------------------------

    [Fact]
    public void A_pushed_snapshot_reads_back_whole()
    {
        var store = NewStore();
        store.StoreBatch(Alice, "dir-1", "SOREN", new[]
        {
            Snapshot("D:/repos/one", branches: new[] { Branch("feature-a"), Branch("done", merged: true, ahead: 0) },
                worktrees: new[]
                {
                    new RepoStateWorktreeDto
                    {
                        Path = "D:/wt/one-a", Branch = "feature-a", TipCommitUtc = T0.AddDays(-9),
                        LastActivityUtc = T0.AddDays(-8), IsDirty = true, BranchMergedIntoDefault = false,
                        HasLiveSession = false,
                    },
                }),
        }, T0);

        var stored = Assert.Single(store.ReadFresh(Alice, TimeSpan.FromHours(12), T0));
        Assert.Equal("dir-1", stored.DirectorId);
        Assert.Equal("SOREN", stored.MachineName);
        Assert.Equal("D:/repos/one", stored.Path);
        Assert.Equal("origin/main", stored.DefaultBranch);
        Assert.Equal(T0, stored.ReceivedAtUtc);

        Assert.Equal(2, stored.Branches.Count);
        Assert.True(Assert.Single(stored.Branches, b => b.Name == "done").MergedIntoDefault);
        Assert.False(Assert.Single(stored.Branches, b => b.Name == "feature-a").MergedIntoDefault);

        var worktree = Assert.Single(stored.Worktrees);
        Assert.Equal("D:/wt/one-a", worktree.Path);
        Assert.True(worktree.IsDirty);
        Assert.Equal(T0.AddDays(-8), worktree.LastActivityUtc);
    }

    [Fact]
    public void A_null_merged_verdict_survives_the_round_trip_as_null()
    {
        // The distinction the whole feed rests on: "not determined" must not come back as false, which a
        // report would read as "this branch definitely has unmerged work".
        var store = NewStore();
        store.StoreBatch(Alice, "dir-1", "SOREN", new[]
        {
            Snapshot("D:/repos/lonely", defaultBranch: null, branches: new[] { Branch("whatever", merged: null) }),
        }, T0);

        var stored = Assert.Single(store.ReadFresh(Alice, TimeSpan.FromHours(12), T0));
        Assert.Null(stored.DefaultBranch);
        Assert.Null(Assert.Single(stored.Branches).MergedIntoDefault);
    }

    [Fact]
    public void A_re_push_OVERWRITES_the_repositorys_row_rather_than_appending()
    {
        var store = NewStore();
        store.StoreBatch(Alice, "dir-1", "SOREN",
            new[] { Snapshot("D:/repos/one", branches: new[] { Branch("old-branch") }) }, T0);
        store.StoreBatch(Alice, "dir-1", "SOREN",
            new[] { Snapshot("D:/repos/one", branches: new[] { Branch("new-branch") }) }, T0.AddHours(6));

        var stored = Assert.Single(store.ReadFresh(Alice, TimeSpan.FromDays(1), T0.AddHours(6)));
        Assert.Equal(T0.AddHours(6), stored.ReceivedAtUtc);
        Assert.Equal("new-branch", Assert.Single(stored.Branches).Name);
    }

    [Fact]
    public void Two_Directors_on_one_account_keep_separate_rows_for_the_same_path()
    {
        // Two machines can genuinely hold a checkout at the same path. They are different facts about
        // different machines, and collapsing them would silently discard one machine's hygiene.
        var store = NewStore();
        store.StoreBatch(Alice, "dir-1", "SOREN", new[] { Snapshot("D:/repos/one") }, T0);
        store.StoreBatch(Alice, "dir-2", "SOREN-NORTH", new[] { Snapshot("D:/repos/one") }, T0);

        var stored = store.ReadFresh(Alice, TimeSpan.FromHours(12), T0);
        Assert.Equal(2, stored.Count);
        Assert.Equal(new[] { "dir-1", "dir-2" }, stored.Select(s => s.DirectorId).OrderBy(x => x));
    }

    // ---- tenant isolation ------------------------------------------------------------------------------

    [Fact]
    public void One_accounts_push_never_lands_in_or_overwrites_anothers_row()
    {
        // The hardest shape: the SAME director id and the SAME repository path in two accounts.
        var store = NewStore();
        store.StoreBatch(Alice, "dir-1", "SOREN",
            new[] { Snapshot("D:/repos/one", branches: new[] { Branch("alice-branch") }) }, T0);
        store.StoreBatch(Bob, "dir-1", "SOREN",
            new[] { Snapshot("D:/repos/one", branches: new[] { Branch("bob-branch") }) }, T0);

        var alice = Assert.Single(store.ReadFresh(Alice, TimeSpan.FromHours(12), T0));
        var bob = Assert.Single(store.ReadFresh(Bob, TimeSpan.FromHours(12), T0));

        Assert.Equal("alice-branch", Assert.Single(alice.Branches).Name);
        Assert.Equal("bob-branch", Assert.Single(bob.Branches).Name);
    }

    [Fact]
    public void An_account_with_no_rows_reads_nothing_even_when_another_account_has_plenty()
    {
        var store = NewStore();
        store.StoreBatch(Alice, "dir-1", "SOREN",
            new[] { Snapshot("D:/repos/one"), Snapshot("D:/repos/two") }, T0);

        Assert.Empty(store.ReadFresh(Bob, TimeSpan.FromHours(12), T0));
    }

    // ---- validation ------------------------------------------------------------------------------------

    [Fact]
    public void One_malformed_repository_rejects_the_WHOLE_batch()
    {
        var store = NewStore();
        var batch = new[] { Snapshot("D:/repos/good"), Snapshot("") };

        Assert.Throws<RepoStateValidationException>(() => store.StoreBatch(Alice, "dir-1", "SOREN", batch, T0));
        // Nothing landed - not even the good one. A half-landed batch would mix two moments in time.
        Assert.Empty(store.ReadFresh(Alice, TimeSpan.FromHours(12), T0));
    }

    [Fact]
    public void A_push_naming_one_repository_twice_is_rejected()
    {
        // Two rows with the same key would make the stored answer depend on iteration order.
        var store = NewStore();
        var batch = new[] { Snapshot("D:/repos/one"), Snapshot("D:/repos/one") };

        Assert.Throws<RepoStateValidationException>(() => store.StoreBatch(Alice, "dir-1", "SOREN", batch, T0));
    }

    [Fact]
    public void A_push_without_a_director_id_is_rejected()
    {
        var store = NewStore();
        Assert.Throws<RepoStateValidationException>(
            () => store.StoreBatch(Alice, "  ", "SOREN", new[] { Snapshot("D:/repos/one") }, T0));
    }

    [Fact]
    public void A_batch_beyond_the_ceiling_is_rejected_rather_than_truncated()
    {
        var store = NewStore();
        var batch = Enumerable.Range(0, RepoStateStore.MaxRepositoriesPerPush + 1)
            .Select(i => Snapshot($"D:/repos/{i}")).ToArray();

        // Truncating would store a partial picture and report it as the machine's whole hygiene.
        Assert.Throws<RepoStateValidationException>(() => store.StoreBatch(Alice, "dir-1", "SOREN", batch, T0));
    }

    [Fact]
    public void An_empty_batch_stores_nothing_and_is_not_an_error()
    {
        var store = NewStore();
        Assert.Equal(0, store.StoreBatch(Alice, "dir-1", "SOREN", Array.Empty<RepoStateSnapshotDto>(), T0));
    }

    [Fact]
    public void A_collection_time_in_the_future_is_clamped_to_the_receive_time()
    {
        // A skewed clock on the pushing machine must not produce a negative age downstream.
        var store = NewStore();
        store.StoreBatch(Alice, "dir-1", "SOREN",
            new[] { Snapshot("D:/repos/one", collectedAtUtc: T0.AddHours(5)) }, T0);

        var stored = Assert.Single(store.ReadFresh(Alice, TimeSpan.FromHours(12), T0));
        Assert.Equal(T0, stored.CollectedAtUtc);
    }

    [Fact]
    public void An_invalid_tenant_is_refused_rather_than_written_somewhere()
    {
        var store = NewStore();
        Assert.Throws<ArgumentException>(
            () => store.StoreBatch(default, "dir-1", "SOREN", new[] { Snapshot("D:/repos/one") }, T0));
        Assert.Throws<ArgumentException>(() => store.ReadFresh(default, TimeSpan.FromHours(12), T0));
    }

    // ---- staleness -------------------------------------------------------------------------------------

    [Fact]
    public void A_snapshot_older_than_the_freshness_bar_is_EXCLUDED_not_aged()
    {
        var store = NewStore();
        store.StoreBatch(Alice, "dir-stale", "OLD", new[] { Snapshot("D:/repos/stale") }, T0.AddDays(-7));
        store.StoreBatch(Alice, "dir-fresh", "NOW", new[] { Snapshot("D:/repos/fresh") }, T0);

        // A Director that stopped pushing a week ago knows nothing about that repository today; serving its
        // last picture is how a report comes to recommend deleting a worktree someone has since started in.
        var stored = store.ReadFresh(Alice, TimeSpan.FromHours(24), T0);
        Assert.Equal("D:/repos/fresh", Assert.Single(stored).Path);

        // Widen the bar and the older one is there - it was excluded by age, not lost.
        Assert.Equal(2, store.ReadFresh(Alice, TimeSpan.FromDays(30), T0).Count);
    }

    [Fact]
    public void Rows_come_back_newest_first()
    {
        var store = NewStore();
        store.StoreBatch(Alice, "dir-1", "SOREN", new[] { Snapshot("D:/repos/older") }, T0.AddHours(-5));
        store.StoreBatch(Alice, "dir-2", "SOREN", new[] { Snapshot("D:/repos/newer") }, T0);

        var stored = store.ReadFresh(Alice, TimeSpan.FromDays(1), T0);
        Assert.Equal(new[] { "D:/repos/newer", "D:/repos/older" }, stored.Select(s => s.Path));
    }
}
