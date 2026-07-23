using System.Diagnostics;
using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Result of a single <c>git</c> invocation: the exit code plus captured stdout/stderr.
/// The exit code is kept because some commands (e.g. <c>merge-base --is-ancestor</c>)
/// communicate their answer through the exit status rather than output.
/// </summary>
public sealed class GitCommandResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string Output { get; init; } = "";
    public string Error { get; init; } = "";
}

/// <summary>
/// Runs a single <c>git</c> command in a working directory and captures its result.
/// Shared by the worktree inventory and reaper services so every git call is logged
/// and its exit code is available. Uses <see cref="ProcessStartInfo.ArgumentList"/> so
/// arguments are passed without shell quoting hazards.
/// </summary>
public sealed class GitCommandRunner
{
    /// <summary>
    /// Runs <c>git &lt;args&gt;</c> in <paramref name="workingDirectory"/> and returns its result.
    /// </summary>
    public async Task<GitCommandResult> RunAsync(string workingDirectory, string[] args, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return new GitCommandResult { Success = false, ExitCode = -1, Error = $"working directory not found: {workingDirectory}" };

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        FileLog.Write($"[GitCommandRunner] git {string.Join(' ', args)} (cwd={workingDirectory})");

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);

        var result = new GitCommandResult
        {
            Success = proc.ExitCode == 0,
            ExitCode = proc.ExitCode,
            Output = stdout.ToString().TrimEnd(),
            Error = stderr.ToString().TrimEnd(),
        };
        if (!result.Success)
            FileLog.Write($"[GitCommandRunner] git exit={proc.ExitCode}: {result.Error}");
        return result;
    }
}
