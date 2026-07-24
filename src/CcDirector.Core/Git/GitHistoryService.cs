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

/// <summary>Reads the recent commit log for the History tab. Read-only, v1: no graph, no paging.</summary>
public sealed class GitHistoryService
{
    private readonly GitCommandRunner _git;

    public GitHistoryService(GitCommandRunner? git = null)
    {
        _git = git ?? new GitCommandRunner();
    }

    public async Task<IReadOnlyList<CommitInfo>> RecentAsync(string repoPath, int count = 30, CancellationToken ct = default)
    {
        var result = await _git.RunAsync(repoPath, new[]
        {
            "log", "-n", count.ToString(), "--format=%h%x09%an%x09%ct%x09%s"
        }, ct);
        if (!result.Success)
        {
            FileLog.Write($"[GitHistoryService] git log failed: {result.Error}");
            return Array.Empty<CommitInfo>();
        }
        return Parse(result.Output);
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
