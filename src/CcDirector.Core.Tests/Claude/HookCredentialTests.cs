using CcDirector.Core.Claude;
using CcDirector.Core.Codex;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// The session hooks must present the credential the Director injects.
///
/// This is guarded on its own because of HOW it fails. Both hook scripts swallow every error and
/// exit 0 by design - a hook must never take a session down - so a hook whose Authorization header
/// went missing would not raise anything anywhere. Claude's session pointer would silently stop
/// tracking across /clear and compaction, session history would quietly go empty, and every agent
/// would start with no fleet preamble and no idea it was missing one. Nothing in the product would
/// report it.
///
/// So the assertion is on the SCRIPT TEXT: the header has to be there, and it has to read the
/// environment variable the Director actually stamps.
/// </summary>
public sealed class HookCredentialTests
{
    private static string WriteAndRead(Func<string, string?> install, string scriptName, out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "cc-hook-cred-" + Guid.NewGuid().ToString("N"));
        install(dir);
        return File.ReadAllText(Path.Combine(dir, scriptName));
    }

    [Fact]
    public void The_windows_claude_hook_presents_the_session_credential()
    {
        var script = WriteAndRead(d => ClaudeHookInstaller.EnsureInstalled(d, forWindows: true),
            "report-session.ps1", out var dir);
        try
        {
            Assert.Contains("CC_DIRECTOR_TOKEN", script, StringComparison.Ordinal);
            Assert.Contains("Authorization", script, StringComparison.Ordinal);
            Assert.Contains("Bearer", script, StringComparison.Ordinal);

            // Both calls carry it, not just the one that happened to be edited first: the pointer
            // report and the preamble fetch are separate requests to separate authenticated routes.
            Assert.Contains("claude-hook", script, StringComparison.Ordinal);
            Assert.Contains("fleet-preamble", script, StringComparison.Ordinal);
            Assert.Equal(2, CountOccurrences(script, "-Headers $hdr"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    [Fact]
    public void The_posix_claude_hook_presents_the_session_credential()
    {
        var script = WriteAndRead(d => ClaudeHookInstaller.EnsureInstalled(d, forWindows: false),
            "report-session.sh", out var dir);
        try
        {
            Assert.Contains("CC_DIRECTOR_TOKEN", script, StringComparison.Ordinal);
            Assert.Contains("Authorization: Bearer", script, StringComparison.Ordinal);

            // Both curl calls have an authenticated form.
            Assert.Equal(2, CountOccurrences(script, "-H \"$auth\""));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    [Fact]
    public void The_codex_hook_presents_the_session_credential()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-hook-cred-codex-" + Guid.NewGuid().ToString("N"));
        try
        {
            CodexHookInstaller.EnsureInstalled(dir, Path.Combine(dir, "hooks.json"));
            var script = File.ReadAllText(Path.Combine(dir, "report-preamble.ps1"));

            Assert.Contains("CC_DIRECTOR_TOKEN", script, StringComparison.Ordinal);
            Assert.Contains("Authorization", script, StringComparison.Ordinal);
            Assert.Contains("Bearer", script, StringComparison.Ordinal);
            Assert.Contains("-Headers $hdr", script, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }
}
