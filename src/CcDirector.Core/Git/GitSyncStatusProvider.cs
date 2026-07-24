using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

public class GitSyncStatus
{
    public string BranchName { get; init; } = "";
    public bool IsDetachedHead { get; init; }
    public bool HasUpstream { get; init; }
    public int AheadCount { get; init; }
    public int BehindCount { get; init; }
    public int BehindMainCount { get; init; } // -1 if on main already
    public string MainBranchName { get; init; } = "";
    public bool Success { get; init; }
    public string? Error { get; init; }
}

public class GitSyncStatusProvider
{
    public async Task<GitSyncStatus> GetSyncStatusAsync(string repoPath, CancellationToken ct = default)
    {
        try
        {
            var output = await RunGitAsync(repoPath, new[] { "status", "--branch", "--porcelain=v2" }, ct);
            if (output == null)
                return new GitSyncStatus { Success = false, Error = "Failed to start git process" };

            var status = ParseBranchHeaders(output);

            // Determine behind-main count if not on main
            if (!status.IsDetachedHead && !IsMainBranch(status.BranchName))
            {
                var mainBranch = await DetectMainBranchAsync(repoPath, ct);
                if (mainBranch != null)
                {
                    var countOutput = await RunGitAsync(repoPath, new[] { "rev-list", "--count", $"HEAD..origin/{mainBranch}" }, ct);
                    if (countOutput != null && int.TryParse(countOutput.Trim(), out var count))
                    {
                        return new GitSyncStatus
                        {
                            BranchName = status.BranchName,
                            IsDetachedHead = status.IsDetachedHead,
                            HasUpstream = status.HasUpstream,
                            AheadCount = status.AheadCount,
                            BehindCount = status.BehindCount,
                            BehindMainCount = count,
                            MainBranchName = mainBranch,
                            Success = true
                        };
                    }
                }
            }

            return status;
        }
        catch (OperationCanceledException)
        {
            // A superseded scan cancelled this compute - propagate, never fold into a failed status.
            throw;
        }
        catch (Exception ex)
        {
            return new GitSyncStatus { Success = false, Error = ex.Message };
        }
    }

    public static GitSyncStatus ParseBranchHeaders(string output)
    {
        string branchName = "";
        bool isDetached = false;
        bool hasUpstream = false;
        int ahead = 0;
        int behind = 0;

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("# branch.head "))
            {
                branchName = line["# branch.head ".Length..];
                if (branchName == "(detached)")
                    isDetached = true;
            }
            else if (line.StartsWith("# branch.upstream "))
            {
                hasUpstream = true;
            }
            else if (line.StartsWith("# branch.ab "))
            {
                // Format: # branch.ab +3 -1
                var parts = line["# branch.ab ".Length..].Split(' ');
                if (parts.Length >= 2)
                {
                    if (parts[0].StartsWith('+'))
                        int.TryParse(parts[0][1..], out ahead);
                    if (parts[1].StartsWith('-'))
                        int.TryParse(parts[1][1..], out behind);
                }
            }
        }

        return new GitSyncStatus
        {
            BranchName = branchName,
            IsDetachedHead = isDetached,
            HasUpstream = hasUpstream,
            AheadCount = ahead,
            BehindCount = behind,
            BehindMainCount = IsMainBranch(branchName) ? -1 : 0,
            MainBranchName = "",
            Success = true
        };
    }

    public async Task FetchAsync(string repoPath, CancellationToken ct = default)
    {
        try
        {
            await RunGitAsync(repoPath, new[] { "fetch", "--quiet" }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Swallow — invalid paths, git not found, etc.
        }
    }

    private async Task<string?> DetectMainBranchAsync(string repoPath, CancellationToken ct)
    {
        var result = await RunGitAsync(repoPath, new[] { "rev-parse", "--verify", "--quiet", "origin/main" }, ct);
        if (result != null)
            return "main";

        result = await RunGitAsync(repoPath, new[] { "rev-parse", "--verify", "--quiet", "origin/master" }, ct);
        if (result != null)
            return "master";

        return null;
    }

    private static bool IsMainBranch(string branchName) =>
        branchName is "main" or "master";

    private async Task<string?> RunGitAsync(string repoPath, string[] args, CancellationToken ct)
    {
        // ProcessRunner drains BOTH pipes (the old code never drained stderr, so a git command that
        // filled its error pipe could deadlock) and honors cancellation by killing the child so a
        // superseded scan does not leave git processes behind (issue 516).
        var r = await ProcessRunner.RunAsync("git", args, repoPath, ct);
        if (!r.Started)
            return null;
        return r.ExitCode == 0 ? r.StandardOutput : null;
    }
}
