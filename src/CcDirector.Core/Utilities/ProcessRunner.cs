using System.Diagnostics;

namespace CcDirector.Core.Utilities;

/// <summary>
/// Runs a child process and captures its stdout and stderr safely. Two hazards this exists to avoid
/// (issue 516):
///   1. Pipe deadlock. Reading one redirected stream to end before the other lets the child block
///      writing to the second pipe once its buffer fills (about 64 KB), while the parent blocks
///      waiting for end-of-stream on the first - neither can progress. Both streams are drained
///      CONCURRENTLY here so a full stderr can never stall stdout, or the reverse.
///   2. Orphaned children on cancellation. Disposing a <see cref="Process"/> does NOT terminate the
///      operating-system process. When the caller cancels, the whole process tree is killed so a
///      hung child (a stuck credential or network prompt) does not outlive the cancelled call.
/// </summary>
public static class ProcessRunner
{
    /// <summary>The captured result. <see cref="Started"/> is false when the process could not start.</summary>
    public readonly record struct Result(int ExitCode, string StandardOutput, string StandardError, bool Started);

    /// <summary>
    /// Starts <paramref name="fileName"/> with <paramref name="args"/> in
    /// <paramref name="workingDirectory"/>, drains both pipes concurrently, and returns once the
    /// process exits. On cancellation the child process tree is killed and
    /// <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    public static async Task<Result> RunAsync(
        string fileName, IReadOnlyList<string> args, string? workingDirectory, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrEmpty(workingDirectory))
            psi.WorkingDirectory = workingDirectory;
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
            return new Result(-1, "", $"{fileName} could not start", Started: false);

        // Start draining BOTH pipes immediately and concurrently - neither read waits on the other.
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct);
            var stdout = await outTask;
            var stderr = await errTask;
            return new Result(proc.ExitCode, stdout, stderr, Started: true);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(proc);
            // Observe the drain tasks so a killed-pipe read does not surface as an unobserved fault.
            await ObserveQuietlyAsync(outTask);
            await ObserveQuietlyAsync(errTask);
            throw;
        }
    }

    private static void TryKillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already exited, or the operating system refused - best effort, nothing more to do.
        }
    }

    private static async Task ObserveQuietlyAsync(Task task)
    {
        try { await task; }
        catch { /* the read was cancelled or the pipe was closed by the kill - expected */ }
    }
}
