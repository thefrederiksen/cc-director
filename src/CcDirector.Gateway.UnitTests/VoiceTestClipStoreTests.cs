using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves the voice-test clip store keeps recorded speech inside the tenant that produced it, and
/// keeps it BOUNDED.
///
/// These are the two properties that decide whether this store may run on a hosted Gateway at all. The
/// older <see cref="TranscriptionAudioArchive"/> is switched off on hosted precisely because it has one
/// process-wide directory and a global prune, which would mix accounts' speech at rest and let a busy
/// tenant evict a quiet one's clips. This store exists to close that gap, so the gap is what these
/// tests aim at: a partition that is real, and a prune that cannot reach across it.
/// </summary>
public sealed class VoiceTestClipStoreTests : IDisposable
{
    private readonly string _root;

    public VoiceTestClipStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cc-voice-test-clips-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private VoiceTestClipStore StoreIn(string name) => new(Path.Combine(_root, name));

    private static VoiceTestClip Clip(string kind = VoiceTestKind.Transcription, string? language = "en",
        string? expected = "the passage", string? transcript = "the passage")
        => new()
        {
            ClipId = Guid.NewGuid().ToString("N"),
            Kind = kind,
            RecordedAtUtc = DateTime.UtcNow,
            Language = language,
            ExpectedText = expected,
            Transcript = transcript,
        };

    private static byte[] Audio(int bytes = 64) => Enumerable.Range(0, bytes).Select(i => (byte)i).ToArray();

    [Fact]
    public void TrySave_StoresTheAudioAndASidecarBesideIt()
    {
        var store = StoreIn("a");
        var clip = Clip();

        var id = store.TrySave(clip, Audio(), "audio/wav");

        Assert.Equal(clip.ClipId, id);
        Assert.True(File.Exists(Path.Combine(store.Directory, $"clip-{clip.ClipId}.wav")));
        Assert.True(File.Exists(Path.Combine(store.Directory, $"clip-{clip.ClipId}.json")));
    }

    [Fact]
    public void TrySave_KeepsThePassageAndTheTranscript_SoAScoreCanBeRecomputedLater()
    {
        // The sidecar deliberately holds the EVIDENCE, not a score. A future question - a different
        // scoring rule, a per-word breakdown - must still be answerable from what was stored.
        var store = StoreIn("a");
        var clip = Clip(language: "da", expected: "I gaar afsluttede jeg seks", transcript: "i gaar afsluttede jeg six");
        store.TrySave(clip, Audio(), "audio/wav");

        var stored = Assert.Single(store.List());
        Assert.Equal("da", stored.Language);
        Assert.Equal("I gaar afsluttede jeg seks", stored.ExpectedText);
        Assert.Equal("i gaar afsluttede jeg six", stored.Transcript);
    }

    [Fact]
    public void TrySave_RecordsTheMeasurementsVerbatim()
    {
        var store = StoreIn("a");
        var quality = JsonDocument.Parse("""{"narrowband":true,"signalToNoiseDb":41.5}""").RootElement.Clone();
        var clip = Clip(kind: VoiceTestKind.Microphone, expected: null, transcript: null) with { Quality = quality };

        store.TrySave(clip, Audio(), "audio/wav");

        var stored = Assert.Single(store.List());
        Assert.NotNull(stored.Quality);
        Assert.True(stored.Quality!.Value.GetProperty("narrowband").GetBoolean());
    }

    [Fact]
    public void OneTenantsClipsAreInvisibleToAnother()
    {
        // The property the hosted deployment turns on. Two stores, two partitions, no leakage.
        var alice = VoiceTestClipStore.ForTenant(new TenantId("alice"));
        var bob = VoiceTestClipStore.ForTenant(new TenantId("bob"));
        Assert.NotEqual(alice.Directory, bob.Directory);
    }

    [Fact]
    public void ATenantIdCannotEscapeItsOwnDirectory()
    {
        // A tenant id is attacker-influenced in principle, so path separators and traversal segments
        // must not survive into the path. Without this a tenant called "../other" would write into a
        // sibling's partition.
        var nasty = VoiceTestClipStore.ForTenant(new TenantId("../../etc/passwd"));
        var root = VoiceTestClipStore.DefaultDirectory();

        Assert.StartsWith(root, nasty.Directory, StringComparison.Ordinal);
        Assert.DoesNotContain("..", Path.GetFileName(nasty.Directory));
    }

    [Fact]
    public void PruneKeepsOnlyTheNewestClipsWithinOneTenant()
    {
        var store = StoreIn("a");
        for (var i = 0; i < VoiceTestClipStore.MaxClipsPerTenant + 12; i++)
            store.TrySave(Clip(), Audio(), "audio/wav");

        Assert.Equal(VoiceTestClipStore.MaxClipsPerTenant, store.List().Count);
    }

    [Fact]
    public void PruneDeletesTheAudioWithItsSidecar_SoAListingNeverPointsAtAMissingClip()
    {
        var store = StoreIn("a");
        for (var i = 0; i < VoiceTestClipStore.MaxClipsPerTenant + 5; i++)
            store.TrySave(Clip(), Audio(), "audio/wav");

        var sidecars = Directory.GetFiles(store.Directory, "clip-*.json").Length;
        var audio = Directory.GetFiles(store.Directory, "clip-*.wav").Length;
        Assert.Equal(sidecars, audio);
    }

    [Fact]
    public void PruneNeverReachesIntoAnotherTenantsPartition()
    {
        // The exact failure that disabled the older archive on hosted: one tenant's traffic evicting
        // another's clips. Fill one partition past its cap and prove the neighbour is untouched.
        var busy = StoreIn("busy");
        var quiet = StoreIn("quiet");
        quiet.TrySave(Clip(), Audio(), "audio/wav");

        for (var i = 0; i < VoiceTestClipStore.MaxClipsPerTenant + 20; i++)
            busy.TrySave(Clip(), Audio(), "audio/wav");

        Assert.Single(quiet.List());
        Assert.Equal(VoiceTestClipStore.MaxClipsPerTenant, busy.List().Count);
    }

    [Fact]
    public void PruneRemovesClipsOlderThanTheRetentionWindow()
    {
        var store = StoreIn("a");
        var old = Clip();
        store.TrySave(old, Audio(), "audio/wav");

        // Age the stored pair past the window; the prune keys on the file timestamp.
        var stale = DateTime.UtcNow - VoiceTestClipStore.MaxAge - TimeSpan.FromDays(1);
        foreach (var f in Directory.GetFiles(store.Directory, $"clip-{old.ClipId}.*"))
            File.SetLastWriteTimeUtc(f, stale);

        // Any save runs the prune.
        store.TrySave(Clip(), Audio(), "audio/wav");

        Assert.DoesNotContain(store.List(), c => c.ClipId == old.ClipId);
        Assert.False(File.Exists(Path.Combine(store.Directory, $"clip-{old.ClipId}.wav")));
    }

    [Fact]
    public void ListIsNewestFirst()
    {
        var store = StoreIn("a");
        var older = Clip() with { RecordedAtUtc = DateTime.UtcNow.AddMinutes(-10) };
        var newer = Clip() with { RecordedAtUtc = DateTime.UtcNow };
        store.TrySave(older, Audio(), "audio/wav");
        store.TrySave(newer, Audio(), "audio/wav");

        Assert.Equal(newer.ClipId, store.List()[0].ClipId);
    }

    [Fact]
    public void ListSurvivesAnUnreadableSidecar()
    {
        // One corrupt file must not hide every other clip from an analysis run.
        var store = StoreIn("a");
        store.TrySave(Clip(), Audio(), "audio/wav");
        File.WriteAllText(Path.Combine(store.Directory, "clip-corrupt.json"), "{ not json", Encoding.UTF8);

        Assert.Single(store.List());
    }

    [Fact]
    public void ListIsEmptyBeforeAnythingIsStored()
    {
        Assert.Empty(StoreIn("never-used").List());
    }

    [Fact]
    public void ClearRemovesEveryClipForThisTenantOnly()
    {
        var mine = StoreIn("mine");
        var theirs = StoreIn("theirs");
        mine.TrySave(Clip(), Audio(), "audio/wav");
        mine.TrySave(Clip(), Audio(), "audio/wav");
        theirs.TrySave(Clip(), Audio(), "audio/wav");

        var removed = mine.Clear();

        Assert.Equal(2, removed);
        Assert.Empty(mine.List());
        Assert.Single(theirs.List());
    }

    [Fact]
    public void TrySave_ReportsRatherThanThrows_WhenThereIsNoAudio()
    {
        // Storing a diagnostic must never fail the check the user is running.
        Assert.Null(StoreIn("a").TrySave(Clip(), Array.Empty<byte>(), "audio/wav"));
    }

    [Fact]
    public void TrySave_DerivesTheAudioExtensionFromTheContentType()
    {
        var store = StoreIn("a");
        var clip = Clip();
        store.TrySave(clip, Audio(), "audio/webm;codecs=opus");

        // The MIME parameter must not leak into the filename, or the clip will not open in a player.
        Assert.True(File.Exists(Path.Combine(store.Directory, $"clip-{clip.ClipId}.webm")));
    }
}
