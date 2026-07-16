using CcDirector.Core.Account;
using CcDirector.Core.Pi;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Pi;

public class PiPreambleWriterTests
{
    // These tests assert the content of the DEVTHROTTLE default, so they pin the store to ours. Once
    // the writer started honouring the user's choice, a bare call would read the real config.json and
    // these would pass or fail depending on whether the developer running them happens to be running
    // their own injected text. A test whose result depends on the tester is not a test.
    private static InjectedTextStore OursStore(string dir) => InjectedTextStore.AlwaysOurs(dir);

    // The user's text is chosen, but its cached copy is absent (a broken/partial cache). ActiveTemplate
    // throws; the writer must swallow that and inject NOTHING, never ours.
    private static InjectedTextStore TheirsStore(string dir) => SeedStore(dir, useYours: true, yours: null);

    private static InjectedTextStore SeedStore(string dir, bool useYours, string? yours)
    {
        var store = new InjectedTextStore(Path.Combine(dir, "injected-text-cache.json"));
        store.WriteCache(new InjectedTextCacheEntry(useYours, yours, DateTime.UtcNow));
        return store;
    }

    // Issue #1357: when a signed-in user is supplied, the Pi preamble file names that user.
    [Fact]
    public void WriteForSession_WithSignedInUser_WritesIdentityLine()
    {
        var dir = NewDir();
        try
        {
            var sid = "abc12345-1111-2222-3333-444455556666";
            var path = PiPreambleWriter.WriteForSession(
                sid, "myrepo", "MACHINE_A", @"D:\repo\myrepo", dir,
                new SignedInUser("soren@example.com", "Starlord"), OursStore(dir));

            var text = File.ReadAllText(path);
            Assert.Contains("The user of this session is Starlord (soren@example.com).", text);
            Assert.Contains("do not guess identity from usage or the database", text);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void WriteForSession_WritesPreambleFile_WithIdentityAndCommands()
    {
        var dir = NewDir();
        try
        {
            var sid = "abc12345-1111-2222-3333-444455556666";
            var path = PiPreambleWriter.WriteForSession(
                sid, "myrepo", "MACHINE_A", @"D:\repo\myrepo", dir, user: null, store: OursStore(dir));

            Assert.True(File.Exists(path));
            Assert.Equal(Path.Combine(dir, sid + ".txt"), path);

            var text = File.ReadAllText(path);
            Assert.Contains("abc12345", text);   // short id present
            Assert.Contains("myrepo", text);      // name present
            Assert.Contains("cc-devthrottle", text);
            Assert.Contains("session list", text);
            Assert.Contains("message send", text);
            Assert.Contains("message ask", text);
            Assert.DoesNotContain("cc-rename", text);
            Assert.DoesNotContain("cc-sessions", text);
            Assert.DoesNotContain("cc-send", text);
            Assert.DoesNotContain("cc-ask", text);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void WriteForSession_UnnamedSession_StillWritesUsableFile()
    {
        var dir = NewDir();
        try
        {
            var path = PiPreambleWriter.WriteForSession(
                "603b2066-aaaa-bbbb-cccc-ddddeeeeffff", null, "MACHINE_A", @"D:\repo", dir,
                user: null, store: OursStore(dir));

            var text = File.ReadAllText(path);
            Assert.Contains("(unnamed)", text);
            Assert.Contains("603b2066", text);
        }
        finally { Cleanup(dir); }
    }

    // The behaviour the documentation promises, and the behaviour Pi did NOT have: when the user's text
    // is chosen but unreadable, Pi launches with NOTHING injected. It must not abort the launch - the
    // exception used to escape and take the session start down with it - and it must certainly not fall
    // back to the DevThrottle text the user turned off.
    [Fact]
    public void WriteForSession_TheirTextChosenButMissing_WritesAnEmptyFile_AndNeverOurs()
    {
        var dir = NewDir();
        try
        {
            var path = PiPreambleWriter.WriteForSession(
                "abc12345-1111-2222-3333-444455556666", "myrepo", "MACHINE_A", @"D:\repo\myrepo",
                dir, user: null, store: TheirsStore(dir));

            // The file exists - Pi is launched with --append-system-prompt pointing at it - and is
            // empty, so nothing is injected.
            Assert.True(File.Exists(path));
            var text = File.ReadAllText(path);
            Assert.Equal("", text);

            // The thing that must never happen: our text, and our policy, reaching someone who declined it.
            Assert.DoesNotContain("NEVER SIGN IT", text);
            Assert.DoesNotContain("cc-devthrottle", text);
        }
        finally { Cleanup(dir); }
    }

    // Whitespace means nothing, and it must mean nothing HERE too - the same answer the hook endpoints
    // give. Pi used to write the spaces while the hook path dropped them, so two agents disagreed about
    // the same saved text.
    [Fact]
    public void WriteForSession_TheirTextIsWhitespace_WritesAnEmptyFile()
    {
        var dir = NewDir();
        try
        {
            var store = SeedStore(dir, useYours: true, yours: "   \n  \n");

            var path = PiPreambleWriter.WriteForSession(
                "abc12345-1111-2222-3333-444455556666", "myrepo", "MACHINE_A", @"D:\repo\myrepo",
                dir, user: null, store: store);

            Assert.Equal("", File.ReadAllText(path));
        }
        finally { Cleanup(dir); }
    }

    // The user's own text reaches Pi intact when it is readable.
    [Fact]
    public void WriteForSession_TheirText_IsWhatPiGets()
    {
        var dir = NewDir();
        try
        {
            var store = SeedStore(dir, useYours: true, yours: "only my words. you are [SESSION_SHORT_ID].");

            var path = PiPreambleWriter.WriteForSession(
                "abc12345-1111-2222-3333-444455556666", "myrepo", "MACHINE_A", @"D:\repo\myrepo",
                dir, user: null, store: store);

            var text = File.ReadAllText(path);
            Assert.Equal("only my words. you are abc12345.", text);
            Assert.DoesNotContain("NEVER SIGN IT", text);
        }
        finally { Cleanup(dir); }
    }

    private static string NewDir()
        => Path.Combine(Path.GetTempPath(), "pi-preamble-test-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
