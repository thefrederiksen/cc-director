using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Verifies Session.UpdateClaudeSessionPointer (the target of the session-pointer drop): it updates
/// the Claude session id and transcript path on a well-formed report, ignores blank values so a
/// partial hook payload never clears a good pointer, and REFUSES an id that is not a session id at
/// all.
///
/// The ids here are GUIDs because real ones are (for example
/// 57409e62-bd96-42f1-9fd8-77a758658d2c). These tests used to use convenient labels like
/// "claude-new", which read fine and quietly modelled something the product never produces - and
/// that is not a cosmetic point, because the shape is now the guard.
/// </summary>
public sealed class SessionClaudePointerTests
{
    private const string OriginalId = "11111111-1111-4111-8111-111111111111";
    private const string NewId = "22222222-2222-4222-8222-222222222222";

    private static Session NewSession(string? claudeSessionId = OriginalId) => new(
        Guid.NewGuid(),
        repoPath: @"C:\test\repo",
        workingDirectory: @"C:\test\repo",
        claudeArgs: null,
        backend: new NullBackend(),
        claudeSessionId: claudeSessionId,
        activityState: ActivityState.Working,
        createdAt: DateTimeOffset.UtcNow,
        customName: null,
        customColor: null);

    [Fact]
    public void UpdateClaudeSessionPointer_UpdatesOnNonBlank_IgnoresBlank()
    {
        using var s = NewSession();

        Assert.Equal(OriginalId, s.ClaudeSessionId);
        Assert.Null(s.ClaudeTranscriptPath);

        // A /clear hook reports the new id + transcript file.
        s.UpdateClaudeSessionPointer(NewId, @"C:\proj\new.jsonl", "clear");
        Assert.Equal(NewId, s.ClaudeSessionId);
        Assert.Equal(@"C:\proj\new.jsonl", s.ClaudeTranscriptPath);

        // A payload missing fields must not wipe the good pointer.
        s.UpdateClaudeSessionPointer(null, null, "startup");
        Assert.Equal(NewId, s.ClaudeSessionId);
        Assert.Equal(@"C:\proj\new.jsonl", s.ClaudeTranscriptPath);

        s.UpdateClaudeSessionPointer("   ", "   ", "resume");
        Assert.Equal(NewId, s.ClaudeSessionId);
        Assert.Equal(@"C:\proj\new.jsonl", s.ClaudeTranscriptPath);
    }

    // Issue #2456. A drop carrying the literal id "x" - no event, no source, no transcript - was
    // accepted over a verified GUID and persisted. The session could no longer resolve its transcript,
    // so narration found no reply, recorded "nothing to narrate" and returned without generating
    // anything: the rail sat on "Preparing voice" forever and the session never spoke again. Silent,
    // permanent, and it hit three sessions - one while running the v1.9.11 release gate.

    [Theory]
    [InlineData("x")]                                   // the exact value that did the damage
    [InlineData("claude-new")]                          // a plausible-looking label that is not an id
    [InlineData("57409e62-bd96-42f1-9fd8")]             // a TRUNCATED guid - the near miss that matters most
    [InlineData("not a guid at all")]
    [InlineData("../../other-tenant/voice-audio/x")]    // a path, not an id
    public void AMalformedId_IsRefused_AndTheGoodOneStands(string malformed)
    {
        using var s = NewSession();

        s.UpdateClaudeSessionPointer(malformed, @"C:\proj\new.jsonl", "SessionStart");

        Assert.Equal(OriginalId, s.ClaudeSessionId);
    }

    [Fact]
    public void AMalformedId_AlsoRefusesTheTranscriptPathThatCameWithIt()
    {
        // The whole drop is suspect, not just its id. Taking the path from a message we have already
        // judged malformed would half-apply it - and a transcript path pointing somewhere the id does
        // not agree with is its own silent wrong-transcript bug.
        using var s = NewSession();
        s.UpdateClaudeSessionPointer(NewId, @"C:\proj\good.jsonl", "clear");

        s.UpdateClaudeSessionPointer("x", @"C:\proj\attacker.jsonl", "SessionStart");

        Assert.Equal(NewId, s.ClaudeSessionId);
        Assert.Equal(@"C:\proj\good.jsonl", s.ClaudeTranscriptPath);
    }

    [Fact]
    public void AMalformedId_CannotEvenTakeHoldWhenThereIsNoPointerYet()
    {
        // The empty case is the one a "keep what you had" guard can accidentally leave open: with
        // nothing to protect, an accept costs nothing visible today and breaks narration later.
        using var s = NewSession(claudeSessionId: null);

        s.UpdateClaudeSessionPointer("x", null, "SessionStart");

        Assert.Null(s.ClaudeSessionId);
    }

    [Fact]
    public void AWellFormedIdStillReplacesAnEarlierOne()
    {
        // The guard must not be so tight that the real thing it exists to allow stops working: Claude
        // mints a NEW id on /clear and on compaction, and tracking it is the whole point of the drop.
        using var s = NewSession();

        s.UpdateClaudeSessionPointer(NewId, @"C:\proj\new.jsonl", "compact");

        Assert.Equal(NewId, s.ClaudeSessionId);
        Assert.Equal(@"C:\proj\new.jsonl", s.ClaudeTranscriptPath);
    }

    [Fact]
    public void TheRealWorldRepairValueIsAccepted()
    {
        // The exact id used to repair session 114d729f by hand on 2026-08-06, so the shape the guard
        // accepts is pinned to a value taken from production rather than one invented for the test.
        using var s = NewSession(claudeSessionId: null);

        s.UpdateClaudeSessionPointer("57409e62-bd96-42f1-9fd8-77a758658d2c", null, "resume");

        Assert.Equal("57409e62-bd96-42f1-9fd8-77a758658d2c", s.ClaudeSessionId);
    }

    private sealed class NullBackend : ISessionBackend
    {
        public CircularTerminalBuffer? Buffer => null;
        public int ProcessId => 1;
        public string Status => "Null";
        public bool IsRunning => true;
        public bool HasExited => false;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
