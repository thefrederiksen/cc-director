namespace CcDirector.Core.Git;

public enum RecommendationKind
{
    /// <summary>Finished worktrees can be removed; disk comes back.</summary>
    ReapSafeWorktrees,

    /// <summary>Uncommitted work has been sitting unprotected too long.</summary>
    ProtectDirtyRepo,

    /// <summary>A branch has drifted far behind main; merging gets harder by the day.</summary>
    FarBehindMain,
}

/// <summary>
/// One recommendation, fully folded: finished title, body, and why-line in plain language. The UI
/// renders these verbatim and never re-derives them (the dumb-client rule). Severity orders the
/// list: 1 = free win, 2 = protect work, 3 = drift.
/// </summary>
public sealed record Recommendation
{
    public RecommendationKind Kind { get; init; }
    public int Severity { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string Why { get; init; } = "";

    /// <summary>The repository this recommendation is about; null for the fleet-wide reap summary.</summary>
    public string? RepoPath { get; init; }

    public long ReclaimBytes { get; init; }
}

/// <summary>
/// Turns the repository model into plain-language recommendations - the coaching half of the
/// repositories vision: the product tells the user what is drifting BEFORE it becomes 300 dangling
/// worktrees, and offers the one-click or one-agent way out. Pure and threshold-driven; unit-tested
/// as a table. Provisional (unverified warm-start) entries never generate recommendations.
/// </summary>
public static class RecommendationEngine
{
    /// <summary>Uncommitted work older than this is "sitting too long".</summary>
    public static readonly TimeSpan DirtyThreshold = TimeSpan.FromDays(7);

    /// <summary>A branch this far behind main is drifting expensively.</summary>
    public const int BehindMainThreshold = 30;

    public static IReadOnlyList<Recommendation> Evaluate(IReadOnlyList<RepositoryStatus> repositories, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var results = new List<Recommendation>();
        var verified = repositories.Where(r => r.Success && !r.Provisional).ToList();

        // 1) The free win: finished worktrees across all repos.
        int safeCount = verified.Sum(r => r.WorktreesSafeToReap);
        if (safeCount > 0)
        {
            long reclaim = verified
                .SelectMany(r => r.Worktrees)
                .Where(w => w.Safety == WorktreeSafety.SafeToReap)
                .Sum(w => w.SizeBytes ?? 0);
            results.Add(new Recommendation
            {
                Kind = RecommendationKind.ReapSafeWorktrees,
                Severity = 1,
                Title = $"Reclaim {FormatBytes(reclaim)} - {safeCount} worktree{(safeCount == 1 ? " is" : "s are")} finished and safe to remove",
                Body = "Their work is already on origin/main and nothing is using them. Removing them deletes only leftover copies.",
                Why = "why: merged and clean, verified by the background scan; worktrees a session is using are never counted",
                ReclaimBytes = reclaim,
            });
        }

        // 2) Protect work: repos dirty past the threshold.
        foreach (var r in verified.Where(r => !r.IsClean && r.DirtySinceUtc is { } since && now - since >= DirtyThreshold)
                     .OrderByDescending(r => r.UncommittedCount))
        {
            int days = (int)(now - r.DirtySinceUtc!.Value).TotalDays;
            results.Add(new Recommendation
            {
                Kind = RecommendationKind.ProtectDirtyRepo,
                Severity = 2,
                Title = $"{r.Name} has {r.UncommittedCount} uncommitted file{(r.UncommittedCount == 1 ? "" : "s")} sitting for {days} days",
                Body = "Uncommitted work is unprotected - a disk failure or a careless cleanup loses it. An agent can sort, commit, and push it for your review.",
                Why = $"why: uncommitted since {r.DirtySinceUtc:yyyy-MM-dd} (threshold {DirtyThreshold.TotalDays:0} days)",
                RepoPath = r.Path,
            });
        }

        // 3) Drift: branches far behind main.
        foreach (var r in verified.Where(r => r.BehindMainCount >= BehindMainThreshold)
                     .OrderByDescending(r => r.BehindMainCount))
        {
            results.Add(new Recommendation
            {
                Kind = RecommendationKind.FarBehindMain,
                Severity = 3,
                Title = $"{r.Name}'s branch {r.Branch} is {r.BehindMainCount} commits behind main",
                Body = "The longer it drifts, the harder the merge. Best next step: rebase or fold the work back while it is still cheap.",
                Why = $"why: behind-main threshold ({BehindMainThreshold}) exceeded",
                RepoPath = r.Path,
            });
        }

        return results.OrderBy(x => x.Severity).ToList();
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:0.0} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:0} MB",
        > 0 => $"{bytes / 1024.0:0} KB",
        _ => "0 KB",
    };
}
