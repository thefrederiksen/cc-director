using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>One local branch with its sync state and the fail-closed safe-delete verdict.</summary>
public sealed record BranchInfo
{
    public string Name { get; init; } = "";
    public bool IsCurrent { get; init; }

    /// <summary>True when the branch is checked out in some linked worktree (never deletable).</summary>
    public bool CheckedOutInWorktree { get; init; }

    /// <summary>The commit the branch pointed at when the verdict was computed - deletion binds to it.</summary>
    public string TipCommit { get; init; } = "";

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
    /// <summary>git's "expected old value" meaning "the ref must not exist" - makes a restore
    /// create-only, so a concurrently recreated branch is never overwritten (ruling R3-1).</summary>
    private const string ZeroSha = "0000000000000000000000000000000000000000";

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
            "--format=%(HEAD)%09%(refname:short)%09%(committerdate:unix)%09%(objectname)"
        }, ct);
        if (!list.Success)
            return results;

        // Branches held by linked worktrees (never deletable from under them).
        var worktreeBranches = await BranchesCheckedOutInWorktreesAsync(repoPath, ct);

        foreach (var raw in list.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split('\t');
            if (parts.Length < 4)
                continue;
            bool isCurrent = parts[0].Trim() == "*";
            var name = parts[1].Trim();
            DateTime? lastCommit = long.TryParse(parts[2].Trim(), out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                : null;
            var tip = parts[3].Trim();

            bool inspectionOk = mainRef != null;
            bool originGone = false, containedInMain = false, prMerged = false;
            int ahead = 0, behind = 0;

            if (mainRef != null)
            {
                // "Upstream gone" only proves a merge when the branch HAD a configured upstream -
                // a never-pushed branch's absence from the remote proves nothing (same rule as
                // worktrees). The probe asks the configured remote for the configured ref name,
                // both of which can differ from origin/<local-name>.
                var upstream = await ConfiguredUpstreamProbe.ProbeAsync(_git, repoPath, name, ct);
                inspectionOk &= upstream.InspectionSucceeded;
                originGone = upstream.HasConfiguredUpstream && upstream.UpstreamGone;

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
                TipCommit = tip,
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
    /// verdict other than safe refuses, with the reason. The deletion binds to the verified tip:
    /// if a concurrent commit moves the branch between the verdict and the delete, git refuses
    /// and the branch survives.
    /// </summary>
    public async Task<(bool Deleted, string Message)> DeleteIfSafeAsync(string repoPath, string branch, CancellationToken ct = default)
    {
        FileLog.Write($"[GitBranchService] DeleteIfSafeAsync: {branch} in {repoPath}");
        var current = (await ListAsync(repoPath, ct)).FirstOrDefault(b => b.Name == branch);
        if (current is null)
            return (false, $"branch not found: {branch}");
        if (!current.SafeToDelete)
            return (false, current.Explanation);
        if (string.IsNullOrWhiteSpace(current.TipCommit))
            return (false, $"could not read the verified tip of {branch} - not deleted");

        return await DeleteAtVerifiedTipAsync(repoPath, branch, current.TipCommit, current.Explanation, ct);
    }

    /// <summary>
    /// The atomic delete: <c>git update-ref -d refs/heads/&lt;name&gt; &lt;verified-sha&gt;</c>.
    /// Git refuses when the ref no longer points at the verified commit, which closes the
    /// time-of-check to time-of-use window a plain force delete leaves open.
    ///
    /// update-ref binds the TIP but - unlike <c>git branch -D</c> - it does NOT refuse to delete
    /// a branch that is checked out. A checkout into a worktree can land between verification and
    /// deletion, which would leave that worktree with a broken symbolic HEAD. Compensating
    /// post-check: immediately after a successful delete the worktrees are listed; if any worktree
    /// HEAD still references the deleted branch, the ref is RESTORED at the verified sha (we hold
    /// the exact commit, so restoration is lossless) and the delete is reported as refused. The
    /// restore is CREATE-ONLY (ruling R3-1, expected old value of forty zeros): a branch another
    /// process recreated in the window stands untouched, and the outcome reports that a branch of
    /// the same name has since appeared.
    ///
    /// On a confirmed delete the branch's config section is cleaned up, as <c>git branch -D</c>
    /// would have done - but only after confirming the ref is still absent at that moment, so a
    /// branch another process just recreated does not lose ITS configuration. The residual
    /// millisecond window between that check and the removal is accepted and documented: worst
    /// case a just-recreated branch loses its tracking config - annoying, never destructive to
    /// commits.
    /// </summary>
    internal async Task<(bool Deleted, string Message)> DeleteAtVerifiedTipAsync(
        string repoPath, string branch, string verifiedSha, string explanation, CancellationToken ct = default)
    {
        var del = await _git.RunAsync(repoPath, new[] { "update-ref", "-d", $"refs/heads/{branch}", verifiedSha }, ct);
        if (!del.Success)
        {
            FileLog.Write($"[GitBranchService] delete refused for {branch}: ref no longer at {verifiedSha}: {del.Error.Trim()}");
            return (false, $"branch moved since it was verified - not deleted");
        }

        // The compensating post-check (ruling R2-1). Includes the FIRST entry too: the primary
        // checkout is just as broken by losing its checked-out branch as a linked worktree is.
        var wtList = await _git.RunAsync(repoPath, new[] { "worktree", "list", "--porcelain" }, ct);
        bool mustRestore;
        string refusal;
        if (!wtList.Success)
        {
            // Cannot PROVE no worktree holds the branch - fail closed and put the ref back.
            FileLog.Write($"[GitBranchService] worktree list failed after deleting {branch} - restoring: {wtList.Error.Trim()}");
            mustRestore = true;
            refusal = "could not verify the worktrees after deletion - the branch was restored, not deleted";
        }
        else
        {
            mustRestore = WorktreeListParser.Parse(wtList.Output).Any(e => e.Branch == branch);
            refusal = "branch is checked out in a worktree - restored, not deleted";
        }
        if (mustRestore)
        {
            // CREATE-ONLY restore (ruling R3-1): the expected-old-value of forty zeros makes git
            // refuse when the ref exists again. Another process can recreate the branch at a NEW
            // commit between our delete and this restore - an unconditional restore would silently
            // rewind that new branch to the verified sha and discard its tip.
            var restore = await _git.RunAsync(repoPath, new[] { "update-ref", $"refs/heads/{branch}", verifiedSha, ZeroSha }, ct);
            if (!restore.Success)
            {
                // Two distinct outcomes hide behind a refused create: either the ref exists again
                // (the concurrent recreation - the new branch stands, and the worktree HEAD is
                // valid again by that very fact), or the restore genuinely failed (loud error).
                var recreated = await _git.RunAsync(repoPath, new[] { "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}" }, ct);
                if (recreated.Success)
                {
                    FileLog.Write($"[GitBranchService] {branch} was recreated by another process after deletion - the new branch stands, not restored");
                    return (true, $"deleted {branch} ({explanation}) - a branch of the same name has since appeared and was left untouched");
                }
                // Restoration failing is a real error and must be loud: the commit still exists,
                // and the exact command to recreate the ref is part of the message.
                FileLog.Write($"[GitBranchService] RESTORE FAILED for {branch} at {verifiedSha}: {restore.Error.Trim()}");
                return (false, $"branch is checked out in a worktree and restoring it failed - run: git update-ref refs/heads/{branch} {verifiedSha}");
            }
            FileLog.Write($"[GitBranchService] {branch} is checked out in a worktree - restored at {verifiedSha}, not deleted");
            return (false, refusal);
        }

        // Config cleanup only when the ref is confirmed absent RIGHT NOW - another process may
        // have recreated the branch after our delete, and the new branch's config is not ours.
        var refCheck = await _git.RunAsync(repoPath, new[] { "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}" }, ct);
        if (refCheck.Success)
        {
            FileLog.Write($"[GitBranchService] {branch} was recreated after deletion - leaving the new branch's config in place");
            return (true, $"deleted {branch} ({explanation})");
        }

        // git branch -D removes the branch's config section; update-ref does not, so clean it up.
        // A branch without any config has no section - that outcome is expected, not an error.
        var cleanup = await _git.RunAsync(repoPath, new[] { "config", "--remove-section", $"branch.{branch}" }, ct);
        if (!cleanup.Success && !cleanup.Error.Contains("no such section", StringComparison.OrdinalIgnoreCase))
            FileLog.Write($"[GitBranchService] config cleanup for {branch} reported: {cleanup.Error.Trim()}");

        return (true, $"deleted {branch} ({explanation})");
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
