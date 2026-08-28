using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Voice;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A PENDING dictation that no client is coming back for must stop holding its session locked.
///
/// PENDING deliberately never auto-releases for a LIVE dictation (issue #1188) and these tests do not
/// weaken that - the fresh-record and activity cases below are what pin it. What they add is a bound on a
/// DEAD one: the record clears only when the client delivers or abandons, so a client that simply never
/// returns left the marker on disk forever. Seven were found stuck on the hosted Gateway on 2026-08-28, the
/// oldest five weeks old, each still refusing human input on a session nobody could unlock.
/// </summary>
public sealed class ExpireStalePendingDictationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-stalepending-" + Guid.NewGuid().ToString("N"));
    private readonly VoiceUploadStore _store;

    public ExpireStalePendingDictationTests() => _store = new VoiceUploadStore(_root, TenantId.Local);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { /* test cleanup */ }
    }

    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    private void Age(string uploadId, TimeSpan by)
        => Directory.SetLastWriteTimeUtc(Path.Combine(_root, uploadId), DateTime.UtcNow - by);

    [Fact]
    public void AStalePendingRecord_IsAbandoned_AndItsSessionUnlocks()
    {
        var id = _store.Register(null);
        _store.MarkPending(id, "session-a");
        Age(id, TimeSpan.FromHours(30));

        Assert.True(_store.IsSessionLocked("session-a"));

        Assert.Equal(1, _store.ExpireStalePending(Day));

        Assert.False(_store.IsSessionLocked("session-a"));
    }

    /// <summary>
    /// It is an ABANDON, not a delete: the tombstone survives (so a late client cannot re-drive the upload
    /// id) and it says the SERVER timed this out rather than the user giving up on it.
    /// </summary>
    [Fact]
    public void TheExpiredRecordLeavesATombstoneSayingWhy()
    {
        var id = _store.Register(null);
        _store.MarkPending(id, "session-a");
        Age(id, TimeSpan.FromDays(40));

        _store.ExpireStalePending(Day);

        var json = File.ReadAllText(Path.Combine(_root, id, "record.json"));
        Assert.Contains("Abandoned", json);
        Assert.Contains(VoiceUploadStore.StalePendingReason, json);
        Assert.DoesNotContain("user_abandoned", json);
    }

    [Fact]
    public void AFreshPendingRecord_IsLeftAlone()
    {
        var id = _store.Register(null);
        _store.MarkPending(id, "session-a");

        Assert.Equal(0, _store.ExpireStalePending(Day));

        Assert.True(_store.IsSessionLocked("session-a"));
        Assert.True(_store.IsPending(id));
    }

    /// <summary>
    /// THE ONE THAT PROTECTS A LIVE USER. Age is measured from last ACTIVITY, and every register, resume,
    /// chunk and assemble refreshes it - so a client still working, however slowly, pushes its own deadline
    /// out and is never expired mid-dictation. Only silence ages.
    /// </summary>
    [Fact]
    public void ActivityOnAnOldUpload_PushesTheDeadlineOut()
    {
        var id = _store.Register(null);
        _store.MarkPending(id, "session-a");
        Age(id, TimeSpan.FromDays(9));

        // The client comes back and resumes: re-registering the same id is the resume path, and it stamps
        // the same last-activity signal the sweep judges by.
        _store.Register(id);

        Assert.Equal(0, _store.ExpireStalePending(Day));
        Assert.True(_store.IsSessionLocked("session-a"));
    }

    /// <summary>
    /// FAILED is a user-retryable pause that holds NO session lock, so ageing it out would cancel a retry
    /// the user was offered rather than release a lock nobody can clear. Left alone on purpose.
    /// </summary>
    [Fact]
    public void AFailedRecord_IsNotExpired()
    {
        var id = _store.Register(null);
        _store.MarkPending(id, "session-a");
        _store.MarkFailed(id, "out_of_credits");
        Age(id, TimeSpan.FromDays(40));

        Assert.Equal(0, _store.ExpireStalePending(Day));

        var json = File.ReadAllText(Path.Combine(_root, id, "record.json"));
        Assert.Contains("Failed", json);
        Assert.True(_store.ClearFailed(id));   // still retryable
    }

    [Theory]
    [InlineData("delivered")]
    [InlineData("abandoned")]
    public void AlreadyTerminalRecords_AreNotTouched(string state)
    {
        var id = _store.Register(null);
        _store.MarkPending(id, "session-a");
        if (state == "delivered") _store.MarkDelivered(id, submitted: true, movedOn: false, "words");
        else _store.MarkAbandoned(id, "user_abandoned");
        Age(id, TimeSpan.FromDays(40));

        Assert.Equal(0, _store.ExpireStalePending(Day));

        var json = File.ReadAllText(Path.Combine(_root, id, "record.json"));
        Assert.DoesNotContain(VoiceUploadStore.StalePendingReason, json);
    }

    /// <summary>
    /// The expiry runs inside ONE tenant's partition. Another tenant's stuck record is not this pass's to
    /// resolve - the directory partition is the boundary, and a sweep that reached across it would be the
    /// one place tenant isolation is enforced by a predicate someone could forget.
    /// </summary>
    [Fact]
    public void ItDoesNotReachIntoAnotherTenantsPartition()
    {
        var a = _store.ForTenant(new TenantId("11111111-1111-1111-1111-111111111111"));
        var b = _store.ForTenant(new TenantId("22222222-2222-2222-2222-222222222222"));

        var idB = b.Register(null);
        b.MarkPending(idB, "session-b");
        Directory.SetLastWriteTimeUtc(Path.Combine(b.Root, idB), DateTime.UtcNow.AddDays(-40));

        Assert.Equal(0, a.ExpireStalePending(Day));

        Assert.True(b.IsSessionLocked("session-b"));
    }

    /// <summary>
    /// Expiring discards the staged audio, exactly as a user-driven abandon does - the words are gone, so
    /// keeping the bytes would be recorded speech with no purpose and no retention bound.
    /// </summary>
    [Fact]
    public async Task ExpiringDiscardsTheStagedAudio()
    {
        var id = _store.Register(null);
        _store.MarkPending(id, "session-a");
        await _store.StoreChunkAsync(id, 0, new byte[] { 1, 2, 3, 4 }, null);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_root, id), "*.part"));

        Age(id, TimeSpan.FromDays(40));
        _store.ExpireStalePending(Day);

        Assert.Empty(Directory.GetFiles(Path.Combine(_root, id), "*.part"));
    }
}
