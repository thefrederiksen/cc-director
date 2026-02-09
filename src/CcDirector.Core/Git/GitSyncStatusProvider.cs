using System.Diagnostics;

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
    public async Task<GitSyncStatus> GetSyncStatusAsync(string repoPath)
    {
        try
        {
            var output = await RunGitAsync(repoPath, "status --branch --porcelain=v2");
            if (output == null)
                return new GitSyncStatus { Success = false, Error = "Failed to start git process" };

            var status = ParseBranchHeaders(output);

            // Determine behind-main count if not on main
            if (!status.IsDetachedHead && !IsMainBranch(status.BranchName))
            {
                var mainBranch = await DetectMainBranchAsync(repoPath);
                if (mainBranch != null)
                {
                    var countOutput = await RunGitAsync(repoPath, $"rev-list --count HEAD..origin/{mainBranch}");
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

    public async Task FetchAsync(string repoPath)
    {
        try
        {
            await RunGitAsync(repoPath, "fetch --quiet");
        }
        catch
        {
            // Swallow — invalid paths, git not found, etc.
        }
    }

    private async Task<string?> DetectMainBranchAsync(string repoPath)
    {
        var result = await RunGitAsync(repoPath, "rev-parse --verify --quiet origin/main");
        if (result != null)
            return "main";

        result = await RunGitAsync(repoPath, "rev-parse --verify --quiet origin/master");
        if (result != null)
            return "master";

        return null;
    }

    private static bool IsMainBranch(string branchName) =>
        branchName is "main" or "master";

    private async Task<string?> RunGitAsync(string repoPath, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = repoPath,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            return null;

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        return process.ExitCode == 0 ? output : null;
    }
}
