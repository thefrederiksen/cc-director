using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>One commit row for the History tab.</summary>
public sealed record CommitInfo
{
    public string ShortHash { get; init; } = "";
    public string Author { get; init; } = "";
    public DateTime? WhenUtc { get; init; }
    public string Subject { get; init; } = "";
}

/// <summary>
/// The recent commits, WITH whether the read succeeded. An empty list is not enough on its own: a
/// repository with no commits and a git that could not run both produce zero rows, and the History
/// tab rendered both as "No commits." - stating as fact something it had not established
/// (devthrottle_internal issue #1048). The caller cannot reach the commits without passing the
/// success flag, which is the point of the type.
/// </summary>
public readonly record struct CommitHistory(bool Success, IReadOnlyList<CommitInfo> Commits, string? Error)
{
    public static CommitHistory Ok(IReadOnlyList<CommitInfo> commits) => new(true, commits, null);

    public static CommitHistory Failed(string? error) => new(false, Array.Empty<CommitInfo>(), error);
}

/// <summary>Reads the recent commit log for the History tab. Read-only, v1: no graph, no paging.</summary>
public sealed class GitHistoryService
{
    private readonly GitCommandRunner _git;

    public GitHistoryService(GitCommandRunner? git = null)
    {
        _git = git ?? new GitCommandRunner();
    }

    public async Task<CommitHistory> RecentAsync(string repoPath, int count = 30, CancellationToken ct = default)
    {
        var result = await _git.RunAsync(repoPath, new[]
        {
            "log", "-n", count.ToString(), "--format=%h%x09%an%x09%ct%x09%s"
        }, ct);
        if (!result.Success)
        {
            // NOT an empty list. Returning one here is what let the History tab say "No commits."
            // about a repository it had never managed to read.
            FileLog.Write($"[GitHistoryService] git log failed: {result.Error}");
            return CommitHistory.Failed(result.Error);
        }
        return CommitHistory.Ok(Parse(result.Output));
    }

    /// <summary>Pure parser for the tab-separated log format (unit-tested).</summary>
    internal static IReadOnlyList<CommitInfo> Parse(string output)
    {
        var items = new List<CommitInfo>();
        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split('\t', 4);
            if (parts.Length < 4)
                continue;
            items.Add(new CommitInfo
            {
                ShortHash = parts[0].Trim(),
                Author = parts[1].Trim(),
                WhenUtc = long.TryParse(parts[2].Trim(), out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
                    : null,
                Subject = parts[3].Trim(),
            });
        }
        return items;
    }
}
