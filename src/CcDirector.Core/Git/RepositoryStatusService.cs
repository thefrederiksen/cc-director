using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Computes the at-a-glance status of on-disk repositories for the Repository screen. It composes
/// the pieces that already exist - remote URL, sync (ahead/behind), uncommitted count, and the
/// worktree triage - into one <see cref="RepositoryStatus"/> per repo. Reads only; never fetches by
/// default (uses the last-known remote-tracking refs), so a whole-machine scan stays fast.
/// </summary>
public sealed class RepositoryStatusService
{
    private readonly GitCommandRunner _git;
    private readonly GitStatusProvider _status;
    private readonly GitSyncStatusProvider _sync;
    private readonly WorktreeInventoryService _worktrees;

    public RepositoryStatusService(
        GitCommandRunner? git = null,
        GitStatusProvider? status = null,
        GitSyncStatusProvider? sync = null,
        WorktreeInventoryService? worktrees = null)
    {
        _git = git ?? new GitCommandRunner();
        _status = status ?? new GitStatusProvider();
        _sync = sync ?? new GitSyncStatusProvider();
        _worktrees = worktrees ?? new WorktreeInventoryService();
    }

    /// <summary>
    /// Enumerates the git repositories directly under each root directory and returns each one's
    /// status. A worktree (which carries a <c>.git</c> file, not a folder) is not a repository here -
    /// only real checkouts are returned; a repo's worktrees are summarised on its own status.
    /// </summary>
    public async Task<IReadOnlyList<RepositoryStatus>> ScanAsync(
        IEnumerable<string> rootPaths,
        IReadOnlyList<LiveSessionRef>? liveSessions = null,
        CancellationToken ct = default)
    {
        FileLog.Write("[RepositoryStatusService] ScanAsync");
        var results = new List<RepositoryStatus>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in rootPaths)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            foreach (var (_, path) in RemoteRepoProvider.ScanLocalRepos(root))
            {
                if (!seen.Add(WorktreeReaperService.NormalizePath(path)))
                    continue; // a repo reachable from two roots is listed once
                results.Add(await GetStatusAsync(path, liveSessions, fetchPrune: false, ct));
            }
        }

        FileLog.Write($"[RepositoryStatusService] ScanAsync: {results.Count} repositories");
        return results;
    }

    /// <summary>
    /// Builds the status of a single repository. <paramref name="fetchPrune"/> is off by default so a
    /// scan does not hit the network per repo; pass true for an explicit refresh of one repo.
    /// </summary>
    public async Task<RepositoryStatus> GetStatusAsync(
        string repoPath,
        IReadOnlyList<LiveSessionRef>? liveSessions = null,
        bool fetchPrune = false,
        CancellationToken ct = default)
    {
        FileLog.Write($"[RepositoryStatusService] GetStatusAsync: {repoPath}");
        try
        {
            if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
                return new RepositoryStatus { Path = repoPath, Success = false, Error = $"repository path not found: {repoPath}" };

            var remoteUrl = await ReadOriginUrlAsync(repoPath, ct);
            var (provider, org) = ClassifyRemote(remoteUrl);

            var sync = await _sync.GetSyncStatusAsync(repoPath);
            var uncommitted = await _status.GetCountAsync(repoPath);
            var inventory = await _worktrees.GetInventoryAsync(repoPath, fetchPrune, liveSessions, ct);

            var safe = inventory.SafeToReapCount;
            var inUse = inventory.InUseBySession.Count;
            var needs = inventory.NeedsAttention.Count;

            return new RepositoryStatus
            {
                Path = repoPath,
                Name = System.IO.Path.GetFileName(repoPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)),
                RemoteUrl = remoteUrl,
                Provider = provider,
                Org = org,
                Branch = sync.BranchName,
                IsDetachedHead = sync.IsDetachedHead,
                HasUpstream = sync.HasUpstream,
                AheadCount = sync.AheadCount,
                BehindCount = sync.BehindCount,
                BehindMainCount = sync.BehindMainCount,
                IsClean = uncommitted == 0,
                UncommittedCount = uncommitted,
                WorktreeCount = safe + inUse + needs,
                WorktreesSafeToReap = safe,
                WorktreesInUse = inUse,
                WorktreesNeedAttention = needs,
                Success = true,
            };
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryStatusService] GetStatusAsync FAILED: {ex.Message}");
            return new RepositoryStatus { Path = repoPath, Success = false, Error = ex.Message };
        }
    }

    /// <summary>Classifies an origin remote URL into a provider and its owner/org. Pure and testable.</summary>
    internal static (RepoProvider Provider, string? Org) ClassifyRemote(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return (RepoProvider.None, null);

        var gh = GitHubUrls.TryGitHubOwnerRepo(remoteUrl);
        if (gh is { } g)
            return (RepoProvider.GitHub, g.Owner);

        var az = GitHubUrls.TryAzureDevOpsOrgRepo(remoteUrl);
        if (az is { } a)
            return (RepoProvider.AzureDevOps, a.Org);

        return (RepoProvider.Other, null);
    }

    private async Task<string?> ReadOriginUrlAsync(string repoPath, CancellationToken ct)
    {
        var result = await _git.RunAsync(repoPath, new[] { "remote", "get-url", "origin" }, ct);
        return result.Success && !string.IsNullOrWhiteSpace(result.Output) ? result.Output.Trim() : null;
    }
}
