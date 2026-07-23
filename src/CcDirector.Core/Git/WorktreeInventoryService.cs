using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Enumerates every worktree for a repository and computes each one's fail-closed
/// safe-to-reap verdict, exactly as issue #503 defines it. This is the detector: it
/// gathers the git facts and delegates the decision to <see cref="WorktreeSafetyEvaluator"/>.
/// The UI renders the result; it never re-derives a verdict.
/// </summary>
public sealed class WorktreeInventoryService
{
    private readonly GitCommandRunner _git;
    private readonly IMergedPullRequestProbe _pullRequestProbe;

    public WorktreeInventoryService(GitCommandRunner? git = null, IMergedPullRequestProbe? pullRequestProbe = null)
    {
        _git = git ?? new GitCommandRunner();
        _pullRequestProbe = pullRequestProbe ?? new NullMergedPullRequestProbe();
    }

    /// <summary>
    /// Enumerates the worktrees of <paramref name="repositoryPath"/> and returns their verdicts.
    /// When <paramref name="fetchPrune"/> is true a <c>git fetch --prune</c> runs first so the
    /// origin-branch-gone signal (C2) is current.
    /// </summary>
    public async Task<WorktreeInventory> GetInventoryAsync(string repositoryPath, bool fetchPrune = true, CancellationToken ct = default)
    {
        FileLog.Write($"[WorktreeInventoryService] GetInventoryAsync: repo={repositoryPath}, fetchPrune={fetchPrune}");
        try
        {
            if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
                return Failure(repositoryPath, $"repository path not found: {repositoryPath}");

            // Learn which origin branches were deleted (delete-branch-on-merge => gone == merged).
            if (fetchPrune)
                await _git.RunAsync(repositoryPath, new[] { "fetch", "--prune" }, ct);

            var mainRef = await ResolveMainRefAsync(repositoryPath, ct);

            var listResult = await _git.RunAsync(repositoryPath, new[] { "worktree", "list", "--porcelain" }, ct);
            if (!listResult.Success)
                return Failure(repositoryPath, $"git worktree list failed: {listResult.Error}");

            var rawEntries = WorktreeListParser.Parse(listResult.Output);
            var worktrees = new List<WorktreeInfo>();
            bool primaryAssigned = false;

            foreach (var entry in rawEntries)
            {
                if (entry.IsBare)
                    continue; // a bare repository has no primary checkout to protect or reap

                // Git lists the main working tree first; it is the primary checkout.
                bool isPrimary = !primaryAssigned;
                primaryAssigned = true;

                worktrees.Add(await BuildInfoAsync(repositoryPath, entry, isPrimary, mainRef, ct));
            }

            var safeCount = worktrees.Count(w => w.Safety == WorktreeSafety.SafeToReap);
            FileLog.Write($"[WorktreeInventoryService] inventory: {worktrees.Count} worktrees, {safeCount} safe to reap");

            return new WorktreeInventory
            {
                RepositoryPath = repositoryPath,
                Worktrees = worktrees,
                Success = true,
            };
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreeInventoryService] GetInventoryAsync FAILED: {ex.Message}");
            return Failure(repositoryPath, ex.Message);
        }
    }

    private async Task<WorktreeInfo> BuildInfoAsync(string repositoryPath, RawWorktreeEntry entry, bool isPrimary, string? mainRef, CancellationToken ct)
    {
        // Cleanliness is measured inside the worktree itself.
        var statusResult = await _git.RunAsync(entry.Path, new[] { "status", "--porcelain" }, ct);
        int dirtyCount = statusResult.Success
            ? statusResult.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
            : 0;
        bool isClean = statusResult.Success && dirtyCount == 0;

        // Every merge signal needs origin/main; if we could not resolve it, fail closed.
        bool inspectionOk = statusResult.Success && mainRef != null;

        bool prMerged = false, hasOpenPr = false, originGone = false, containedInMain = false, detachedAncestor = false;
        int ahead = 0, behind = 0;

        if (mainRef != null && !isPrimary)
        {
            if (entry.IsDetached)
            {
                // Detached HEAD: safe only if its commit is an ancestor of origin/main.
                var ancestor = await _git.RunAsync(repositoryPath, new[] { "merge-base", "--is-ancestor", entry.Head, mainRef }, ct);
                // exit 0 => ancestor, exit 1 => not an ancestor. Any other exit is a real error.
                inspectionOk &= ancestor.ExitCode is 0 or 1;
                detachedAncestor = ancestor.ExitCode == 0;
            }
            else if (entry.Branch != null)
            {
                // C2: has the origin branch been deleted (after the prune above)?
                var lsRemote = await _git.RunAsync(repositoryPath, new[] { "ls-remote", "--heads", "origin", entry.Branch }, ct);
                inspectionOk &= lsRemote.Success;
                originGone = lsRemote.Success && string.IsNullOrWhiteSpace(lsRemote.Output);

                // C3: does the branch add anything origin/main lacks? git cherry marks such commits with '+'.
                var cherry = await _git.RunAsync(repositoryPath, new[] { "cherry", mainRef, entry.Branch }, ct);
                inspectionOk &= cherry.Success;
                containedInMain = cherry.Success && !HasUniqueCommits(cherry.Output);

                // Ahead/behind counts for the needs-attention display.
                var counts = await _git.RunAsync(repositoryPath, new[] { "rev-list", "--left-right", "--count", $"{mainRef}...{entry.Branch}" }, ct);
                (behind, ahead) = ParseLeftRightCount(counts.Output);

                // C1: authoritative pull-request-merged signal (optional).
                prMerged = await _pullRequestProbe.IsBranchMergedAsync(repositoryPath, entry.Branch, ct);
                hasOpenPr = await _pullRequestProbe.HasOpenPullRequestAsync(repositoryPath, entry.Branch, ct);
            }
        }

        var facts = new WorktreeFacts
        {
            IsPrimary = isPrimary,
            IsDetachedHead = entry.IsDetached,
            IsClean = isClean,
            PullRequestMerged = prMerged,
            OriginBranchGone = originGone,
            ContainedInMain = containedInMain,
            DetachedHeadIsAncestorOfMain = detachedAncestor,
            InspectionSucceeded = inspectionOk,
        };

        var verdict = WorktreeSafetyEvaluator.Evaluate(facts);

        return new WorktreeInfo
        {
            Path = entry.Path,
            Branch = entry.Branch,
            HeadCommit = entry.Head,
            IsPrimary = isPrimary,
            IsDetachedHead = entry.IsDetached,
            IsClean = isClean,
            DirtyFileCount = dirtyCount,
            AheadOfMain = ahead,
            BehindMain = behind,
            HasOpenPullRequest = hasOpenPr,
            Safety = verdict.Safety,
            Reason = verdict.Reason,
            Explanation = verdict.Explanation,
        };
    }

    /// <summary>True if <c>git cherry</c> output contains a commit the upstream lacks (a line starting with '+').</summary>
    private static bool HasUniqueCommits(string cherryOutput) =>
        cherryOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.TrimStart().StartsWith('+'));

    /// <summary>
    /// Parses <c>git rev-list --left-right --count A...B</c> output ("&lt;left&gt;\t&lt;right&gt;").
    /// Left is commits in A (origin/main) not B, i.e. how far the branch is behind; right is
    /// commits in B not A, i.e. how far the branch is ahead. Returns (behind, ahead).
    /// </summary>
    private static (int Behind, int Ahead) ParseLeftRightCount(string output)
    {
        var parts = output.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[0], out var left) && int.TryParse(parts[1], out var right))
            return (left, right);
        return (0, 0);
    }

    private async Task<string?> ResolveMainRefAsync(string repositoryPath, CancellationToken ct)
    {
        var main = await _git.RunAsync(repositoryPath, new[] { "rev-parse", "--verify", "--quiet", "origin/main" }, ct);
        if (main.Success && !string.IsNullOrWhiteSpace(main.Output))
            return "origin/main";

        var master = await _git.RunAsync(repositoryPath, new[] { "rev-parse", "--verify", "--quiet", "origin/master" }, ct);
        if (master.Success && !string.IsNullOrWhiteSpace(master.Output))
            return "origin/master";

        FileLog.Write($"[WorktreeInventoryService] could not resolve origin/main or origin/master in {repositoryPath}");
        return null;
    }

    private static WorktreeInventory Failure(string repositoryPath, string error)
    {
        FileLog.Write($"[WorktreeInventoryService] inventory FAILED: {error}");
        return new WorktreeInventory { RepositoryPath = repositoryPath, Success = false, Error = error };
    }
}
