using System.Diagnostics;
using CcDirector.Core.Utilities;

namespace CcDirector.Engine.Jobs;

public sealed class ProcessJob : IJob
{
    private readonly string _command;
    private readonly string? _workingDir;
    private readonly int _timeoutSeconds;

    public string Name { get; }

    public ProcessJob(string name, string command, string? workingDir = null, int timeoutSeconds = 300)
    {
        Name = name;
        _command = command;
        _workingDir = workingDir;
        _timeoutSeconds = timeoutSeconds;
    }

    public async Task<JobResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        FileLog.Write($"[ProcessJob] Executing: name={Name}, command={_command}");

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {_command}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (!string.IsNullOrEmpty(_workingDir))
            startInfo.WorkingDirectory = _workingDir;

        using var process = new Process { StartInfo = startInfo };

        var stdoutTask = Task.CompletedTask as Task<string>;
        var stderrTask = Task.CompletedTask as Task<string>;

        process.Start();
        stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            FileLog.Write($"[ProcessJob] Timeout after {_timeoutSeconds}s: name={Name}");
            KillProcess(process);
            var stdout = await ReadSafe(stdoutTask);
            var stderr = await ReadSafe(stderrTask);
            return new JobResult(false, stdout, $"Timed out after {_timeoutSeconds} seconds. {stderr}");
        }

        var finalStdout = await ReadSafe(stdoutTask);
        var finalStderr = await ReadSafe(stderrTask);
        var exitCode = process.ExitCode;

        FileLog.Write($"[ProcessJob] Completed: name={Name}, exitCode={exitCode}");

        return new JobResult(
            Success: exitCode == 0,
            Output: finalStdout,
            Error: exitCode != 0 ? $"Exit code {exitCode}. {finalStderr}" : null
        );
    }

    private static void KillProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ProcessJob] Failed to kill process: {ex.Message}");
        }
    }

    private static async Task<string> ReadSafe(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            return string.Empty;
        }
    }
}
