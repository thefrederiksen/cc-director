using CcDirector.Core.Dictation;
using CcDirector.Core.Transcription;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// The sweeper is what recovers a held clip without a restart (issue #1130): it re-drives every Pending
/// clip whose session is present, leaves the rest, prunes stale ones, and never double-delivers. Pinned
/// here with fakes - no timer, no UI, no network.
/// </summary>
public sealed class PendingDictationSweeperTests : IDisposable
{
    private readonly string _dir;
    private readonly PendingDictationStore _store;
    private static readonly byte[] SampleWav = { 4, 4, 4, 4 };

    public PendingDictationSweeperTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sweeper-tests-" + Guid.NewGuid().ToString("N"));
        _store = new PendingDictationStore(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private sealed class FakeTranscriber : IDictationTranscriber
    {
        private readonly Func<byte[], Task<DictationTranscript>> _fn;
        public FakeTranscriber(Func<byte[], Task<DictationTranscript>> fn) => _fn = fn;
        public static FakeTranscriber Returning(string text)
            => new(_ => Task.FromResult(new DictationTranscript(text, text, 0)));
        public Task<DictationTranscript> TranscribeAsync(byte[] wav, CancellationToken ct = default) => _fn(wav);
    }

    private DictationDeliveryService Delivery(IDictationTranscriber t) => new(t, _store);

    /// <summary>A session resolver where only the ids in <paramref name="present"/> are deliverable.</summary>
    private static Func<string, Func<string, Task>?> Present(List<string> sink, params string[] present)
    {
        var set = new HashSet<string>(present, StringComparer.Ordinal);
        return sid => set.Contains(sid) ? (t => { sink.Add(t); return Task.CompletedTask; }) : null;
    }

    [Fact]
    public async Task SweepAsync_DeliversPendingClipWhoseSessionIsPresent_AndRemovesIt()
    {
        _store.Save("present", "", SampleWav);
        var sweeper = new PendingDictationSweeper(_store, Delivery(FakeTranscriber.Returning("hi")));
        var submitted = new List<string>();

        var report = await sweeper.SweepAsync(Present(submitted, "present"));

        Assert.Equal(1, report.Delivered);
        Assert.Equal(new[] { "hi" }, submitted);
        Assert.Empty(_store.LoadAll());
        Assert.Equal(0, report.StillHeld);
    }

    [Fact]
    public async Task SweepAsync_LeavesClipWhoseSessionIsNotLoadedYet_ForALaterSweep()
    {
        _store.Save("not-loaded", "", SampleWav);
        var sweeper = new PendingDictationSweeper(_store, Delivery(FakeTranscriber.Returning("hi")));
        var submitted = new List<string>();

        var report = await sweeper.SweepAsync(Present(submitted /* nothing present */));

        Assert.Equal(0, report.Delivered);
        Assert.Equal(1, report.WaitingForSession);
        Assert.Single(_store.LoadAll());     // kept
        Assert.True(report.StillHeld > 0);
    }

    [Fact]
    public async Task SweepAsync_SkipsParkedNeedsAttentionClips()
    {
        var rec = _store.Save("present", "", SampleWav);
        _store.WriteSidecar(rec with { Status = PendingDictationStatus.NeedsAttention });
        var sweeper = new PendingDictationSweeper(_store, Delivery(FakeTranscriber.Returning("hi")));
        var submitted = new List<string>();

        var report = await sweeper.SweepAsync(Present(submitted, "present"));

        Assert.Equal(0, report.Delivered);
        Assert.Equal(1, report.ParkedNeedingAttention);
        Assert.Empty(submitted);
        Assert.Single(_store.LoadAll());     // parked clip stays until promoted
    }

    [Fact]
    public void PromoteParkedToPending_FlipsNeedsAttentionBackToPending()
    {
        var a = _store.Save("s", "", SampleWav);
        var b = _store.Save("s", "", SampleWav);
        _store.WriteSidecar(a with { Status = PendingDictationStatus.NeedsAttention });
        // b stays Pending
        var sweeper = new PendingDictationSweeper(_store, Delivery(FakeTranscriber.Returning("hi")));

        var promoted = sweeper.PromoteParkedToPending();

        Assert.Equal(1, promoted);
        Assert.All(_store.LoadAll(), r => Assert.Equal(PendingDictationStatus.Pending, r.Status));
        _ = b;
    }

    [Fact]
    public async Task SweepAsync_PrunesStaleClipsFirst()
    {
        var stale = _store.Save("present", "", SampleWav);
        _store.WriteSidecar(stale with { CreatedUtc = DateTime.UtcNow.AddDays(-30).ToString("o") });
        var sweeper = new PendingDictationSweeper(_store, Delivery(FakeTranscriber.Returning("hi")), staleAfter: TimeSpan.FromDays(7));
        var submitted = new List<string>();

        var report = await sweeper.SweepAsync(Present(submitted, "present"));

        Assert.Equal(1, report.Pruned);
        Assert.Equal(0, report.Delivered);   // it was pruned before any delivery attempt
        Assert.Empty(submitted);
        Assert.Empty(_store.LoadAll());
    }

    [Fact]
    public async Task TryDeliverAsync_WhileClipAlreadyInFlight_ReturnsNull_NoDoubleDelivery()
    {
        var rec = _store.Save("present", "", SampleWav);
        var gate = new TaskCompletionSource();
        var transcribeStarted = new TaskCompletionSource();
        var blocking = new FakeTranscriber(async _ =>
        {
            transcribeStarted.TrySetResult();
            await gate.Task;                       // hold the first delivery inside transcription
            return new DictationTranscript("hi", "hi", 0);
        });
        var sweeper = new PendingDictationSweeper(_store, Delivery(blocking));
        var submitted = new List<string>();
        Func<string, Task> submit = t => { lock (submitted) submitted.Add(t); return Task.CompletedTask; };

        // Start the first delivery; wait until it is inside transcription (id claimed).
        var first = sweeper.TryDeliverAsync(rec, submit);
        await transcribeStarted.Task;

        // A second attempt for the SAME id is refused while the first is in flight.
        var second = await sweeper.TryDeliverAsync(rec, submit);
        Assert.Null(second);

        // Let the first finish; it delivers exactly once.
        gate.SetResult();
        var firstResult = await first;
        Assert.NotNull(firstResult);
        Assert.Equal(DictationDeliveryOutcome.Delivered, firstResult!.Outcome);
        Assert.Single(submitted);
    }

    [Fact]
    public async Task SweepAsync_DefersClipWhoseSessionIsBusy_KeepsIt_NeverTypesIntoTheComposer()
    {
        // issue #1135: the session is loaded but not idle at its prompt. The clip must be deferred, not
        // typed in, so a failed-echo submit can never pile up duplicate copies.
        _store.Save("busy", "", SampleWav);
        var sweeper = new PendingDictationSweeper(_store, Delivery(FakeTranscriber.Returning("hi")));
        var submitted = new List<string>();

        var report = await sweeper.SweepAsync(Present(submitted, "busy"), isSessionReady: _ => false);

        Assert.Equal(0, report.Delivered);
        Assert.Equal(1, report.DeferredSessionBusy);
        Assert.Empty(submitted);              // never typed into a busy composer
        Assert.Single(_store.LoadAll());      // kept for a later sweep
        Assert.True(report.StillHeld > 0);
    }

    [Fact]
    public async Task SweepAsync_DeliversWhenSessionIsReady()
    {
        _store.Save("ready", "", SampleWav);
        var sweeper = new PendingDictationSweeper(_store, Delivery(FakeTranscriber.Returning("hi")));
        var submitted = new List<string>();

        var report = await sweeper.SweepAsync(Present(submitted, "ready"), isSessionReady: _ => true);

        Assert.Equal(1, report.Delivered);
        Assert.Equal(new[] { "hi" }, submitted);
        Assert.Empty(_store.LoadAll());
    }

    [Fact]
    public async Task SweepAsync_NullReadiness_DeliversEveryPresentClip_AsBefore()
    {
        // Backward-compatible: with no readiness predicate every loaded session is treated as ready.
        _store.Save("present", "", SampleWav);
        var sweeper = new PendingDictationSweeper(_store, Delivery(FakeTranscriber.Returning("hi")));
        var submitted = new List<string>();

        var report = await sweeper.SweepAsync(Present(submitted, "present"));

        Assert.Equal(1, report.Delivered);
        Assert.Equal(new[] { "hi" }, submitted);
    }

    [Fact]
    public async Task SweepAsync_MixedBatch_DeliversPresentAndHoldsAbsent()
    {
        _store.Save("present", "", SampleWav);
        _store.Save("absent", "", SampleWav);
        var sweeper = new PendingDictationSweeper(_store, Delivery(FakeTranscriber.Returning("hi")));
        var submitted = new List<string>();

        var report = await sweeper.SweepAsync(Present(submitted, "present"));

        Assert.Equal(1, report.Delivered);
        Assert.Equal(1, report.WaitingForSession);
        var remaining = _store.LoadAll();
        Assert.Single(remaining);
        Assert.Equal("absent", remaining[0].SessionId);
    }
}
