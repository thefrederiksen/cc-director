using System.Diagnostics;
using System.Text.Json;
using VoiceChat.Core.Logging;

namespace VoiceChat.Core.Llm;

/// <summary>
/// Launches the Claude CLI in headless mode to send prompts and receive responses.
/// Uses "claude -p --output-format json" for one-shot interactions.
/// </summary>
public sealed class ClaudeCodeBridge
{
    private readonly string _claudePath;
    private readonly string _workingDirectory;
    private string? _sessionId;

    public event Action<string>? StatusChanged;

    public ClaudeCodeBridge(string claudePath = "claude", string? workingDirectory = null)
    {
        _claudePath = claudePath;
        _workingDirectory = workingDirectory ?? Environment.CurrentDirectory;
        VoiceLog.Write($"[ClaudeCodeBridge] Created: claudePath={_claudePath}, workDir={_workingDirectory}");
    }

    /// <summary>
    /// Sends a prompt to Claude Code and returns the response text.
    /// Maintains a session across calls for conversation context.
    /// </summary>
    public async Task<string> SendPromptAsync(string prompt, CancellationToken ct = default)
    {
        StatusChanged?.Invoke("Thinking...");

        var args = BuildArgs(prompt);
        VoiceLog.Write($"[ClaudeCodeBridge] SendPromptAsync: launching claude with args: {args}");

        var psi = new ProcessStartInfo
        {
            FileName = _claudePath,
            Arguments = args,
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Remove CLAUDECODE env var to prevent nested-session detection
        psi.Environment.Remove("CLAUDECODE");

        using var process = new Process { StartInfo = psi };
        process.Start();
        VoiceLog.Write($"[ClaudeCodeBridge] Process started: PID={process.Id}");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);
        VoiceLog.Write($"[ClaudeCodeBridge] Process exited: code={process.ExitCode}");

        if (!string.IsNullOrWhiteSpace(stderr))
            VoiceLog.Write($"[ClaudeCodeBridge] stderr: {stderr.Trim()}");

        if (process.ExitCode != 0)
        {
            var errorDetail = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
            VoiceLog.Write($"[ClaudeCodeBridge] FAILED: exit={process.ExitCode}, error={errorDetail}");
            throw new InvalidOperationException($"Claude exited with code {process.ExitCode}: {errorDetail}");
        }

        VoiceLog.Write($"[ClaudeCodeBridge] stdout length: {stdout.Length}");
        return ParseResponse(stdout);
    }

    private string BuildArgs(string prompt)
    {
        var escaped = prompt.Replace("\"", "\\\"");
        var args = $"-p \"{escaped}\" --output-format json";

        if (_sessionId is not null)
        {
            args = $"--resume \"{_sessionId}\" {args}";
        }
        else
        {
            _sessionId = Guid.NewGuid().ToString();
            args = $"--session-id \"{_sessionId}\" {args}";
        }

        return args;
    }

    private string ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("result", out var result))
        {
            var text = result.GetString() ?? string.Empty;
            VoiceLog.Write($"[ClaudeCodeBridge] Response parsed: {text.Length} chars");
            StatusChanged?.Invoke("Response received.");
            return text;
        }

        if (root.TryGetProperty("error", out var error))
        {
            VoiceLog.Write($"[ClaudeCodeBridge] Claude returned error: {error.GetString()}");
            throw new InvalidOperationException($"Claude error: {error.GetString()}");
        }

        VoiceLog.Write($"[ClaudeCodeBridge] Unexpected response: {json[..Math.Min(200, json.Length)]}");
        throw new InvalidOperationException("Unexpected Claude response format.");
    }

    public void ResetSession()
    {
        VoiceLog.Write("[ClaudeCodeBridge] Session reset.");
        _sessionId = null;
    }
}
