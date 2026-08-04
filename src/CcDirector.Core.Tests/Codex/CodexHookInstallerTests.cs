using System.Text.Json;
using CcDirector.Core.Codex;
using Xunit;

namespace CcDirector.Core.Tests.Codex;

public class CodexHookInstallerTests
{
    private static (string ScriptDir, string HooksPath) TempPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-codex-hook-test-" + Guid.NewGuid().ToString("N"));
        return (Path.Combine(root, "scripts"), Path.Combine(root, ".codex", "hooks.json"));
    }

    [Fact]
    public void EnsureInstalled_WritesScript_AndAddsSessionStartHook()
    {
        var (scriptDir, hooksPath) = TempPaths();
        try
        {
            var ok = CodexHookInstaller.EnsureInstalled(scriptDir, hooksPath, forWindows: true);

            Assert.True(ok);
            var scriptPath = Path.Combine(scriptDir, "cc-director-preamble.ps1");
            Assert.True(File.Exists(scriptPath));
            var script = File.ReadAllText(scriptPath);
            // Remove-the-network-port mission, phase 3: the hook prints the preamble FILE the Director
            // maintains for the session instead of fetching it from a route.
            // The finished envelope is IN the file, so the script no longer builds one - which is what
            // stops it and the two Claude scripts drifting apart about its shape.
            Assert.Contains("CC_SESSION_PREAMBLE_FILE", script);
            Assert.DoesNotContain("additionalContext", script);
            Assert.DoesNotContain("CC_DIRECTOR_API", script);

            using var doc = JsonDocument.Parse(File.ReadAllText(hooksPath));
            var sessionStart = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart");
            var entry = Assert.Single(sessionStart.EnumerateArray());
            Assert.Equal("startup|resume|clear|compact", entry.GetProperty("matcher").GetString());
            var command = entry.GetProperty("hooks")[0].GetProperty("command").GetString();
            Assert.Contains("cc-director-preamble.ps1", command);
        }
        finally { TryDeleteRoot(scriptDir); }
    }

    [Fact]
    public void EnsureInstalled_IsIdempotent_NoDuplicateEntry()
    {
        var (scriptDir, hooksPath) = TempPaths();
        try
        {
            CodexHookInstaller.EnsureInstalled(scriptDir, hooksPath);
            CodexHookInstaller.EnsureInstalled(scriptDir, hooksPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(hooksPath));
            var sessionStart = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart");
            Assert.Single(sessionStart.EnumerateArray());
        }
        finally { TryDeleteRoot(scriptDir); }
    }

    [Fact]
    public void EnsureInstalled_PreservesExistingUserHooks()
    {
        var (scriptDir, hooksPath) = TempPaths();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
            // A user who already has a PreToolUse hook AND their own SessionStart hook.
            File.WriteAllText(hooksPath, """
            {
              "hooks": {
                "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "my-policy.py" } ] } ],
                "SessionStart": [ { "matcher": "startup", "hooks": [ { "type": "command", "command": "my-greeting.py" } ] } ]
              }
            }
            """);

            var ok = CodexHookInstaller.EnsureInstalled(scriptDir, hooksPath, forWindows: true);

            Assert.True(ok);
            using var doc = JsonDocument.Parse(File.ReadAllText(hooksPath));
            var hooks = doc.RootElement.GetProperty("hooks");
            // The user's PreToolUse hook survives untouched.
            Assert.Equal("my-policy.py",
                hooks.GetProperty("PreToolUse")[0].GetProperty("hooks")[0].GetProperty("command").GetString());
            // SessionStart now has BOTH the user's entry and ours.
            var sessionStart = hooks.GetProperty("SessionStart").EnumerateArray().ToList();
            Assert.Equal(2, sessionStart.Count);
            var commands = sessionStart
                .Select(e => e.GetProperty("hooks")[0].GetProperty("command").GetString() ?? "")
                .ToList();
            Assert.Contains(commands, c => c.Contains("my-greeting.py"));
            Assert.Contains(commands, c => c.Contains("cc-director-preamble.ps1"));
        }
        finally { TryDeleteRoot(scriptDir); }
    }

    [Fact]
    public void EnsureInstalled_MalformedHooksJson_ReturnsFalse_AndDoesNotClobber()
    {
        var (scriptDir, hooksPath) = TempPaths();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
            const string garbage = "this is not json {{{";
            File.WriteAllText(hooksPath, garbage);

            var ok = CodexHookInstaller.EnsureInstalled(scriptDir, hooksPath);

            Assert.False(ok);
            // The user's file is left exactly as it was - never overwritten.
            Assert.Equal(garbage, File.ReadAllText(hooksPath));
        }
        finally { TryDeleteRoot(scriptDir); }
    }

    /// <summary>
    /// THE MACOS AND LINUX FORM, PROVEN FROM ANY PLATFORM. Inspection 3, finding 6: this installer had
    /// no operating-system branch and wrote a "powershell" command everywhere, while the Claude
    /// installer beside it branched correctly. PowerShell Core on macOS and Linux is "pwsh" and is
    /// usually absent, so the installed hook was simply not a runnable command and every Codex session
    /// off Windows silently got no preamble. The flavour is a parameter precisely so this can be
    /// asserted on a Windows run - a platform-conditional test would have skipped where it mattered.
    /// </summary>
    [Fact]
    public void EnsureInstalled_OnMacOrLinux_WritesAShellScriptAndAShellCommand()
    {
        var (scriptDir, hooksPath) = TempPaths();
        try
        {
            Assert.True(CodexHookInstaller.EnsureInstalled(scriptDir, hooksPath, forWindows: false));

            var scriptPath = Path.Combine(scriptDir, "cc-director-preamble.sh");
            Assert.True(File.Exists(scriptPath), "no POSIX shell script was written");
            var script = File.ReadAllText(scriptPath);
            Assert.StartsWith("#!/bin/sh", script, StringComparison.Ordinal);
            Assert.Contains("CC_SESSION_PREAMBLE_FILE", script);
            Assert.DoesNotContain("powershell", script, StringComparison.OrdinalIgnoreCase);

            using var doc = JsonDocument.Parse(File.ReadAllText(hooksPath));
            var command = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart")[0]
                .GetProperty("hooks")[0].GetProperty("command").GetString()!;
            Assert.StartsWith("/bin/sh ", command, StringComparison.Ordinal);
            Assert.DoesNotContain("powershell", command, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDeleteRoot(scriptDir); }
    }

    /// <summary>
    /// TWO NAMED INSTANCES MUST NOT LEAVE TWO HOOKS. Inspection 3, finding 7: the hooks file is global
    /// (~/.codex/hooks.json) while the script directory is scoped to the named instance, and idempotence
    /// compared the whole command string - so every instance looked like a different hook and appended
    /// another one. The inspector proved it: two installs, two SessionStart entries, both reading the
    /// same variable, so a Codex launch received its preamble twice. A removed or renamed instance also
    /// left its command behind for ever.
    ///
    /// The second install must both REMOVE the first instance's entry and leave the current one, which
    /// is why this asserts the surviving command as well as the count. Asserting only the count would
    /// pass on an implementation that skipped the write entirely and left the stale entry in place.
    /// </summary>
    [Fact]
    public void TwoNamedInstances_LeaveExactlyOneOfOurHooks()
    {
        var (defaultScripts, hooksPath) = TempPaths();
        var workScripts = Path.Combine(Directory.GetParent(defaultScripts)!.FullName, "work-scripts");
        try
        {
            Assert.True(CodexHookInstaller.EnsureInstalled(defaultScripts, hooksPath, forWindows: true));
            Assert.True(CodexHookInstaller.EnsureInstalled(workScripts, hooksPath, forWindows: true));

            using var doc = JsonDocument.Parse(File.ReadAllText(hooksPath));
            var entries = doc.RootElement.GetProperty("hooks").GetProperty("SessionStart")
                .EnumerateArray().ToList();

            var entry = Assert.Single(entries);
            var command = entry.GetProperty("hooks")[0].GetProperty("command").GetString()!;
            Assert.Contains(workScripts, command, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(defaultScripts, command, StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDeleteRoot(defaultScripts); }
    }

    /// <summary>
    /// A hook left by a build before this fix is OURS and is cleaned up, not duplicated. Every machine
    /// with Codex already has a "report-preamble" entry; without this the next install would add the new
    /// one beside it and deliver the preamble twice - the very defect being fixed, introduced by fixing it.
    /// </summary>
    [Fact]
    public void ALegacyEntryFromABeforeTheFixBuild_IsReplacedRatherThanDuplicated()
    {
        var (scriptDir, hooksPath) = TempPaths();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
            File.WriteAllText(hooksPath, """
            {
              "hooks": {
                "SessionStart": [
                  { "matcher": "startup|resume|clear|compact",
                    "hooks": [ { "type": "command", "command": "powershell -NoProfile -File \"C:/old/report-preamble.ps1\"" } ] }
                ]
              }
            }
            """);

            Assert.True(CodexHookInstaller.EnsureInstalled(scriptDir, hooksPath, forWindows: true));

            using var doc = JsonDocument.Parse(File.ReadAllText(hooksPath));
            var entry = Assert.Single(
                doc.RootElement.GetProperty("hooks").GetProperty("SessionStart").EnumerateArray());
            var command = entry.GetProperty("hooks")[0].GetProperty("command").GetString()!;
            Assert.Contains("cc-director-preamble.ps1", command);
            Assert.DoesNotContain("C:/old", command);
        }
        finally { TryDeleteRoot(scriptDir); }
    }

    private static void TryDeleteRoot(string scriptDir)
    {
        try
        {
            var root = Directory.GetParent(scriptDir)?.FullName;
            if (root is not null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch { /* best effort */ }
    }
}
