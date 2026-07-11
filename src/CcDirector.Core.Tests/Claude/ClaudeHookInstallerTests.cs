using System.Text.Json;
using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

public class ClaudeHookInstallerTests
{
    [Fact]
    public void EnsureInstalled_Windows_WritesPowerShellScriptAndSettings_WithSessionStartMatchers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-hook-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settingsPath = ClaudeHookInstaller.EnsureInstalled(dir, forWindows: true);

            Assert.NotNull(settingsPath);
            Assert.True(File.Exists(settingsPath));

            var scriptPath = Path.Combine(dir, "report-session.ps1");
            Assert.True(File.Exists(scriptPath));

            // The script posts to the claude-hook endpoint using the injected per-session env.
            var script = File.ReadAllText(scriptPath);
            Assert.Contains("claude-hook", script);
            Assert.Contains("CC_SESSION_ID", script);
            Assert.Contains("CC_DIRECTOR_API", script);

            // It also fetches the fleet preamble and surfaces it into the session via the
            // SessionStart additionalContext field, so the agent knows the fleet instantly.
            Assert.Contains("fleet-preamble", script);
            Assert.Contains("additionalContext", script);
            Assert.Contains("hookSpecificOutput", script);

            var command = ReadFirstHookCommand(settingsPath!, out var matchers);
            Assert.Equal(new[] { "startup", "resume", "clear", "compact" }, matchers);
            Assert.Contains("powershell", command);
            Assert.Contains(scriptPath, command); // the script path is embedded in the command
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void EnsureInstalled_Unix_WritesShellScriptAndSettings_WithSessionStartMatchers()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-hook-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settingsPath = ClaudeHookInstaller.EnsureInstalled(dir, forWindows: false);

            Assert.NotNull(settingsPath);
            Assert.True(File.Exists(settingsPath));

            var scriptPath = Path.Combine(dir, "report-session.sh");
            Assert.True(File.Exists(scriptPath));

            // curl-only contract: the raw hook event is forwarded verbatim to the claude-hook
            // endpoint, and the preamble is fetched as ready-made hook output. No JSON is
            // parsed or built in shell.
            var script = File.ReadAllText(scriptPath);
            Assert.Contains("curl", script);
            Assert.Contains("claude-hook", script);
            Assert.Contains("fleet-preamble-hook-output", script);
            Assert.Contains("CC_SESSION_ID", script);
            Assert.Contains("CC_DIRECTOR_API", script);
            Assert.Contains("exit 0", script);
            Assert.DoesNotContain("powershell", script, StringComparison.OrdinalIgnoreCase);

            var command = ReadFirstHookCommand(settingsPath!, out var matchers);
            Assert.Equal(new[] { "startup", "resume", "clear", "compact" }, matchers);
            Assert.Contains("/bin/sh", command);
            Assert.Contains(scriptPath, command); // the script path is embedded, quoted for spaces
            Assert.DoesNotContain("powershell", command, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void EnsureInstalled_PlatformDefault_MatchesCurrentOperatingSystem()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-hook-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settingsPath = ClaudeHookInstaller.EnsureInstalled(dir);
            Assert.NotNull(settingsPath);

            var command = ReadFirstHookCommand(settingsPath!, out _);
            if (OperatingSystem.IsWindows())
                Assert.Contains("powershell", command);
            else
                Assert.Contains("/bin/sh", command);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void EnsureInstalled_IsIdempotent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-hook-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = ClaudeHookInstaller.EnsureInstalled(dir);
            var second = ClaudeHookInstaller.EnsureInstalled(dir);

            Assert.Equal(first, second);
            Assert.True(File.Exists(first!));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string ReadFirstHookCommand(string settingsPath, out string?[] matchers)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var sessionStart = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart");
        matchers = sessionStart.EnumerateArray()
            .Select(e => e.GetProperty("matcher").GetString())
            .ToArray();
        return sessionStart[0].GetProperty("hooks")[0].GetProperty("command").GetString()!;
    }
}
