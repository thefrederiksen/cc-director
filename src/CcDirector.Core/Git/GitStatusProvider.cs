using System.Diagnostics;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

public enum GitFileStatus { Modified, Added, Deleted, Renamed, Copied, Untracked, Unknown }

public class GitFileEntry
{
    public GitFileStatus Status { get; init; }
    public string StatusChar { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public bool IsStaged { get; init; }
}

public class GitStatusResult
{
    public List<GitFileEntry> StagedChanges { get; init; } = new();
    public List<GitFileEntry> UnstagedChanges { get; init; } = new();
    public bool Success { get; init; }
    public string? Error { get; init; }
}

public class GitStatusProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);

    private readonly record struct CacheEntry(string RawOutput, GitStatusResult Result, DateTime Timestamp);

    // Static cache keyed by normalized repo path; shared across all GitStatusProvider instances
    private static readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _cacheLock = new();

    public async Task<GitStatusResult> GetStatusAsync(string repoPath)
    {
        FileLog.Write($"[GitStatusProvider] GetStatusAsync: repoPath={repoPath}");

        // Check cache first
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(repoPath, out var cached)
                && DateTime.UtcNow - cached.Timestamp < CacheTtl)
            {
                FileLog.Write($"[GitStatusProvider] GetStatusAsync: cache hit for {repoPath}");
                return cached.Result;
            }
        }

        var (rawOutput, error, exitCode) = await RunGitStatusAsync(repoPath);
        if (exitCode < 0)
            return new GitStatusResult { Success = false, Error = error ?? "Failed to start git process" };
        if (exitCode != 0)
            return new GitStatusResult { Success = false, Error = error };

        var result = ParsePorcelainOutput(rawOutput);

        lock (_cacheLock)
        {
            _cache[repoPath] = new CacheEntry(rawOutput, result, DateTime.UtcNow);
        }

        FileLog.Write($"[GitStatusProvider] GetStatusAsync: staged={result.StagedChanges.Count}, unstaged={result.UnstagedChanges.Count}");
        return result;
    }

    /// <summary>
    /// Returns just the total count of changed files (staged + unstaged) without
    /// allocating GitFileEntry objects. Uses the cache if available.
    /// </summary>
    public async Task<int> GetCountAsync(string repoPath)
    {
        FileLog.Write($"[GitStatusProvider] GetCountAsync: repoPath={repoPath}");

        // Check cache — if we have a full result, derive count from it
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(repoPath, out var cached)
                && DateTime.UtcNow - cached.Timestamp < CacheTtl)
            {
                int cachedCount = cached.Result.StagedChanges.Count + cached.Result.UnstagedChanges.Count;
                FileLog.Write($"[GitStatusProvider] GetCountAsync: cache hit, count={cachedCount}");
                return cachedCount;
            }
        }

        var (rawOutput, error, exitCode) = await RunGitStatusAsync(repoPath);
        if (exitCode != 0)
            return 0;

        int count = CountPorcelainLines(rawOutput);

        // Parse full result and cache it so subsequent GetStatusAsync calls benefit
        var result = ParsePorcelainOutput(rawOutput);
        lock (_cacheLock)
        {
            _cache[repoPath] = new CacheEntry(rawOutput, result, DateTime.UtcNow);
        }

        FileLog.Write($"[GitStatusProvider] GetCountAsync: count={count}");
        return count;
    }

    /// <summary>
    /// Returns the raw porcelain output string from the last cached result for
    /// the given repo, or null if not cached. Used for change detection.
    /// </summary>
    public string? GetCachedRawOutput(string repoPath)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(repoPath, out var cached)
                && DateTime.UtcNow - cached.Timestamp < CacheTtl)
            {
                return cached.RawOutput;
            }
        }
        return null;
    }

    private static async Task<(string Output, string? Error, int ExitCode)> RunGitStatusAsync(string repoPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "status --porcelain=v1 -u",
                WorkingDirectory = repoPath,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return ("", "Failed to start git process", -1);

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return (output, error, process.ExitCode);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GitStatusProvider] RunGitStatusAsync FAILED: {ex.Message}");
            return ("", ex.Message, -1);
        }
    }

    /// <summary>
    /// Counts the number of change entries in porcelain output without allocating
    /// per-file objects. Each entry may produce 1 or 2 counts (staged + unstaged).
    /// </summary>
    internal static int CountPorcelainLines(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return 0;

        int count = 0;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 3) continue;

            var x = line[0];
            var y = line[1];

            if (x == '?' && y == '?')
            {
                count++; // untracked = 1 unstaged entry
                continue;
            }
            if (x != ' ') count++; // staged
            if (y != ' ') count++; // unstaged
        }
        return count;
    }

    public static GitStatusResult ParsePorcelainOutput(string output)
    {
        var staged = new List<GitFileEntry>();
        var unstaged = new List<GitFileEntry>();

        if (string.IsNullOrWhiteSpace(output))
            return new GitStatusResult { StagedChanges = staged, UnstagedChanges = unstaged, Success = true };

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 3)
                continue;

            var x = line[0]; // index (staged) status
            var y = line[1]; // worktree (unstaged) status
            var filePath = line[3..].Trim();

            // Handle renames: "R  old -> new"
            if (filePath.Contains(" -> "))
                filePath = filePath.Split(" -> ")[1];

            // Strip trailing slashes from directory entries
            filePath = filePath.TrimEnd('/', '\\');

            var fileName = Path.GetFileName(filePath);
            if (string.IsNullOrEmpty(fileName))
                fileName = filePath;

            // Untracked files
            if (x == '?' && y == '?')
            {
                unstaged.Add(new GitFileEntry
                {
                    Status = GitFileStatus.Untracked,
                    StatusChar = "?",
                    FilePath = filePath,
                    FileName = fileName,
                    IsStaged = false
                });
                continue;
            }

            // Staged changes (X is non-space)
            if (x != ' ')
            {
                staged.Add(new GitFileEntry
                {
                    Status = CharToStatus(x),
                    StatusChar = x.ToString(),
                    FilePath = filePath,
                    FileName = fileName,
                    IsStaged = true
                });
            }

            // Unstaged changes (Y is non-space)
            if (y != ' ')
            {
                unstaged.Add(new GitFileEntry
                {
                    Status = CharToStatus(y),
                    StatusChar = y.ToString(),
                    FilePath = filePath,
                    FileName = fileName,
                    IsStaged = false
                });
            }
        }

        return new GitStatusResult { StagedChanges = staged, UnstagedChanges = unstaged, Success = true };
    }

    private static GitFileStatus CharToStatus(char c) => c switch
    {
        'M' => GitFileStatus.Modified,
        'A' => GitFileStatus.Added,
        'D' => GitFileStatus.Deleted,
        'R' => GitFileStatus.Renamed,
        'C' => GitFileStatus.Copied,
        '?' => GitFileStatus.Untracked,
        _ => GitFileStatus.Unknown
    };
}
