namespace CcDirector.Core.Utilities;

/// <summary>
/// Builds GitHub web URLs for a local repository by resolving its origin remote.
/// Used by the desktop "New GitHub Issue" session menu item, the screenshot Issue
/// button, and the Director's GET /sessions/{sid}/github-urls endpoint (which the
/// Cockpit's session menu calls, since the repo lives on the Director's machine).
/// </summary>
public static class GitHubUrls
{
    private static readonly TimeSpan RemoteUrlRegexTimeout = TimeSpan.FromMilliseconds(50);

    // repoPath -> resolved "owner/repo" slug, or "" when the checkout has no github.com origin. A local
    // clone's origin does not change over a process lifetime, and resolving it spawns a git subprocess, so
    // this is cached: ResolveSlugCached runs on the Director's roster path (every session, every poll) and
    // must not fork git each time. Misses ("" - no origin, not a git repo, non-github remote) are cached
    // too, so a checkout without a GitHub origin is probed once, not on every poll.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> SlugCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Resolves the repo's origin remote via git and converts it to the GitHub
    /// "new issue" URL. Throws with a clear message when the directory is not a
    /// git repo, has no origin remote, or the origin is not on github.com.
    /// </summary>
    public static string BuildNewIssueUrl(string repoPath)
    {
        FileLog.Write($"[GitHubUrls] BuildNewIssueUrl: repoPath={repoPath}");
        if (!Directory.Exists(repoPath))
            throw new InvalidOperationException($"Directory not found: {repoPath}");

        var origin = GetOriginRemoteUrl(repoPath);
        var url = ParseNewIssueUrl(origin);
        FileLog.Write($"[GitHubUrls] BuildNewIssueUrl: origin={origin} -> {url}");
        return url;
    }

    /// <summary>
    /// The GitHub "owner/repo" slug for a local checkout, or "" when the checkout has no github.com origin
    /// (no origin remote, not a git repo, or a non-GitHub remote). Best-effort and NEVER throws: an empty
    /// result is a legitimate answer - not every checkout is on GitHub - and the caller (the DevThrottle
    /// Stats repo grouping) falls back to the local path for those. Cached by path; see <see cref="SlugCache"/>.
    ///
    /// This is the key that makes every worktree and every per-machine clone of one repository roll up into
    /// a single row on the Repos page: they all share one origin remote, so they all resolve to one slug.
    /// </summary>
    public static string ResolveSlugCached(string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return "";
        return SlugCache.GetOrAdd(repoPath, ResolveSlugUncached);
    }

    private static string ResolveSlugUncached(string repoPath)
    {
        try
        {
            if (!Directory.Exists(repoPath)) return "";
            var slug = ParseSlug(GetOriginRemoteUrl(repoPath));
            FileLog.Write($"[GitHubUrls] ResolveSlugCached: {repoPath} -> {slug}");
            return slug;
        }
        catch (Exception ex)
        {
            // A checkout with no origin, or one whose origin is not on github.com, is an expected state, not
            // a failure to hide: it simply has no slug and groups by its path instead. Recorded, then "".
            FileLog.Write($"[GitHubUrls] ResolveSlugCached: {repoPath} has no GitHub slug: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Converts an origin remote URL to the GitHub "new issue" URL. Pure string
    /// logic so it is unit-testable without git. Accepts the three remote shapes:
    ///   git@github.com:owner/repo.git
    ///   ssh://git@github.com/owner/repo.git
    ///   https://github.com/owner/repo(.git)
    /// Throws when the remote is not on github.com.
    /// </summary>
    internal static string ParseNewIssueUrl(string originUrl)
        => $"https://github.com/{ParseSlug(originUrl)}/issues/new";

    /// <summary>
    /// Extracts the "owner/repo" slug from an origin remote URL. Pure string logic so it is unit-testable
    /// without git; accepts the same three remote shapes as <see cref="ParseNewIssueUrl"/>. Throws when the
    /// remote is empty or not on github.com.
    /// </summary>
    internal static string ParseSlug(string originUrl)
    {
        if (string.IsNullOrWhiteSpace(originUrl))
            throw new ArgumentException("Origin remote URL is required", nameof(originUrl));

        var match = System.Text.RegularExpressions.Regex.Match(
            originUrl.Trim(), @"github\.com[:/](?<owner>[^/\s]+)/(?<repo>[^/\s]+?)(\.git)?$",
            System.Text.RegularExpressions.RegexOptions.None, RemoteUrlRegexTimeout);
        if (!match.Success)
            throw new InvalidOperationException($"Origin is not a GitHub remote: {originUrl}");

        return $"{match.Groups["owner"].Value}/{match.Groups["repo"].Value}";
    }

    /// <summary>
    /// Runs "git remote get-url origin" in the repo and returns the trimmed URL.
    /// Throws when the repo has no origin remote (or is not a git repo).
    /// </summary>
    private static string GetOriginRemoteUrl(string repoPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = "remote get-url origin",
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git");
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0 || output.Length == 0)
            throw new InvalidOperationException($"No 'origin' remote in {repoPath}");
        return output;
    }
}
