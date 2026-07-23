using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>The result of removing one worktree, including its proof-of-safety for the audit log.</summary>
public sealed class ReapOutcome
{
    public string Path { get; init; } = "";
    public string? Branch { get; init; }

    /// <summary>Why this worktree was safe to remove (logged for the audit trail).</summary>
    public string Proof { get; init; } = "";

    /// <summary>True only when the worktree was fully removed (deregistered and the folder deleted).</summary>
    public bool Removed { get; init; }

    /// <summary>Set when the folder could not be fully deleted (e.g. Windows-locked build outputs).</summary>
    public string? Leftover { get; init; }

    public string? Error { get; init; }

    public static ReapOutcome RemovedOk(WorktreeInfo w) =>
        new() { Path = WorktreeReaperService.NormalizePath(w.Path), Branch = w.Branch, Proof = w.Explanation, Removed = true };

    public static ReapOutcome LeftBehind(WorktreeInfo w) =>
        new()
        {
            Path = WorktreeReaperService.NormalizePath(w.Path),
            Branch = w.Branch,
            Proof = w.Explanation,
            Removed = false,
            Leftover = WorktreeReaperService.NormalizePath(w.Path),
            Error = "folder could not be fully deleted (files are locked)",
        };

    public static ReapOutcome Failed(WorktreeInfo w, string error) =>
        new() { Path = WorktreeReaperService.NormalizePath(w.Path), Branch = w.Branch, Proof = w.Explanation, Removed = false, Error = error };
}

/// <summary>The result of a reap run: what was removed, what folders remain, and what was skipped.</summary>
public sealed class ReapResult
{
    public bool Success { get; init; }
    public IReadOnlyList<ReapOutcome> Outcomes { get; init; } = Array.Empty<ReapOutcome>();

    /// <summary>Folders that could not be fully deleted - reported exactly rather than claimed removed.</summary>
    public IReadOnlyList<string> Leftovers { get; init; } = Array.Empty<string>();

    /// <summary>Safe worktrees deliberately NOT removed because a live session is using them.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = Array.Empty<string>();

    public string? Error { get; init; }

    public int RemovedCount => Outcomes.Count(o => o.Removed);

    public static ReapResult Failure(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// Removes worktrees that are provably safe to reap. It re-runs the detector immediately before
/// acting (state can change between the scan and the click), removes ONLY the safe set, protects
/// the primary checkout and any worktree a live session is using, and - crucially - reports any
/// folder it could not physically delete rather than claiming success. It only ever removes
/// worktrees; it never merges, rebases, force-pushes, or deletes origin branches.
/// </summary>
public sealed class WorktreeReaperService
{
    private readonly WorktreeInventoryService _inventory;
    private readonly GitCommandRunner _git;

    public WorktreeReaperService(WorktreeInventoryService? inventory = null, GitCommandRunner? git = null)
    {
        _git = git ?? new GitCommandRunner();
        _inventory = inventory ?? new WorktreeInventoryService(_git);
    }

    /// <summary>
    /// Re-checks safety and removes every currently safe-to-reap worktree except those whose path
    /// is in <paramref name="protectedPaths"/> (the working directories of live sessions).
    /// </summary>
    public async Task<ReapResult> ReapAsync(string repositoryPath, IReadOnlySet<string>? protectedPaths = null, CancellationToken ct = default)
    {
        FileLog.Write($"[WorktreeReaperService] ReapAsync: repo={repositoryPath}");
        try
        {
            if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
                return ReapResult.Failure($"repository path not found: {repositoryPath}");

            var protectedSet = BuildProtectedSet(protectedPaths);

            // Re-run the safety check immediately before acting - state can change between load and click.
            var inventory = await _inventory.GetInventoryAsync(repositoryPath, fetchPrune: true, liveSessions: null, ct);
            if (!inventory.Success)
                return ReapResult.Failure($"could not enumerate worktrees: {inventory.Error}");

            var outcomes = new List<ReapOutcome>();
            var skipped = new List<string>();

            foreach (var worktree in inventory.SafeToReap)
            {
                if (protectedSet.Contains(NormalizePath(worktree.Path)))
                {
                    FileLog.Write($"[WorktreeReaperService] SKIP (a live session is using it): {worktree.Path}");
                    skipped.Add(NormalizePath(worktree.Path));
                    continue;
                }

                outcomes.Add(await RemoveOneAsync(repositoryPath, worktree, ct));
            }

            // One prune at the end clears any admin entries whose directories are now gone.
            await _git.RunAsync(repositoryPath, new[] { "worktree", "prune" }, ct);

            var leftovers = outcomes.Where(o => o.Leftover != null).Select(o => o.Leftover!).ToList();
            var result = new ReapResult
            {
                Success = outcomes.All(o => o.Removed),
                Outcomes = outcomes,
                Leftovers = leftovers,
                Skipped = skipped,
            };
            FileLog.Write($"[WorktreeReaperService] reaped {result.RemovedCount}/{outcomes.Count}, leftovers={leftovers.Count}, skipped={skipped.Count}");
            return result;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WorktreeReaperService] ReapAsync FAILED: {ex.Message}");
            return ReapResult.Failure(ex.Message);
        }
    }

    private async Task<ReapOutcome> RemoveOneAsync(string repositoryPath, WorktreeInfo worktree, CancellationToken ct)
    {
        FileLog.Write($"[WorktreeReaperService] remove: path={worktree.Path}, branch={worktree.Branch}, proof={worktree.Reason} ({worktree.Explanation})");

        // Re-verify cleanliness at the very last moment. Authoritative, so a failure below can be
        // treated as a locked-file case rather than guessed at from git's error text.
        var status = await _git.RunAsync(worktree.Path, new[] { "status", "--porcelain" }, ct);
        if (!status.Success)
            return ReapOutcome.Failed(worktree, $"could not verify cleanliness: {status.Error}");
        if (!string.IsNullOrWhiteSpace(status.Output))
            return ReapOutcome.Failed(worktree, "worktree became dirty after the safety scan - not removed");

        await _git.RunAsync(repositoryPath, new[] { "worktree", "remove", worktree.Path }, ct);

        if (!Directory.Exists(worktree.Path))
            return ReapOutcome.RemovedOk(worktree);

        // git could not fully delete the folder (Windows-locked bin/obj DLLs). Finish the job ourselves.
        FileLog.Write($"[WorktreeReaperService] folder remains after git remove; attempting physical delete: {worktree.Path}");
        TryDeleteDirectory(worktree.Path);
        await _git.RunAsync(repositoryPath, new[] { "worktree", "prune" }, ct);

        if (!Directory.Exists(worktree.Path))
            return ReapOutcome.RemovedOk(worktree);

        FileLog.Write($"[WorktreeReaperService] LEFTOVER (locked files remain): {worktree.Path}");
        return ReapOutcome.LeftBehind(worktree);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            // Best effort - locked files remain and are reported as a leftover, not swallowed as success.
            FileLog.Write($"[WorktreeReaperService] physical delete incomplete for {path}: {ex.Message}");
        }
    }

    private static HashSet<string> BuildProtectedSet(IReadOnlySet<string>? protectedPaths)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (protectedPaths != null)
        {
            foreach (var p in protectedPaths)
                if (!string.IsNullOrWhiteSpace(p))
                    set.Add(NormalizePath(p));
        }
        return set;
    }

    /// <summary>Full-path, trailing-separator-trimmed form used to compare worktree paths across git and the OS.</summary>
    public static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
