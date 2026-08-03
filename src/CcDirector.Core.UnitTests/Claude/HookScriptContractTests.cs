using CcDirector.Core.Claude;
using CcDirector.Core.Codex;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// The session hooks must use the two FILES the Director stamps, and must not reach for the network.
///
/// This is guarded on its own because of HOW it fails. All three hook scripts swallow every error and
/// exit 0 by design - a hook must never take a session down - so a hook wired to the wrong variable, or
/// still holding an HTTP call after the route behind it was deleted, raises nothing anywhere. Claude's
/// session pointer would silently stop tracking across /clear and compaction, session history would
/// quietly go empty, and every agent would start with no fleet preamble and no idea it was missing one.
/// Nothing in the product would report it.
///
/// It REPLACES the credential tests that stood here, and the replacement is deliberate rather than a
/// deletion to get green: those tests asserted that each script presents an Authorization header, which
/// was the right assertion while the hooks called authenticated routes. The
/// remove-the-network-port mission's phase 3 deleted those routes; a hook that still presented a
/// credential would be evidence the change had not happened. So the assertion inverts - no credential,
/// no address, no HTTP - and the reason it is asserted at all is unchanged.
/// </summary>
public sealed class HookScriptContractTests
{
    private static string Read(Func<string, string?> install, string scriptName, out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "cc-hook-contract-" + Guid.NewGuid().ToString("N"));
        install(dir);
        return File.ReadAllText(Path.Combine(dir, scriptName));
    }

    private static string ClaudeWindows(out string dir)
        => Read(d => ClaudeHookInstaller.EnsureInstalled(d, forWindows: true), "report-session.ps1", out dir);

    private static string ClaudePosix(out string dir)
        => Read(d => ClaudeHookInstaller.EnsureInstalled(d, forWindows: false), "report-session.sh", out dir);

    private static string Codex(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "cc-hook-contract-codex-" + Guid.NewGuid().ToString("N"));
        CodexHookInstaller.EnsureInstalled(dir, Path.Combine(dir, "hooks.json"));
        return File.ReadAllText(Path.Combine(dir, "report-preamble.ps1"));
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    // ---------- The deletion proof: no script talks to the Director over HTTP any more ----------

    /// <summary>
    /// Every token that could only be there to make a network call. This is the assertion that fails if
    /// anybody re-adds a route and wires a hook back to it - the change this phase exists to make would
    /// be undone in the script, not in the endpoint file, and this is where that shows up.
    /// </summary>
    [Theory]
    [InlineData("CC_DIRECTOR_API")]
    [InlineData("CC_DIRECTOR_TOKEN")]
    [InlineData("Authorization")]
    [InlineData("Bearer")]
    [InlineData("Invoke-RestMethod")]
    [InlineData("curl")]
    [InlineData("http://")]
    [InlineData("fleet-preamble")]
    [InlineData("claude-hook")]
    public void No_hook_script_holds_anything_that_could_call_the_Director(string forbidden)
    {
        var windows = ClaudeWindows(out var d1);
        var posix = ClaudePosix(out var d2);
        var codex = Codex(out var d3);
        try
        {
            Assert.DoesNotContain(forbidden, windows, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, posix, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, codex, StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(d1); Cleanup(d2); Cleanup(d3); }
    }

    // ---------- The positive contract: each script uses the variables the Director stamps ----------

    [Fact]
    public void The_windows_claude_hook_drops_the_pointer_and_prints_the_preamble_file()
    {
        var script = ClaudeWindows(out var dir);
        try
        {
            Assert.Contains(SessionHookFiles.PointerFileEnvVar, script, StringComparison.Ordinal);
            Assert.Contains(SessionHookFiles.PreambleFileEnvVar, script, StringComparison.Ordinal);

            // The event is written verbatim from stdin - nothing here re-shapes Claude's JSON.
            Assert.Contains("[Console]::In.ReadToEnd()", script, StringComparison.Ordinal);
            // Temporary file then move, so the Director never reads half an event.
            Assert.Contains(".tmp", script, StringComparison.Ordinal);
            Assert.Contains("Move-Item", script, StringComparison.Ordinal);
            // The preamble goes to stdout as-is: the file already holds the finished envelope.
            Assert.Contains("[Console]::Out.Write($out)", script, StringComparison.Ordinal);
            Assert.Contains("exit 0", script, StringComparison.Ordinal);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// The 3-argument <c>File.Move</c> overload does not exist on .NET Framework, which is what
    /// <c>powershell.exe</c> (Windows PowerShell 5.1) runs on - and the script swallows the resulting
    /// error and exits 0, so using it would silently kill transcript tracking on every Windows install.
    /// </summary>
    [Fact]
    public void The_windows_hooks_use_no_dotnet_core_only_api()
    {
        var claude = ClaudeWindows(out var d1);
        var codex = Codex(out var d2);
        try
        {
            Assert.DoesNotContain("File]::Move(", claude, StringComparison.Ordinal);
            Assert.DoesNotContain("File]::Move(", codex, StringComparison.Ordinal);
        }
        finally { Cleanup(d1); Cleanup(d2); }
    }

    [Fact]
    public void The_posix_claude_hook_drops_the_pointer_and_prints_the_preamble_file()
    {
        var script = ClaudePosix(out var dir);
        try
        {
            Assert.Contains(SessionHookFiles.PointerFileEnvVar, script, StringComparison.Ordinal);
            Assert.Contains(SessionHookFiles.PreambleFileEnvVar, script, StringComparison.Ordinal);
            Assert.Contains("mv -f", script, StringComparison.Ordinal);
            // -s, not -e: print only when there is something to print.
            Assert.Contains("-s \"$CC_SESSION_PREAMBLE_FILE\"", script, StringComparison.Ordinal);
            Assert.Contains("exit 0", script, StringComparison.Ordinal);
            Assert.DoesNotContain("powershell", script, StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// Codex has no pointer to report - it is Claude alone that mints a new session id and transcript on
    /// /clear and compaction - so its hook must read the preamble file and nothing else. In particular it
    /// must not read stdin: Codex 0.142 and later can fail interactive startup if a hook command consumes
    /// or probes the terminal stdin.
    /// </summary>
    [Fact]
    public void The_codex_hook_prints_the_preamble_file_and_does_not_touch_stdin_or_the_pointer()
    {
        var script = Codex(out var dir);
        try
        {
            Assert.Contains(SessionHookFiles.PreambleFileEnvVar, script, StringComparison.Ordinal);
            Assert.DoesNotContain(SessionHookFiles.PointerFileEnvVar, script, StringComparison.Ordinal);
            Assert.DoesNotContain("ReadToEnd", script, StringComparison.Ordinal);
            Assert.Contains("exit 0", script, StringComparison.Ordinal);
        }
        finally { Cleanup(dir); }
    }

    /// <summary>
    /// Both Claude scripts and the Codex script deliver the preamble the SAME way now - by printing one
    /// file the Director wrote. Before this phase the Windows script built the JSON envelope itself while
    /// the POSIX one fetched a pre-built envelope from a second route, and that difference is exactly how
    /// the macOS hook came to omit the signed-in user for as long as it did (issue #1357). Pinned so the
    /// two cannot drift apart again.
    /// </summary>
    [Fact]
    public void No_script_builds_the_hook_envelope_itself()
    {
        var windows = ClaudeWindows(out var d1);
        var posix = ClaudePosix(out var d2);
        var codex = Codex(out var d3);
        try
        {
            foreach (var script in new[] { windows, posix, codex })
            {
                Assert.DoesNotContain("hookSpecificOutput", script, StringComparison.Ordinal);
                Assert.DoesNotContain("additionalContext", script, StringComparison.Ordinal);
                Assert.DoesNotContain("ConvertTo-Json", script, StringComparison.Ordinal);
                Assert.DoesNotContain("ConvertFrom-Json", script, StringComparison.Ordinal);
            }
        }
        finally { Cleanup(d1); Cleanup(d2); Cleanup(d3); }
    }
}
