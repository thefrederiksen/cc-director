using CcDirector.Core.Dictation;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// The durable store is the whole guarantee behind issue #1130: audio written before any network call,
/// deleted only when delivered, and readable back on the next launch. These tests pin that contract
/// against a temp directory - no application state, no network.
/// </summary>
public sealed class PendingDictationStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly PendingDictationStore _store;
    private static readonly byte[] SampleWav = { 1, 2, 3, 4, 5, 6, 7, 8 };

    public PendingDictationStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pending-dictation-tests-" + Guid.NewGuid().ToString("N"));
        _store = new PendingDictationStore(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort temp cleanup */ }
    }

    [Fact]
    public void Save_WritesAudioAndSidecar_AndReturnsPendingRecord()
    {
        var rec = _store.Save("session-1", "prefix words", SampleWav);

        Assert.False(string.IsNullOrWhiteSpace(rec.Id));
        Assert.Equal("session-1", rec.SessionId);
        Assert.Equal("prefix words", rec.Prefix);
        Assert.Equal(0, rec.AttemptCount);
        Assert.Equal(PendingDictationStatus.Pending, rec.Status);
        Assert.True(_store.HasAudio(rec));
        Assert.Equal(SampleWav, _store.ReadAudio(rec));
    }

    [Fact]
    public void Save_EmptyAudio_Throws()
    {
        Assert.Throws<ArgumentException>(() => _store.Save("session-1", "", Array.Empty<byte>()));
    }

    [Fact]
    public void ReadAudio_ReturnsTheExactBytesSaved_AcrossANewStoreInstance()
    {
        var rec = _store.Save("session-1", "", SampleWav);

        // A brand-new store over the same directory models the next launch reading it back.
        var reopened = new PendingDictationStore(_dir);
        var loaded = reopened.LoadAll();

        Assert.Single(loaded);
        Assert.Equal(rec.Id, loaded[0].Id);
        Assert.Equal(SampleWav, reopened.ReadAudio(loaded[0]));
    }

    [Fact]
    public void Delete_RemovesBothAudioAndSidecar()
    {
        var rec = _store.Save("session-1", "", SampleWav);
        _store.Delete(rec);

        Assert.False(_store.HasAudio(rec));
        Assert.Empty(_store.LoadAll());
    }

    [Fact]
    public void RecordFailedAttempt_BumpsCountAndSetsStatusAndError_KeepingAudio()
    {
        var rec = _store.Save("session-1", "", SampleWav);

        var after = _store.RecordFailedAttempt(rec, "Transcription returned 504", PendingDictationStatus.Pending);

        Assert.Equal(1, after.AttemptCount);
        Assert.Equal(PendingDictationStatus.Pending, after.Status);
        Assert.Contains("504", after.LastError);
        Assert.True(_store.HasAudio(after)); // audio is never touched by a failed attempt

        // Persisted: a reload sees the bumped count and status.
        var reloaded = _store.LoadAll();
        Assert.Single(reloaded);
        Assert.Equal(1, reloaded[0].AttemptCount);
    }

    [Fact]
    public void RecordFailedAttempt_CanParkNeedsAttention()
    {
        var rec = _store.Save("session-1", "", SampleWav);
        var after = _store.RecordFailedAttempt(rec, "insufficient_credits", PendingDictationStatus.NeedsAttention);
        Assert.Equal(PendingDictationStatus.NeedsAttention, after.Status);
    }

    [Fact]
    public void LoadAll_SkipsAndCleansAnOrphanSidecarWithNoAudio()
    {
        var rec = _store.Save("session-1", "", SampleWav);
        // Simulate a torn write / manual audio removal: delete just the audio, leave the sidecar.
        File.Delete(Path.Combine(_dir, rec.Id + ".wav"));

        var loaded = _store.LoadAll();

        Assert.Empty(loaded);
        // The orphan sidecar is cleaned up so it does not linger.
        Assert.False(File.Exists(Path.Combine(_dir, rec.Id + ".json")));
    }

    [Fact]
    public void LoadAll_SkipsACorruptSidecar_WithoutFailingTheWholeScan()
    {
        var good = _store.Save("session-good", "", SampleWav);
        // A corrupt sidecar alongside its (irrelevant) audio must not block the good record.
        File.WriteAllText(Path.Combine(_dir, "garbage.json"), "{ this is not valid json ");
        File.WriteAllBytes(Path.Combine(_dir, "garbage.wav"), SampleWav);

        var loaded = _store.LoadAll();

        Assert.Single(loaded);
        Assert.Equal(good.Id, loaded[0].Id);
    }

    [Fact]
    public void LoadAll_ReturnsOldestFirst()
    {
        var a = _store.Save("s", "", SampleWav);
        var b = _store.Save("s", "", SampleWav);
        // Force a known chronological order regardless of clock resolution.
        _store.WriteSidecar(a with { CreatedUtc = "2020-01-01T00:00:00.0000000Z" });
        _store.WriteSidecar(b with { CreatedUtc = "2020-01-02T00:00:00.0000000Z" });

        var loaded = _store.LoadAll();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(a.Id, loaded[0].Id);
        Assert.Equal(b.Id, loaded[1].Id);
    }

    [Fact]
    public void PruneOlderThan_DropsStaleClips_KeepsFreshOnes()
    {
        var stale = _store.Save("s", "", SampleWav);
        var fresh = _store.Save("s", "", SampleWav);
        _store.WriteSidecar(stale with { CreatedUtc = DateTime.UtcNow.AddDays(-30).ToString("o") });
        _store.WriteSidecar(fresh with { CreatedUtc = DateTime.UtcNow.ToString("o") });

        var pruned = _store.PruneOlderThan(TimeSpan.FromDays(7));

        Assert.Equal(1, pruned);
        var loaded = _store.LoadAll();
        Assert.Single(loaded);
        Assert.Equal(fresh.Id, loaded[0].Id);
    }

    [Fact]
    public void LoadAll_OnMissingDirectory_ReturnsEmpty()
    {
        var store = new PendingDictationStore(Path.Combine(_dir, "does-not-exist-yet"));
        Assert.Empty(store.LoadAll());
    }
}
