using System.Text;

namespace CcDirector.Core.Git;

/// <summary>
/// Turns the repository model into a markdown report the owner copies to the clipboard and pastes
/// into whichever coding agent they like. The clipboard is the hand-off - the product does not pick
/// an agent, spawn a session, or route anything; it writes a complete, self-describing work order
/// and gets out of the way.
///
/// Every report states what produced it, the target state, the facts, and the hard rules, so a
/// pasted report needs no follow-up questions. Wording comes from <see cref="RepositoryStatusText"/>
/// and <see cref="RecommendationEngine"/> so the clipboard and the screen always say the same thing.
/// Pure and golden-tested; no UI types, no clock of its own.
/// </summary>
/// <summary>How far the background scan had got when a report was copied.</summary>
/// <param name="IsScanning">True while the scan is still running - the report is then PARTIAL.</param>
public sealed record ScanProgress(bool IsScanning, int Done, int Total);

public static class RepoReportBuilder
{
    /// <summary>The standing rules every report carries, verbatim.</summary>
    public const string HardRules = """
        - NEVER force-push, rebase shared branches, or delete branches with unmerged commits.
        - NEVER discard files without first showing the list and getting explicit approval in this session.
        - Open a pull request rather than merging to main yourself.
        - Write all commits and pull requests as the repository owner. Do not add any attribution,
          Co-authored-by trailer, robot emoji, or generated-with line naming any AI assistant or vendor.
        - If anything is genuinely ambiguous, stop and ask in this session rather than guessing.
        """;

    private const string TargetState = """
        Target state for every repository: everything merged to origin/main, the checkout parked on
        main and clean, no side branches, and no worktree older than a day.
        """;

    /// <summary>
    /// The whole scan: every repository plus every recommendation.
    ///
    /// Copying mid-scan is allowed on purpose - a disabled button cannot explain itself, and the
    /// text is what travels to the agent, so the warning belongs IN the report rather than in the
    /// UI. Pass <paramref name="progress"/> when the background scan is still running and the
    /// report declares itself PARTIAL, in its title and its opening line.
    /// </summary>
    public static string BuildAll(
        IReadOnlyList<RepositoryStatus> repositories,
        IReadOnlyList<Recommendation> recommendations,
        IReadOnlyList<string>? roots = null,
        string? machine = null,
        DateTime? nowLocal = null,
        ScanProgress? progress = null)
    {
        var sb = new StringBuilder();
        Header(sb, progress is { IsScanning: true } ? "Repository report (PARTIAL)" : "Repository report", machine, nowLocal);

        if (progress is { IsScanning: true } mid)
        {
            sb.AppendLine(
                $"PARTIAL: the background scan was still running when this was copied - {mid.Done} of {mid.Total} " +
                "repositories done. The ones not yet scanned are missing entirely from this report, so do not read it " +
                "as the whole picture. Copy again once the scan finishes.");
            sb.AppendLine();
        }

        var scanned = repositories.Where(r => !r.Provisional).ToList();
        sb.Append($"Scanned {scanned.Count} repositor{(scanned.Count == 1 ? "y" : "ies")}");
        if (roots is { Count: > 0 })
            sb.Append($" under {string.Join(", ", roots)}");
        sb.AppendLine(".");
        int skipped = repositories.Count - scanned.Count;
        if (skipped > 0)
            sb.AppendLine($"{skipped} entr{(skipped == 1 ? "y is" : "ies are")} still being verified and are left out of this report.");
        sb.AppendLine();
        sb.AppendLine(TargetState);
        sb.AppendLine();

        var attention = scanned.Where(NeedsAttention)
            .OrderByDescending(r => r.WorktreesSafeToReap)
            .ThenByDescending(r => r.UncommittedCount)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Complement of the same predicate - NOT set subtraction, which would lean on record
        // equality (and would silently drop a duplicate rather than list it).
        var quiet = scanned.Where(r => !NeedsAttention(r)).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        sb.AppendLine("## Needs attention");
        sb.AppendLine();
        if (attention.Count == 0)
        {
            sb.AppendLine("Nothing - every repository is clean, in sync, and free of worktrees.");
        }
        else
        {
            sb.AppendLine("| Repository | Path | Working tree | Sync | Worktrees |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var r in attention)
                sb.AppendLine($"| {r.Name} | {r.Path} | {RepositoryStatusText.Where(r)} | {RepositoryStatusText.Sync(r)} | {RepositoryStatusText.Worktrees(r)} |");
        }
        sb.AppendLine();

        if (quiet.Count > 0)
        {
            sb.AppendLine($"## Nothing to do ({quiet.Count})");
            sb.AppendLine();
            sb.AppendLine(string.Join(", ", quiet.Select(r => r.Name)));
            sb.AppendLine();
        }

        var failed = scanned.Where(r => !r.Success).ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine("## Could not be read");
            sb.AppendLine();
            foreach (var r in failed)
                sb.AppendLine($"- {r.Name} ({r.Path}): {r.Error ?? "unknown error"}");
            sb.AppendLine();
        }

        AppendRecommendations(sb, recommendations);
        AppendSafeWorktrees(sb, recommendations, scanned);
        AppendTasks(sb, TasksForKinds(recommendations));
        AppendRules(sb);
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>One repository: its state, its worktrees, its recommendations, and what to do.</summary>
    public static string BuildOne(
        RepositoryStatus repo,
        IReadOnlyList<Recommendation> recommendations,
        string? machine = null,
        DateTime? nowLocal = null)
    {
        var sb = new StringBuilder();
        Header(sb, $"Repository report - {repo.Name}", machine, nowLocal);

        var nowUtc = (nowLocal ?? DateTime.Now).ToUniversalTime();
        sb.AppendLine($"Repository: {repo.Path}");
        sb.AppendLine($"Remote: {repo.RemoteUrl ?? "(no remote)"} ({RepositoryStatusText.ProviderLabel(repo.Provider)})");
        sb.AppendLine($"State: {RepositoryStatusText.HeaderStats(repo, nowUtc)}");
        if (!repo.Success)
            sb.AppendLine($"WARNING - the last scan of this repository failed: {repo.Error ?? "unknown error"}");
        sb.AppendLine();
        sb.AppendLine(TargetState);
        sb.AppendLine();

        if (repo.Worktrees.Count > 0)
        {
            sb.AppendLine($"## Worktrees ({repo.Worktrees.Count})");
            sb.AppendLine();
            sb.AppendLine("| Path | Branch | Verdict | Ahead of main | Behind main | Size |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var w in repo.Worktrees)
                sb.AppendLine($"| {w.Path} | {Branch(w)} | {SafetyText(w.Safety)} | {w.AheadOfMain} | {w.BehindMain} | {RepositoryStatusText.FormatBytes(w.SizeBytes ?? 0)} |");
            sb.AppendLine();
        }

        var mine = recommendations.Where(r => SamePath(r.RepoPath, repo.Path)).ToList();
        AppendRecommendations(sb, mine);
        AppendTasks(sb, TasksFor(repo, mine));
        AppendRules(sb);
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>
    /// Every recommendation and nothing else - what the Recommendations page is showing. Each one
    /// carries the current state of the repository it names, so the paste needs no lookups.
    /// </summary>
    public static string BuildRecommendations(
        IReadOnlyList<Recommendation> recommendations,
        IReadOnlyList<RepositoryStatus> repositories,
        string? machine = null,
        DateTime? nowLocal = null)
    {
        var sb = new StringBuilder();
        Header(sb, "Repository recommendations", machine, nowLocal);
        sb.AppendLine(TargetState);
        sb.AppendLine();
        AppendRecommendations(sb, recommendations);

        var named = recommendations
            .Select(r => r.RepoPath)
            .Where(p => p is { Length: > 0 })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => repositories.FirstOrDefault(r => SamePath(r.Path, p)))
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();

        if (named.Count > 0)
        {
            var nowUtc = (nowLocal ?? DateTime.Now).ToUniversalTime();
            sb.AppendLine("## The repositories named above");
            sb.AppendLine();
            foreach (var r in named)
                sb.AppendLine($"- {r.Name} ({r.Path}): {RepositoryStatusText.HeaderStats(r, nowUtc)}");
            sb.AppendLine();
        }

        AppendSafeWorktrees(sb, recommendations, repositories);
        AppendTasks(sb, TasksForKinds(recommendations));
        AppendRules(sb);
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>
    /// A single recommendation card, with the repository it is about and the task it implies. The
    /// full scan is passed so the fleet-wide reap - which names no single repository - can still
    /// list the worktrees it is talking about; a paste that references a list it does not contain
    /// is not a hand-off.
    /// </summary>
    public static string BuildRecommendation(
        Recommendation recommendation,
        RepositoryStatus? repo,
        IReadOnlyList<RepositoryStatus>? repositories = null,
        string? machine = null,
        DateTime? nowLocal = null)
    {
        var sb = new StringBuilder();
        Header(sb, "Repository recommendation", machine, nowLocal);

        if (repo is not null)
        {
            var nowUtc = (nowLocal ?? DateTime.Now).ToUniversalTime();
            sb.AppendLine($"Repository: {repo.Path}");
            sb.AppendLine($"State: {RepositoryStatusText.HeaderStats(repo, nowUtc)}");
            sb.AppendLine();
        }

        sb.AppendLine(TargetState);
        sb.AppendLine();
        AppendRecommendations(sb, new[] { recommendation });
        AppendSafeWorktrees(sb, new[] { recommendation },
            repositories ?? (repo is null ? Array.Empty<RepositoryStatus>() : new[] { repo }));
        AppendTasks(sb, new[] { TaskFor(recommendation.Kind) });
        AppendRules(sb);
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    // ----- pieces -----

    private static void Header(StringBuilder sb, string title, string? machine, DateTime? nowLocal)
    {
        var when = nowLocal ?? DateTime.Now;
        sb.AppendLine($"# {title} - {machine ?? Environment.MachineName} - {when:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine("Produced by the DevThrottle Director's background repository scan. Everything below is a fact read from disk at that moment; nothing has been changed.");
        sb.AppendLine();
    }

    private static void AppendRecommendations(StringBuilder sb, IReadOnlyList<Recommendation> recommendations)
    {
        sb.AppendLine("## Recommendations");
        sb.AppendLine();
        if (recommendations.Count == 0)
        {
            sb.AppendLine("None - nothing is drifting.");
            sb.AppendLine();
            return;
        }

        int n = 1;
        foreach (var r in recommendations)
        {
            sb.AppendLine($"{n++}. {r.Title}");
            sb.AppendLine($"   {r.Body}");
            sb.AppendLine($"   {r.Why}");
            if (r.RepoPath is { Length: > 0 } path)
                sb.AppendLine($"   Path: {path}");
            sb.AppendLine();
        }
    }

    /// <summary>
    /// The worktrees the reap recommendation is actually about, by full path. Written whenever that
    /// recommendation is present, because its task tells the agent to work from a list - and the
    /// list has to be IN the paste, not on a screen the agent cannot see.
    ///
    /// Provisional (warm-start, not yet re-verified) entries are excluded, exactly as
    /// <see cref="RecommendationEngine"/> excludes them: this list becomes a delete list, and an
    /// unverified cached verdict must never put a worktree on it.
    /// </summary>
    private static void AppendSafeWorktrees(
        StringBuilder sb,
        IReadOnlyList<Recommendation> recommendations,
        IReadOnlyList<RepositoryStatus> repositories)
    {
        if (!recommendations.Any(r => r.Kind == RecommendationKind.ReapSafeWorktrees))
            return;

        var safe = repositories
            .Where(r => r.Success && !r.Provisional)
            .SelectMany(r => r.Worktrees.Select(w => (Repo: r, Worktree: w)))
            .Where(x => x.Worktree.Safety == WorktreeSafety.SafeToReap)
            .ToList();
        if (safe.Count == 0)
            return;

        sb.AppendLine($"## Worktrees safe to remove ({safe.Count})");
        sb.AppendLine();
        sb.AppendLine("| Worktree | Branch | Repository | Size |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var (r, w) in safe)
            sb.AppendLine($"| {w.Path} | {Branch(w)} | {r.Name} | {RepositoryStatusText.FormatBytes(w.SizeBytes ?? 0)} |");
        sb.AppendLine();
    }

    /// <summary>A worktree with no branch is on a detached HEAD - say so rather than leaving a blank cell.</summary>
    private static string Branch(WorktreeInfo w) => w.Branch is { Length: > 0 } b ? b : "(detached)";

    /// <summary>
    /// The asks, in one section. Written only when there is something to ask for - a whole-fleet
    /// report with nothing drifting ends at the facts rather than inventing busywork.
    /// </summary>
    private static void AppendTasks(StringBuilder sb, IReadOnlyList<string> tasks)
    {
        if (tasks.Count == 0)
            return;
        sb.AppendLine("## What I would like done");
        sb.AppendLine();
        foreach (var task in tasks)
        {
            sb.AppendLine(task);
            sb.AppendLine();
        }
    }

    private static IReadOnlyList<string> TasksForKinds(IReadOnlyList<Recommendation> recommendations) =>
        recommendations.Select(r => r.Kind).Distinct().Select(TaskFor).ToList();

    private static void AppendRules(StringBuilder sb)
    {
        sb.AppendLine("## Hard rules");
        sb.AppendLine();
        sb.AppendLine(HardRules);
    }

    /// <summary>
    /// The concrete tasks a repository's own state implies, in the order they should happen:
    /// protect unprotected work first, then reclaim, then deal with drift. A repository with
    /// nothing wrong gets the explain-only task, so a paste is never a no-op.
    /// </summary>
    internal static IReadOnlyList<string> TasksFor(RepositoryStatus repo, IReadOnlyList<Recommendation> recommendations)
    {
        var tasks = new List<string>();
        if (!repo.IsClean && repo.UncommittedCount > 0)
            tasks.Add(TaskFor(RecommendationKind.ProtectDirtyRepo));
        if (repo.WorktreesSafeToReap > 0)
            tasks.Add(TaskFor(RecommendationKind.ReapSafeWorktrees));
        if (repo.BehindMainCount >= RecommendationEngine.BehindMainThreshold)
            tasks.Add(TaskFor(RecommendationKind.FarBehindMain));

        foreach (var kind in recommendations.Select(r => r.Kind).Distinct())
        {
            var task = TaskFor(kind);
            if (!tasks.Contains(task))
                tasks.Add(task);
        }

        if (tasks.Count == 0)
            tasks.Add(ExplainOnly);
        return tasks;
    }

    internal static string TaskFor(RecommendationKind kind) => kind switch
    {
        RecommendationKind.ProtectDirtyRepo => """
            TASK - protect the uncommitted changes:
            1. Run git status and review every uncommitted file.
            2. Group the changes into coherent pieces and commit each with a clear message.
            3. Push a branch and open a pull request for my review. Nothing merges without me.
            4. Reply with a summary: what was committed, what looked like junk (do NOT delete it), and the pull request link.
            """,
        RecommendationKind.ReapSafeWorktrees => """
            TASK - remove the finished worktrees:
            1. For each worktree listed above as safe to reap, confirm for yourself that its work is on origin/main and nothing is running in it.
            2. Show me the exact list you intend to remove and WAIT for approval.
            3. Only after approval: remove the approved ones with git worktree remove, and prune.
            """,
        RecommendationKind.FarBehindMain => """
            TASK - deal with the drift:
            1. Review what is on this branch that is not on main, and what has moved on main underneath it.
            2. Recommend: land it (rebase onto origin/main, push, open a pull request) or drop it - with reasons.
            3. If landing: do it and link the pull request. If dropping: list exactly what would be lost and WAIT for approval.
            """,
        _ => ExplainOnly,
    };

    private const string ExplainOnly = """
        TASK - explain only, change nothing:
        1. Investigate what is sitting in this repository and how it likely got here.
        2. Write a plain-English summary: what is real work, what is generated, what can go.
        3. Make NO changes of any kind - not even staging.
        """;

    private static string SafetyText(WorktreeSafety safety) => safety switch
    {
        WorktreeSafety.SafeToReap => "safe to remove",
        WorktreeSafety.InUseBySession => "in use by a session",
        _ => "needs attention",
    };

    /// <summary>A repository earns a row in the table when there is something to say about it.</summary>
    private static bool NeedsAttention(RepositoryStatus r) =>
        !r.Success
        || !r.IsClean
        || r.WorktreeCount > 0
        || r.AheadCount > 0
        || r.BehindCount > 0
        || r.BehindMainCount > 0
        || r.IsDetachedHead;

    private static bool SamePath(string? a, string? b) => RepositoryStatusText.SamePath(a, b);
}
