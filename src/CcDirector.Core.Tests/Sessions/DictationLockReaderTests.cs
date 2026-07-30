using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// Issue #1181, Task 3b: the Director-side projection of the enforced dictation lock
/// (<see cref="DictationLockReader"/>). A session is locked exactly while some upload folder under the
/// shared dictation-uploads root holds a <c>record.json</c> in state <c>Pending</c> naming it. These
/// tests write the marker files by hand (the exact on-disk shape the Gateway writes) and assert the read.
/// A companion Gateway test cross-checks the reader against the real writer.
/// </summary>
public sealed class DictationLockReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-dictlock-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private void WriteMarker(string uploadId, string state, string sessionId)
    {
        var dir = Path.Combine(_root, uploadId);
        Directory.CreateDirectory(dir);
        // The exact fields the Gateway's DictationDeliveryRecord serializes (State is a string enum).
        File.WriteAllText(Path.Combine(dir, "record.json"),
            $"{{\"State\":\"{state}\",\"Submitted\":false,\"MovedOn\":false,\"Transcript\":\"\",\"Reason\":null,\"SessionId\":\"{sessionId}\"}}");
    }

    [Fact]
    public void IsSessionLocked_PendingMarkerNamingTheSession_Locks()
    {
        var sid = Guid.NewGuid().ToString();
        WriteMarker(Guid.NewGuid().ToString("N"), "Pending", sid);

        Assert.True(DictationLockReader.IsSessionLocked(_root, sid));
    }

    [Theory]
    [InlineData("Delivered")]
    [InlineData("Abandoned")]
    [InlineData("Failed")]
    public void IsSessionLocked_NonPendingMarker_DoesNotLock(string terminalState)
    {
        var sid = Guid.NewGuid().ToString();
        WriteMarker(Guid.NewGuid().ToString("N"), terminalState, sid);

        Assert.False(DictationLockReader.IsSessionLocked(_root, sid));
    }

    [Fact]
    public void IsSessionLocked_PendingForAnotherSession_DoesNotLockThisOne()
    {
        WriteMarker(Guid.NewGuid().ToString("N"), "Pending", Guid.NewGuid().ToString());

        Assert.False(DictationLockReader.IsSessionLocked(_root, Guid.NewGuid().ToString()));
    }

    [Fact]
    public void IsSessionLocked_CaseInsensitiveOnStateAndSessionId()
    {
        var sid = Guid.NewGuid().ToString().ToUpperInvariant();
        WriteMarker(Guid.NewGuid().ToString("N"), "pending", sid);

        Assert.True(DictationLockReader.IsSessionLocked(_root, sid.ToLowerInvariant()));
    }

    [Fact]
    public void IsSessionLocked_OnePendingAmongTerminals_StillLocks()
    {
        var sid = Guid.NewGuid().ToString();
        WriteMarker(Guid.NewGuid().ToString("N"), "Delivered", sid);
        WriteMarker(Guid.NewGuid().ToString("N"), "Abandoned", sid);
        WriteMarker(Guid.NewGuid().ToString("N"), "Pending", sid);

        Assert.True(DictationLockReader.IsSessionLocked(_root, sid));
    }

    [Fact]
    public void IsSessionLocked_MissingRoot_IsFalse()
    {
        Assert.False(DictationLockReader.IsSessionLocked(Path.Combine(_root, "does-not-exist"), Guid.NewGuid().ToString()));
    }

    [Fact]
    public void IsSessionLocked_GarbageOrEmptyMarker_IsIgnored()
    {
        var sid = Guid.NewGuid().ToString();
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "record.json"), "{ not valid json");
        var empty = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, "record.json"), "");

        // A half-written or corrupt marker must never wedge a session locked - it is simply skipped.
        Assert.False(DictationLockReader.IsSessionLocked(_root, sid));
    }

    [Fact]
    public void IsSessionLocked_BlankSessionId_IsFalse()
    {
        WriteMarker(Guid.NewGuid().ToString("N"), "Pending", Guid.NewGuid().ToString());
        Assert.False(DictationLockReader.IsSessionLocked(_root, ""));
    }

    // ---- LockedSessionIds: the bulk read behind the roster's per-tick refresh (issue #1111) ----
    //
    // The bulk read exists ONLY to stop the roster re-reading the whole marker store once per session per
    // second. It is therefore worth nothing unless it gives the SAME answer as the single-session read it
    // replaces at the call site - so the tests below mirror the cases above, and the last one asserts the
    // agreement directly rather than trusting that the two implementations were kept in step by hand.

    [Fact]
    public void LockedSessionIds_ReturnsEveryPendingSession_InOnePass()
    {
        var a = Guid.NewGuid().ToString();
        var b = Guid.NewGuid().ToString();
        WriteMarker(Guid.NewGuid().ToString("N"), "Pending", a);
        WriteMarker(Guid.NewGuid().ToString("N"), "Pending", b);

        var locked = DictationLockReader.LockedSessionIds(_root);

        Assert.Equal(2, locked.Count);
        Assert.Contains(a, locked);
        Assert.Contains(b, locked);
    }

    [Theory]
    [InlineData("Delivered")]
    [InlineData("Abandoned")]
    [InlineData("Failed")]
    public void LockedSessionIds_NonPendingMarker_ContributesNoLock(string terminalState)
    {
        // The live failure this fixes: a store full of terminal tombstones nobody purges. They must cost a
        // read and contribute nothing, never a lock.
        WriteMarker(Guid.NewGuid().ToString("N"), terminalState, Guid.NewGuid().ToString());

        Assert.Empty(DictationLockReader.LockedSessionIds(_root));
    }

    [Fact]
    public void LockedSessionIds_IsCaseInsensitive_LikeTheSingleSessionRead()
    {
        var sid = Guid.NewGuid().ToString().ToUpperInvariant();
        WriteMarker(Guid.NewGuid().ToString("N"), "pending", sid);

        Assert.Contains(sid.ToLowerInvariant(), DictationLockReader.LockedSessionIds(_root));
    }

    [Fact]
    public void LockedSessionIds_MissingRootOrGarbageMarker_FailsOpen()
    {
        Assert.Empty(DictationLockReader.LockedSessionIds(Path.Combine(_root, "does-not-exist")));

        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "record.json"), "{ not valid json");

        // Same posture as IsSessionLocked: a corrupt marker is skipped, never a phantom lock.
        Assert.Empty(DictationLockReader.LockedSessionIds(_root));
    }

    [Fact]
    public void LockedSessionIds_AgreesWithIsSessionLocked_ForEverySession()
    {
        // A store shaped like a real one: one live dictation among a pile of terminal tombstones, plus a
        // corrupt marker and a session that has never dictated. Whatever the answer is per session, the two
        // reads must give it identically - that agreement is the whole licence to swap one for the other.
        var pending = Guid.NewGuid().ToString();
        var delivered = Guid.NewGuid().ToString();
        var neverSeen = Guid.NewGuid().ToString();

        WriteMarker(Guid.NewGuid().ToString("N"), "Pending", pending);
        WriteMarker(Guid.NewGuid().ToString("N"), "Delivered", delivered);
        WriteMarker(Guid.NewGuid().ToString("N"), "Abandoned", delivered);
        var corrupt = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(corrupt);
        File.WriteAllText(Path.Combine(corrupt, "record.json"), "{ half written");

        var locked = DictationLockReader.LockedSessionIds(_root);

        foreach (var sid in new[] { pending, delivered, neverSeen })
        {
            Assert.Equal(DictationLockReader.IsSessionLocked(_root, sid), locked.Contains(sid));
        }
    }
}
