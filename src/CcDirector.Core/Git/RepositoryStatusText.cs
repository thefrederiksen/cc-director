namespace CcDirector.Core.Git;

/// <summary>
/// The one place a repository's state becomes words. Every surface that describes a repository -
/// the repository list, the detail header, and the report the owner copies to the clipboard - reads
/// its wording from here, so the screen and the clipboard can never disagree about what a repo is
/// doing. Pure, unit-tested, no UI types.
/// </summary>
public static class RepositoryStatusText
{
    /// <summary>Working-tree state: "clean" or "N uncommitted".</summary>
    public static string Where(RepositoryStatus s) =>
        s.IsClean ? "clean" : $"{s.UncommittedCount} uncommitted";

    /// <summary>Drift against origin and main: "up to date", "ahead 2, behind 5", "detached HEAD".</summary>
    public static string Sync(RepositoryStatus s)
    {
        if (s.IsDetachedHead)
            return "detached HEAD";

        var parts = new List<string>();
        if (s.AheadCount > 0) parts.Add($"ahead {s.AheadCount}");
        if (s.BehindCount > 0) parts.Add($"behind {s.BehindCount}");
        if (s.BehindMainCount > 0) parts.Add($"behind main {s.BehindMainCount}");
        return parts.Count == 0 ? "up to date" : string.Join(", ", parts);
    }

    /// <summary>Worktree tally: "no worktrees" or "3 worktrees · 1 safe · 1 in use · 1 attention".</summary>
    public static string Worktrees(RepositoryStatus s)
    {
        if (s.WorktreeCount == 0)
            return "no worktrees";

        var parts = new List<string> { $"{s.WorktreeCount} worktree{(s.WorktreeCount == 1 ? "" : "s")}" };
        if (s.WorktreesSafeToReap > 0) parts.Add($"{s.WorktreesSafeToReap} safe");
        if (s.WorktreesInUse > 0) parts.Add($"{s.WorktreesInUse} in use");
        if (s.WorktreesNeedAttention > 0) parts.Add($"{s.WorktreesNeedAttention} attention");
        return string.Join(" · ", parts);
    }

    /// <summary>The list header line once a scan has finished.</summary>
    public static string Summary(IReadOnlyList<RepositoryStatus> statuses)
    {
        int repos = statuses.Count;
        int dirty = statuses.Count(s => !s.IsClean);
        int reap = statuses.Sum(s => s.WorktreesSafeToReap);
        return $"{repos} on disk · {dirty} with uncommitted work · {reap} worktrees to reap";
    }

    /// <summary>The detail screen's one-line story for a single repository.</summary>
    public static string HeaderStats(RepositoryStatus s, DateTime? nowUtc = null)
    {
        var parts = new List<string> { $"branch {s.Branch}" };
        parts.Add(s.IsClean ? "clean" : $"{s.UncommittedCount} uncommitted{DirtyDays(s, nowUtc)}");
        if (s.AheadCount > 0 || s.BehindCount > 0) parts.Add($"ahead {s.AheadCount} / behind {s.BehindCount}");
        if (s.BehindMainCount > 0) parts.Add($"behind main {s.BehindMainCount}");
        if (s.WorktreeCount > 0) parts.Add($"{s.WorktreeCount} worktree(s), {FormatBytes(s.WorktreeBytes)}");
        return string.Join(" · ", parts);
    }

    /// <summary>How long uncommitted work has been sitting, as " for N day(s)"; empty when clean.</summary>
    public static string DirtyDays(RepositoryStatus s, DateTime? nowUtc = null)
        => s.DirtySinceUtc is { } since
            ? $" for {(int)Math.Max(0, ((nowUtc ?? DateTime.UtcNow) - since).TotalDays)} day(s)"
            : "";

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:0.0} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:0} MB",
        > 0 => $"{bytes / 1024.0:0} KB",
        _ => "0 KB",
    };

    /// <summary>
    /// Do two repository/worktree paths refer to the same place? The one comparison every surface
    /// uses, so casing and separator differences cannot be handled one way here and another way
    /// there. Empty or null is never "the same place" - callers that treat "no repository" as a
    /// matchable value (the fleet-wide recommendation) must say so explicitly.
    /// </summary>
    public static bool SamePath(string? a, string? b) =>
        a is { Length: > 0 } && b is { Length: > 0 }
        && string.Equals(
            WorktreeReaperService.NormalizePath(a),
            WorktreeReaperService.NormalizePath(b),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Provider name as the UI writes it.</summary>
    public static string ProviderLabel(RepoProvider p) => p switch
    {
        RepoProvider.GitHub => "GitHub",
        RepoProvider.AzureDevOps => "Azure DevOps",
        RepoProvider.Other => "Git",
        _ => "local",
    };
}
