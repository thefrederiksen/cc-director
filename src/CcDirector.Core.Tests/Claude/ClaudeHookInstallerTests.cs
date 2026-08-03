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

            // The script uses the two per-session FILES the Director stamps - the pointer drop box and
            // the maintained preamble - not an address and a credential (remove-the-network-port
            // mission, phase 3). The full contract, including everything the script must NOT contain,
            // is HookScriptContractTests; this is the installer's own smoke check.
            var script = File.ReadAllText(scriptPath);
            Assert.Contains("CC_SESSION_POINTER_FILE", script);
            Assert.Contains("CC_SESSION_PREAMBLE_FILE", script);
            Assert.DoesNotContain("CC_DIRECTOR_API", script);

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

            // File contract: the raw hook event is written verbatim to the pointer drop box and the
            // maintained preamble file is printed as-is. No JSON is parsed or built in shell - and
            // nothing is fetched, so there is no curl.
            var script = File.ReadAllText(scriptPath);
            Assert.Contains("CC_SESSION_POINTER_FILE", script);
            Assert.Contains("CC_SESSION_PREAMBLE_FILE", script);
            Assert.DoesNotContain("curl", script);
            Assert.DoesNotContain("CC_DIRECTOR_API", script);
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
