using System.Collections.Concurrent;
using System.Diagnostics;
using CcDirector.Core.Hooks;
using CcDirector.Core.Pipes;

namespace CcDirector.TestHarness;

internal static class Program
{
    static async Task<int> Main()
    {
        Console.WriteLine("=== CcDirector Test Harness ===");
        Console.WriteLine("Proves prompt submission works via redirected stdin (no ConPTY).");
        Console.WriteLine();

        // 1. Verify prerequisites
        var claudePath = FindOnPath("claude.exe");
        if (claudePath == null)
        {
            Log("ERROR: claude.exe not found on PATH.");
            return 1;
        }
        Log($"Found claude at: {claudePath}");

        var relayScript = Path.Combine(AppContext.BaseDirectory, "Hooks", "hook-relay.ps1");
        if (!File.Exists(relayScript))
        {
            Log($"ERROR: Relay script not found at {relayScript}");
            return 1;
        }
        Log($"Relay script: {relayScript}");

        // 2. Install hooks (idempotent)
        Log("Installing hooks...");
        await HookInstaller.InstallAsync(relayScript, log: msg => Log($"  {msg}"));
        Log("Hooks installed.");

        // 3. Start pipe server
        var messages = new ConcurrentBag<PipeMessage>();
        var promptReceived = new TaskCompletionSource<PipeMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var pipeServer = new DirectorPipeServer(log: msg => Log($"[Pipe] {msg}"));
        pipeServer.OnMessageReceived += msg =>
        {
            messages.Add(msg);
            var detail = msg.HookEventName switch
            {
                "UserPromptSubmit" => $"prompt=\"{msg.Prompt}\"",
                "SessionStart" => $"source={msg.Source}",
                "SessionEnd" => $"reason={msg.Reason}",
                "Stop" => "",
                _ => $"tool={msg.ToolName}"
            };
            Log($"[PipeMsg] {msg.HookEventName,-25} session={msg.SessionId?[..Math.Min(8, msg.SessionId.Length)]}  {detail}");

            if (msg.HookEventName == "UserPromptSubmit")
                promptReceived.TrySetResult(msg);
        };
        pipeServer.Start();
        Log("Pipe server listening on CC_ClaudeDirector.");

        // 4. Start claude in pipe mode
        const string prompt = "Say hello";
        var workDir = @"D:\ReposFred\cc_director";

        var psi = new ProcessStartInfo
        {
            FileName = claudePath,
            Arguments = "-p --dangerously-skip-permissions",
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Log($"Starting claude in pipe mode...");
        var process = Process.Start(psi)!;
        Log($"Claude started (PID {process.Id}).");

        // 5. Send prompt via stdin, then close
        Log($"Sending prompt: \"{prompt}\"");
        await process.StandardInput.WriteLineAsync(prompt);
        process.StandardInput.Close();
        Log("Stdin closed.");

        // 6. Drain stdout/stderr in background
        var stdoutTask = Task.Run(async () =>
        {
            var output = await process.StandardOutput.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(output))
                Log($"[stdout] {output.Trim()[..Math.Min(500, output.Trim().Length)]}");
        });

        var stderrTask = Task.Run(async () =>
        {
            var output = await process.StandardError.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(output))
                Log($"[stderr] {output.Trim()[..Math.Min(500, output.Trim().Length)]}");
        });

        // 7. Wait for process exit (up to 60s)
        var processExitTask = Task.Run(() => process.WaitForExit(60_000));
        var exited = await processExitTask;

        if (!exited)
        {
            Log("WARNING: claude did not exit within 60s, killing...");
            process.Kill(entireProcessTree: true);
        }
        else
        {
            Log($"Claude exited with code {process.ExitCode}.");
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        // 8. Wait a few more seconds for late pipe events
        Log("Waiting 5s for late hook events...");
        await Task.Delay(5_000);

        // 9. Print summary
        Console.WriteLine();
        Console.WriteLine("--- PIPE MESSAGES ---");
        var ordered = messages.OrderBy(m => m.ReceivedAt).ToList();
        if (ordered.Count == 0)
        {
            Console.WriteLine("(none received)");
        }
        else
        {
            foreach (var m in ordered)
            {
                var ts = m.ReceivedAt.ToLocalTime().ToString("HH:mm:ss.fff");
                Console.WriteLine($"[{ts}] {m.HookEventName,-25} session={m.SessionId?[..Math.Min(8, m.SessionId.Length)]}");
            }
        }

        Console.WriteLine();

        // 10. Result
        if (promptReceived.Task.IsCompletedSuccessfully)
        {
            var msg = promptReceived.Task.Result;
            Console.WriteLine($"RESULT: SUCCESS — UserPromptSubmit received (prompt=\"{msg.Prompt}\")");
            Console.WriteLine("CONCLUSION: stdin delivery works. ConPTY is the problem.");
            return 0;
        }
        else
        {
            Console.WriteLine("RESULT: FAILURE — UserPromptSubmit was NOT received.");
            Console.WriteLine("NEXT: Investigate hooks or relay script issue.");
            return 1;
        }
    }

    private static string? FindOnPath(string exe)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir, exe);
            if (File.Exists(full)) return full;
        }
        // Also check common locations
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var npmPath = Path.Combine(appData, "npm", exe);
        if (File.Exists(npmPath)) return npmPath;

        return null;
    }

    private static void Log(string message)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        Console.WriteLine($"[{ts}] {message}");
    }
}
