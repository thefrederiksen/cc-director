using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The copied report IS the hand-off, so what matters is that a pasted report is complete and true:
/// every repository accounted for, the same words the screen used, the hard rules attached, and a
/// task that is never empty. These assert those properties rather than a fixed blob of text.
/// </summary>
public class RepoReportBuilderTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 5, 24, 0, DateTimeKind.Local);

    private static RepositoryStatus Repo(
        string name,
        string path,
        bool clean = true,
        int uncommitted = 0,
        int ahead = 0,
        int behind = 0,
        int behindMain = 0,
        int worktrees = 0,
        int safe = 0,
        int inUse = 0,
        int attention = 0,
        bool provisional = false,
        bool success = true,
        string? error = null,
        DateTime? dirtySince = null) => new()
        {
            Name = name,
            Path = path,
            Branch = "main",
            IsClean = clean,
            UncommittedCount = uncommitted,
            AheadCount = ahead,
            BehindCount = behind,
            BehindMainCount = behindMain,
            WorktreeCount = worktrees,
            WorktreesSafeToReap = safe,
            WorktreesInUse = inUse,
            WorktreesNeedAttention = attention,
            DirtySinceUtc = dirtySince,
            Provisional = provisional,
            Success = success,
            Error = error,
        };

    [Fact]
    public void BuildAll_AccountsForEveryRepository_ByPathOrByName()
    {
        var repos = new[]
        {
            Repo("dirty", @"D:\Repos\dirty", clean: false, uncommitted: 67),
            Repo("drifted", @"D:\Repos\drifted", behindMain: 59),
            Repo("tidy", @"D:\Repos\tidy"),
        };

        var report = RepoReportBuilder.BuildAll(repos, RecommendationEngine.Evaluate(repos, Now.ToUniversalTime()), machine: "BOX", nowLocal: Now);

        // Anything with something to say about it is named by its full path - an agent can cd there.
        Assert.Contains(@"D:\Repos\dirty", report);
        Assert.Contains(@"D:\Repos\drifted", report);
        // A repository with nothing wrong still appears, so nothing is silently dropped.
        Assert.Contains("tidy", report);
        Assert.Contains("Nothing to do (1)", report);
    }

    [Fact]
    public void BuildAll_UsesTheSameWordsAsTheScreen()
    {
        var repo = Repo("widget", @"D:\Repos\widget", clean: false, uncommitted: 7, behind: 5, worktrees: 3, safe: 1, inUse: 1, attention: 1);

        var report = RepoReportBuilder.BuildAll(new[] { repo }, Array.Empty<Recommendation>(), machine: "BOX", nowLocal: Now);

        // The seam that would otherwise drift: the report must not re-word the repository's state.
        Assert.Contains(RepositoryStatusText.Where(repo), report);
        Assert.Contains(RepositoryStatusText.Sync(repo), report);
        Assert.Contains(RepositoryStatusText.Worktrees(repo), report);
    }

    [Fact]
    public void BuildAll_LeavesOutUnverifiedEntries_AndSaysSo()
    {
        var repos = new[]
        {
            Repo("verified", @"D:\Repos\verified", clean: false, uncommitted: 2),
            Repo("stillchecking", @"D:\Repos\stillchecking", provisional: true),
        };

        var report = RepoReportBuilder.BuildAll(repos, Array.Empty<Recommendation>(), machine: "BOX", nowLocal: Now);

        Assert.DoesNotContain("stillchecking", report);
        Assert.Contains("Scanned 1 repository", report);
        Assert.Contains("still being verified", report);
    }

    /// <summary>
    /// Copied mid-scan, the report must say so IN THE TEXT - that is the only warning that travels
    /// with the paste into an agent. It names the exact progress so the reader can judge how much
    /// is missing, and the title carries it too, for anyone skimming.
    /// </summary>
    [Fact]
    public void BuildAll_CopiedMidScan_DeclaresItselfPartial_WithTheProgress()
    {
        var repos = new[] { Repo("a", @"D:\Repos\a", clean: false, uncommitted: 3) };

        var report = RepoReportBuilder.BuildAll(
            repos, Array.Empty<Recommendation>(), machine: "BOX", nowLocal: Now,
            progress: new ScanProgress(IsScanning: true, Done: 7, Total: 30));

        Assert.StartsWith("# Repository report (PARTIAL)", report);
        Assert.Contains("PARTIAL:", report);
        Assert.Contains("7 of 30", report);
        Assert.Contains("Copy again once the scan finishes", report);
    }

    /// <summary>A finished scan must NOT be labelled partial - the caveat has to mean something.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void BuildAll_AfterTheScan_IsNotLabelledPartial(bool? scanning)
    {
        var repos = new[] { Repo("a", @"D:\Repos\a") };
        var progress = scanning is null ? null : new ScanProgress(scanning.Value, 30, 30);

        var report = RepoReportBuilder.BuildAll(
            repos, Array.Empty<Recommendation>(), machine: "BOX", nowLocal: Now, progress: progress);

        Assert.DoesNotContain("PARTIAL", report);
        Assert.StartsWith("# Repository report - BOX", report);
    }

    [Fact]
    public void BuildAll_SurfacesRepositoriesThatCouldNotBeRead()
    {
        var repos = new[] { Repo("broken", @"D:\Repos\broken", success: false, error: "not a git repository") };

        var report = RepoReportBuilder.BuildAll(repos, Array.Empty<Recommendation>(), machine: "BOX", nowLocal: Now);

        Assert.Contains("Could not be read", report);
        Assert.Contains("not a git repository", report);
    }

    [Fact]
    public void BuildAll_NamesTheRootsItScanned()
    {
        var report = RepoReportBuilder.BuildAll(
            new[] { Repo("a", @"D:\Repos\a") },
            Array.Empty<Recommendation>(),
            roots: new[] { @"D:\ReposFred", @"C:\Repos" },
            machine: "BOX",
            nowLocal: Now);

        Assert.Contains(@"D:\ReposFred", report);
        Assert.Contains(@"C:\Repos", report);
    }

    [Fact]
    public void BuildOne_CarriesOnlyItsOwnRecommendations()
    {
        var mine = Repo("mine", @"D:\Repos\mine", clean: false, uncommitted: 9, dirtySince: Now.ToUniversalTime().AddDays(-20));
        var other = Repo("other", @"D:\Repos\other", clean: false, uncommitted: 4, dirtySince: Now.ToUniversalTime().AddDays(-30));
        var recs = RecommendationEngine.Evaluate(new[] { mine, other }, Now.ToUniversalTime());

        var report = RepoReportBuilder.BuildOne(mine, recs, machine: "BOX", nowLocal: Now);

        Assert.Contains("mine", report);
        Assert.DoesNotContain(@"D:\Repos\other", report);
    }

    [Fact]
    public void BuildOne_ListsTheWorktreesWithTheirVerdicts()
    {
        var repo = Repo("wt", @"D:\Repos\wt", worktrees: 2, safe: 1, attention: 1) with
        {
            Worktrees = new[]
            {
                new WorktreeInfo { Path = @"D:\wt\a", Branch = "feat/a", Safety = WorktreeSafety.SafeToReap, SizeBytes = 2_000_000 },
                new WorktreeInfo { Path = @"D:\wt\b", Branch = "feat/b", Safety = WorktreeSafety.NeedsAttention, AheadOfMain = 3 },
            },
        };

        var report = RepoReportBuilder.BuildOne(repo, Array.Empty<Recommendation>(), machine: "BOX", nowLocal: Now);

        Assert.Contains(@"D:\wt\a", report);
        Assert.Contains("safe to remove", report);
        Assert.Contains(@"D:\wt\b", report);
        Assert.Contains("needs attention", report);
    }

    [Fact]
    public void BuildOne_AlwaysAsksForSomething_EvenWhenNothingIsWrong()
    {
        var report = RepoReportBuilder.BuildOne(Repo("tidy", @"D:\Repos\tidy"), Array.Empty<Recommendation>(), machine: "BOX", nowLocal: Now);

        Assert.Contains("What I would like done", report);
        Assert.Contains("TASK", report);
    }

    [Theory]
    [InlineData(RecommendationKind.ProtectDirtyRepo, "protect the uncommitted changes")]
    [InlineData(RecommendationKind.ReapSafeWorktrees, "remove the finished worktrees")]
    [InlineData(RecommendationKind.FarBehindMain, "deal with the drift")]
    public void EachRecommendationKind_ImpliesItsOwnTask(RecommendationKind kind, string marker)
    {
        Assert.Contains(marker, RepoReportBuilder.TaskFor(kind));
    }

    [Fact]
    public void BuildRecommendation_WorksForTheFleetWideOne_ThatNamesNoRepository()
    {
        var repo = Repo("wt", @"D:\Repos\wt", worktrees: 1, safe: 1) with
        {
            Worktrees = new[] { new WorktreeInfo { Path = @"D:\wt\a", Branch = "feat/a", Safety = WorktreeSafety.SafeToReap, SizeBytes = 1_048_576 } },
        };
        var reap = RecommendationEngine.Evaluate(new[] { repo }, Now.ToUniversalTime())
            .Single(r => r.Kind == RecommendationKind.ReapSafeWorktrees);
        Assert.Null(reap.RepoPath); // the fleet-wide reap summary is about no single repository

        var report = RepoReportBuilder.BuildRecommendation(reap, repo: null, repositories: new[] { repo }, machine: "BOX", nowLocal: Now);

        Assert.Contains(reap.Title, report);
        Assert.Contains("remove the finished worktrees", report);
        // The task says "work from the list above", so the list must be IN the paste.
        Assert.Contains(@"D:\wt\a", report);
    }

    /// <summary>
    /// The reap task points the agent at a list of worktrees. Every report that carries that task
    /// must also carry the list, or the paste references something the agent cannot see.
    /// </summary>
    [Fact]
    public void EveryReportCarryingTheReapTask_AlsoCarriesTheWorktreePaths()
    {
        var repo = Repo("wt", @"D:\Repos\wt", worktrees: 2, safe: 2) with
        {
            Worktrees = new[]
            {
                new WorktreeInfo { Path = @"D:\wt\one", Branch = "feat/one", Safety = WorktreeSafety.SafeToReap, SizeBytes = 1_048_576 },
                new WorktreeInfo { Path = @"D:\wt\two", Branch = "feat/two", Safety = WorktreeSafety.SafeToReap, SizeBytes = 2_097_152 },
            },
        };
        var recs = RecommendationEngine.Evaluate(new[] { repo }, Now.ToUniversalTime());

        foreach (var report in new[]
                 {
                     RepoReportBuilder.BuildAll(new[] { repo }, recs, machine: "BOX", nowLocal: Now),
                     RepoReportBuilder.BuildRecommendations(recs, new[] { repo }, machine: "BOX", nowLocal: Now),
                     RepoReportBuilder.BuildRecommendation(recs[0], null, new[] { repo }, "BOX", Now),
                 })
        {
            Assert.Contains("remove the finished worktrees", report);
            Assert.Contains(@"D:\wt\one", report);
            Assert.Contains(@"D:\wt\two", report);
        }
    }

    /// <summary>
    /// REGRESSION (found by running it): the reap headline and the worktree table must describe the
    /// SAME set. A live run copied a card that had been folded at "7 of 30" and paired it with a
    /// later snapshot, producing "1 worktree - 336 MB" as the title over a table of five. The
    /// builder cannot stop a caller mixing snapshots, but when it is handed ONE, the count in the
    /// title and the rows in the table must agree.
    /// </summary>
    [Fact]
    public void ReapHeadlineAndWorktreeTable_DescribeTheSameSet()
    {
        var repo = Repo("wt", @"D:\Repos\wt", worktrees: 3, safe: 3) with
        {
            Worktrees = new[]
            {
                new WorktreeInfo { Path = @"D:\wt\a", Branch = "a", Safety = WorktreeSafety.SafeToReap, SizeBytes = 100_000_000 },
                new WorktreeInfo { Path = @"D:\wt\b", Branch = "b", Safety = WorktreeSafety.SafeToReap, SizeBytes = 200_000_000 },
                new WorktreeInfo { Path = @"D:\wt\c", Branch = "c", Safety = WorktreeSafety.SafeToReap, SizeBytes = 300_000_000 },
            },
        };
        var reap = RecommendationEngine.Evaluate(new[] { repo }, Now.ToUniversalTime())
            .Single(r => r.Kind == RecommendationKind.ReapSafeWorktrees);

        var report = RepoReportBuilder.BuildRecommendation(reap, null, new[] { repo }, "BOX", Now);

        Assert.Contains("3 worktrees are finished", report);   // the headline
        Assert.Contains("## Worktrees safe to remove (3)", report); // and the table agree
        foreach (var p in new[] { @"D:\wt\a", @"D:\wt\b", @"D:\wt\c" })
            Assert.Contains(p, report);
    }

    /// <summary>
    /// The safe-to-remove table becomes a DELETE list in the agent's hands, so it must never carry
    /// a worktree whose verdict came from the warm-start cache rather than a live scan - the same
    /// fail-closed rule <see cref="RecommendationEngine"/> applies. A verified repo in the same
    /// snapshot still contributes, so this is exclusion, not blanket suppression.
    /// </summary>
    [Fact]
    public void SafeToRemoveTable_NeverListsWorktreesFromAnUnverifiedEntry()
    {
        var verified = Repo("verified", @"D:\Repos\verified", worktrees: 1, safe: 1) with
        {
            Worktrees = new[] { new WorktreeInfo { Path = @"D:\wt\live", Branch = "live", Safety = WorktreeSafety.SafeToReap, SizeBytes = 1_048_576 } },
        };
        var cached = Repo("cached", @"D:\Repos\cached", worktrees: 1, safe: 1, provisional: true) with
        {
            Worktrees = new[] { new WorktreeInfo { Path = @"D:\wt\stale", Branch = "stale", Safety = WorktreeSafety.SafeToReap, SizeBytes = 1_048_576 } },
        };
        var repos = new[] { verified, cached };
        var recs = RecommendationEngine.Evaluate(repos, Now.ToUniversalTime());

        var report = RepoReportBuilder.BuildRecommendation(
            recs.Single(r => r.Kind == RecommendationKind.ReapSafeWorktrees), null, repos, "BOX", Now);

        Assert.Contains(@"D:\wt\live", report);
        Assert.DoesNotContain(@"D:\wt\stale", report);
        Assert.Contains("## Worktrees safe to remove (1)", report);
    }

    /// <summary>A detached worktree gets a word, not an empty table cell.</summary>
    [Fact]
    public void DetachedWorktree_IsNamedRatherThanBlank()
    {
        var repo = Repo("wt", @"D:\Repos\wt", worktrees: 1, safe: 1) with
        {
            Worktrees = new[] { new WorktreeInfo { Path = @"D:\wt\a", Branch = "", Safety = WorktreeSafety.SafeToReap, SizeBytes = 1_048_576 } },
        };

        var report = RepoReportBuilder.BuildOne(repo, Array.Empty<Recommendation>(), machine: "BOX", nowLocal: Now);

        Assert.Contains("(detached)", report);
        Assert.DoesNotContain("|  |", report); // no empty cells
    }

    [Fact]
    public void BuildRecommendations_CarriesTheStateOfEveryRepositoryItNames()
    {
        var repo = Repo("aging", @"D:\Repos\aging", clean: false, uncommitted: 12, dirtySince: Now.ToUniversalTime().AddDays(-40));
        var recs = RecommendationEngine.Evaluate(new[] { repo }, Now.ToUniversalTime());

        var report = RepoReportBuilder.BuildRecommendations(recs, new[] { repo }, machine: "BOX", nowLocal: Now);

        Assert.Contains(@"D:\Repos\aging", report);
        Assert.Contains(RepositoryStatusText.HeaderStats(repo, Now.ToUniversalTime()), report);
    }

    [Fact]
    public void EveryReport_CarriesTheHardRules_IncludingTheNoAttributionRule()
    {
        var repo = Repo("a", @"D:\Repos\a");
        var recs = new[] { new Recommendation { Kind = RecommendationKind.ProtectDirtyRepo, Title = "t", Body = "b", Why = "w", RepoPath = repo.Path } };

        foreach (var report in new[]
                 {
                     RepoReportBuilder.BuildAll(new[] { repo }, recs, machine: "BOX", nowLocal: Now),
                     RepoReportBuilder.BuildOne(repo, recs, machine: "BOX", nowLocal: Now),
                     RepoReportBuilder.BuildRecommendations(recs, new[] { repo }, machine: "BOX", nowLocal: Now),
                     RepoReportBuilder.BuildRecommendation(recs[0], repo, machine: "BOX", nowLocal: Now),
                 })
        {
            Assert.Contains("## Hard rules", report);
            Assert.Contains(RepoReportBuilder.HardRules, report);
            Assert.Contains("Co-authored-by trailer", report);
            Assert.Contains("NEVER force-push", report);
        }
    }

    /// <summary>
    /// A report is pasted into an agent and drives commits and pull requests, so it must never seed
    /// vendor attribution. The PROHIBITION may name the trailer ("no Co-authored-by trailer"); an
    /// actual trailer or a named assistant must never appear. Ported from the hand-off brief guard.
    /// </summary>
    [Fact]
    public void NoReport_NamesAnAssistantOrCarriesATrailer()
    {
        var repo = Repo("a", @"D:\Repos\a", clean: false, uncommitted: 3);
        var recs = RecommendationEngine.Evaluate(new[] { repo }, Now.ToUniversalTime());

        foreach (var report in new[]
                 {
                     RepoReportBuilder.BuildAll(new[] { repo }, recs, machine: "BOX", nowLocal: Now),
                     RepoReportBuilder.BuildOne(repo, recs, machine: "BOX", nowLocal: Now),
                     RepoReportBuilder.BuildRecommendations(recs, new[] { repo }, machine: "BOX", nowLocal: Now),
                 })
        {
            foreach (var vendor in new[] { "Claude", "Anthropic", "Codex", "OpenAI", "Copilot", "Gemini", "Cursor", "Grok" })
                Assert.DoesNotContain(vendor, report, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Co-Authored-By:", report, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Generated with [", report);
        }
    }

    [Fact]
    public void EveryReport_SaysWhatMadeIt_WhereAndWhen()
    {
        var repo = Repo("a", @"D:\Repos\a");

        var report = RepoReportBuilder.BuildAll(new[] { repo }, Array.Empty<Recommendation>(), machine: "SOREN_NORTH", nowLocal: Now);

        Assert.StartsWith("# Repository report - SOREN_NORTH - 2026-08-01 05:24", report);
        Assert.Contains("nothing has been changed", report);
    }

    [Fact]
    public void SameInputs_ProduceTheSameReport()
    {
        var repos = new[] { Repo("a", @"D:\Repos\a", clean: false, uncommitted: 3) };
        var recs = RecommendationEngine.Evaluate(repos, Now.ToUniversalTime());

        Assert.Equal(
            RepoReportBuilder.BuildAll(repos, recs, machine: "BOX", nowLocal: Now),
            RepoReportBuilder.BuildAll(repos, recs, machine: "BOX", nowLocal: Now));
    }
}
