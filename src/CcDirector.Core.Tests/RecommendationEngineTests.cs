using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

public class RecommendationEngineTests
{
    private static readonly DateTime Now = new(2026, 07, 23, 12, 0, 0, DateTimeKind.Utc);

    private static RepositoryStatus Repo(
        string name,
        int safeWorktrees = 0,
        long safeBytes = 0,
        int uncommitted = 0,
        int dirtyDaysAgo = 0,
        int behindMain = 0,
        bool provisional = false)
    {
        var worktrees = new List<WorktreeInfo>();
        for (int i = 0; i < safeWorktrees; i++)
            worktrees.Add(new WorktreeInfo
            {
                Path = $"/r/{name}-wt{i}",
                Branch = $"b{i}",
                Safety = WorktreeSafety.SafeToReap,
                SizeBytes = safeBytes / Math.Max(1, safeWorktrees),
            });
        return new RepositoryStatus
        {
            Path = $"/r/{name}",
            Name = name,
            Branch = "main",
            IsClean = uncommitted == 0,
            UncommittedCount = uncommitted,
            DirtySinceUtc = uncommitted > 0 ? Now.AddDays(-dirtyDaysAgo) : null,
            BehindMainCount = behindMain,
            WorktreesSafeToReap = safeWorktrees,
            WorktreeCount = safeWorktrees,
            Worktrees = worktrees,
            Provisional = provisional,
            Success = true,
        };
    }

    [Fact]
    public void ReapSafe_SummarizesAcrossRepos_WithReclaimBytes()
    {
        var recs = RecommendationEngine.Evaluate(new[]
        {
            Repo("a", safeWorktrees: 2, safeBytes: 2_147_483_648),
            Repo("b", safeWorktrees: 1, safeBytes: 1_073_741_824),
        }, Now);

        var reap = Assert.Single(recs, r => r.Kind == RecommendationKind.ReapSafeWorktrees);
        Assert.Equal(1, reap.Severity);
        Assert.Contains("3 worktrees are finished", reap.Title);
        Assert.Contains("3.0 GB", reap.Title);
        Assert.Equal(3_221_225_472, reap.ReclaimBytes);
        Assert.Contains("never counted", reap.Why);
    }

    [Fact]
    public void DirtyRepo_PastThreshold_GetsAProtectRecommendation_UnderThresholdDoesNot()
    {
        var recs = RecommendationEngine.Evaluate(new[]
        {
            Repo("old-mess", uncommitted: 434, dirtyDaysAgo: 12),
            Repo("fresh-work", uncommitted: 5, dirtyDaysAgo: 2),
        }, Now);

        var protect = Assert.Single(recs, r => r.Kind == RecommendationKind.ProtectDirtyRepo);
        Assert.Contains("old-mess", protect.Title);
        Assert.Contains("434 uncommitted files sitting for 12 days", protect.Title);
        Assert.Equal("/r/old-mess", protect.RepoPath);
        Assert.DoesNotContain(recs, r => r.Title.Contains("fresh-work"));
    }

    [Fact]
    public void BehindMain_PastThreshold_GetsADriftRecommendation()
    {
        var recs = RecommendationEngine.Evaluate(new[]
        {
            Repo("drifting", behindMain: 70),
            Repo("close", behindMain: 10),
        }, Now);

        var drift = Assert.Single(recs, r => r.Kind == RecommendationKind.FarBehindMain);
        Assert.Contains("drifting", drift.Title);
        Assert.Contains("70 commits behind main", drift.Title);
        Assert.DoesNotContain(recs, r => r.Title.Contains("close"));
    }

    [Fact]
    public void ProvisionalEntries_NeverGenerateRecommendations()
    {
        // The warm-start trust rule extends to coaching: unverified data recommends nothing.
        var recs = RecommendationEngine.Evaluate(new[]
        {
            Repo("cached", safeWorktrees: 5, uncommitted: 100, dirtyDaysAgo: 30, behindMain: 99, provisional: true),
        }, Now);

        Assert.Empty(recs);
    }

    [Fact]
    public void CleanFleet_NoRecommendations()
    {
        Assert.Empty(RecommendationEngine.Evaluate(new[] { Repo("tidy") }, Now));
    }

    [Fact]
    public void Ordering_IsSeverity_ReapFirst()
    {
        var recs = RecommendationEngine.Evaluate(new[]
        {
            Repo("drifting", behindMain: 50),
            Repo("dirty", uncommitted: 9, dirtyDaysAgo: 10),
            Repo("reapable", safeWorktrees: 1, safeBytes: 1024),
        }, Now);

        Assert.Equal(3, recs.Count);
        Assert.Equal(RecommendationKind.ReapSafeWorktrees, recs[0].Kind);
        Assert.Equal(RecommendationKind.ProtectDirtyRepo, recs[1].Kind);
        Assert.Equal(RecommendationKind.FarBehindMain, recs[2].Kind);
    }
}
