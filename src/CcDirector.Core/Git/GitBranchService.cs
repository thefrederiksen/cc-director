using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>One local branch with its sync state and the fail-closed safe-delete verdict.</summary>
public sealed record BranchInfo
{
    public string Name { get; init; } = "";
    public bool IsCurrent { get; init; }

    /// <summary>True when the branch is checked out in some linked worktree (never deletable).</summary>
    public bool CheckedOutInWorktree { get; init; }

    public int AheadOfMain { get; init; }
    public int BehindMain { get; init; }
    public bool OriginBranchExists { get; init; }
    public DateTime? LastCommitUtc { get; init; }

    public bool SafeToDelete { get; init; }
    public string Explanation { get; init; } = "";
}

/// <summary>
/// The pure safe-delete decision for a local branch - the same fail-closed shape as the worktree
/// verdict: deletable ONLY when provably merged (origin branch gone after prune, or contained in
/// origin/main, or its pull request merged) and never the current branch, never one checked out in
/// a worktree. No signal = not deletable.
/// </summary>
public static class BranchSafetyEvaluator
{
    public static (bool Safe, string Explanation) Evaluate(
        bool isCurrent,
        bool checkedOutInWorktree,
        bool inspectionSucceeded,
        bool pullRequestMerged,
        bool originBranchGone,
        bool containedInMain)
    {
        if (isCurrent)
            return (false, "The branch you are on - switch away before deleting.");
        if (checkedOutInWorktree)
            return (false, "Checked out in a worktree - remove the worktree first.");
        if (!inspectionSucceeded)
            return (false, "Could not inspect this branch - treated as unsafe.");
        if (pullRequestMerged)
            return (true, "Pull request merged.");
        if (originBranchGone)
            return (true, "Origin branch deleted after merge.");
        if (containedInMain)
            return (true, "All commits already contained in origin/main.");
        return (false, "Has commits not proven to be in origin/main.");
    }
}

/// <summary>
/// Lists local branches with sync state and the safe-delete verdict, and deletes branches that
/// verdict allows - re-verifying at the moment of deletion (state can change between render and
/// click). Reuses the same git signals as the worktree detector.
/// </summary>
public sealed class GitBranchService
{
    private readonly GitCommandRunner _git;
    private readonly IMergedPullRequestProbe _pullRequestProbe;

    public GitBranchService(GitCommandRunner? git = null, IMergedPullRequestProbe? pullRequestProbe = null)
    {
        _git = git ?? new GitCommandRunner();
        _pullRequestProbe = pullRequestProbe ?? new NullMergedPullRequestProbe();
    }

    public async Task<IReadOnlyList<BranchInfo>> ListAsync(string repoPath, CancellationToken ct = default)
    {
        FileLog.Write($"[GitBranchService] ListAsync: {repoPath}");
        var results = new List<BranchInfo>();

        var mainRef = await ResolveMainRefAsync(repoPath, ct);

        // Branch list with current marker, upstream, and last commit date in one call.
        var list = await _git.RunAsync(repoPath, new[]
        {
            "for-each-ref", "refs/heads",
            "--format=%(HEAD)%09%(refname:short)%09%(committerdate:unix)"
        }, ct);
        if (!list.Success)
            return results;

        // Branches held by linked worktrees (never deletable from under them).
        var worktreeBranches = await BranchesCheckedOutInWorktreesAsync(repoPath, ct);

        foreach (var raw in list.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split('\t');
            if (parts.Length < 3)
                continue;
            bool isCurrent = parts[0].Trim() == "*";
            var name = parts[1].Trim();
            DateTime? lastCommit = long.TryParse(parts[2].Trim(), out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                : null;

            bool inspectionOk = mainRef != null;
            bool originGone = false, containedInMain = false, prMerged = false;
            int ahead = 0, behind = 0;

            if (mainRef != null)
            {
                // "Origin gone" only proves a merge when the branch WAS on origin (had an upstream) -
                // a never-pushed branch's absence from origin proves nothing (same rule as worktrees).
                var upstream = await _git.RunAsync(repoPath, new[] { "config", "--get", $"branch.{name}.merge" }, ct);
                bool hadUpstream = upstream.Success && !string.IsNullOrWhiteSpace(upstream.Output);

                var lsRemote = await _git.RunAsync(repoPath, new[] { "ls-remote", "--heads", "origin", name }, ct);
                inspectionOk &= lsRemote.Success;
                originGone = hadUpstream && lsRemote.Success && string.IsNullOrWhiteSpace(lsRemote.Output);

                var cherry = await _git.RunAsync(repoPath, new[] { "cherry", mainRef, name }, ct);
                inspectionOk &= cherry.Success;
                containedInMain = cherry.Success && !cherry.Output
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Any(l => l.TrimStart().StartsWith('+'));

                var counts = await _git.RunAsync(repoPath, new[] { "rev-list", "--left-right", "--count", $"{mainRef}...{name}" }, ct);
                var nums = counts.Output.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (nums.Length >= 2 && int.TryParse(nums[0], out var l) && int.TryParse(nums[1], out var r))
                    (behind, ahead) = (l, r);

                prMerged = await _pullRequestProbe.IsBranchMergedAsync(repoPath, name, ct);
            }

            bool inWorktree = worktreeBranches.Contains(name);
            var (safe, why) = BranchSafetyEvaluator.Evaluate(isCurrent, inWorktree, inspectionOk, prMerged, originGone, containedInMain);

            results.Add(new BranchInfo
            {
                Name = name,
                IsCurrent = isCurrent,
                CheckedOutInWorktree = inWorktree,
                AheadOfMain = ahead,
                BehindMain = behind,
                OriginBranchExists = !originGone && mainRef != null,
                LastCommitUtc = lastCommit,
                SafeToDelete = safe,
                Explanation = why,
            });
        }

        return results;
    }

    /// <summary>
    /// Deletes a branch AFTER re-verifying its safe-delete verdict right now. Fail closed: any
    /// verdict other than safe refuses, with the reason.
    /// </summary>
    public async Task<(bool Deleted, string Message)> DeleteIfSafeAsync(string repoPath, string branch, CancellationToken ct = default)
    {
        FileLog.Write($"[GitBranchService] DeleteIfSafeAsync: {branch} in {repoPath}");
        var current = (await ListAsync(repoPath, ct)).FirstOrDefault(b => b.Name == branch);
        if (current is null)
            return (false, $"branch not found: {branch}");
        if (!current.SafeToDelete)
            return (false, current.Explanation);

        var del = await _git.RunAsync(repoPath, new[] { "branch", "-D", branch }, ct);
        return del.Success
            ? (true, $"deleted {branch} ({current.Explanation})")
            : (false, del.Error);
    }

    private async Task<HashSet<string>> BranchesCheckedOutInWorktreesAsync(string repoPath, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var list = await _git.RunAsync(repoPath, new[] { "worktree", "list", "--porcelain" }, ct);
        if (!list.Success)
            return set;
        bool first = true; // the first entry is the primary checkout - its branch is "current", not worktree-held
        foreach (var entry in WorktreeListParser.Parse(list.Output))
        {
            if (first) { first = false; continue; }
            if (entry.Branch != null)
                set.Add(entry.Branch);
        }
        return set;
    }

    private async Task<string?> ResolveMainRefAsync(string repoPath, CancellationToken ct)
    {
        var main = await _git.RunAsync(repoPath, new[] { "rev-parse", "--verify", "--quiet", "origin/main" }, ct);
        if (main.Success && !string.IsNullOrWhiteSpace(main.Output))
            return "origin/main";
        var master = await _git.RunAsync(repoPath, new[] { "rev-parse", "--verify", "--quiet", "origin/master" }, ct);
        if (master.Success && !string.IsNullOrWhiteSpace(master.Output))
            return "origin/master";
        return null;
    }
}
