using CcDirector.Core.Dictation;
using CcDirector.Core.Transcription;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// The delivery engine holds the keep-vs-delete rule that is the core of issue #1130: audio is deleted
/// ONLY when delivered, and every kind of failure keeps it with the right status. Each branch is pinned
/// here with a fake transcriber - no mic, no network.
/// </summary>
public sealed class DictationDeliveryServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly PendingDictationStore _store;
    private static readonly byte[] SampleWav = { 9, 8, 7, 6, 5 };

    public DictationDeliveryServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "delivery-tests-" + Guid.NewGuid().ToString("N"));
        _store = new PendingDictationStore(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best effort */ }
    }

    private sealed class FakeTranscriber : IDictationTranscriber
    {
        private readonly Func<byte[], DictationTranscript> _fn;
        public int Calls { get; private set; }
        public FakeTranscriber(Func<byte[], DictationTranscript> fn) => _fn = fn;
        public static FakeTranscriber Returning(string text)
            => new(_ => new DictationTranscript(text, text, 0));
        public static FakeTranscriber Throwing(Exception ex)
            => new(_ => throw ex);
        public Task<DictationTranscript> TranscribeAsync(byte[] wav, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_fn(wav));
        }
    }

    [Fact]
    public async Task DeliverAsync_Success_SubmitsJoinedText_AndDeletesTheClip()
    {
        var rec = _store.Save("s", "hello", SampleWav);
        var transcriber = FakeTranscriber.Returning("world");
        var svc = new DictationDeliveryService(transcriber, _store);
        var submitted = new List<string>();

        var result = await svc.DeliverAsync(rec, t => { submitted.Add(t); return Task.CompletedTask; });

        Assert.Equal(DictationDeliveryOutcome.Delivered, result.Outcome);
        Assert.True(result.Delivered);
        Assert.Equal(new[] { "hello world" }, submitted); // prefix joined ahead of the transcript
        Assert.Empty(_store.LoadAll());                    // delivered => deleted
    }

    [Fact]
    public async Task DeliverAsync_InsertsDictationAtCaretInsideTypedText_SoRetriesNeverDropTheTypedPart()
    {
        // The user had typed "start end" with the caret between the words, then dictated "middle".
        var rec = _store.Save("s", "", SampleWav, before: "start", after: "end");
        var svc = new DictationDeliveryService(FakeTranscriber.Returning("middle"), _store);
        var submitted = new List<string>();

        var result = await svc.DeliverAsync(rec, t => { submitted.Add(t); return Task.CompletedTask; });

        Assert.Equal(DictationDeliveryOutcome.Delivered, result.Outcome);
        Assert.Equal(new[] { "start middle end" }, submitted);
    }

    [Fact]
    public async Task DeliverAsync_WithPrefixAndTypedText_ComposesInOrder()
    {
        // Earlier paused segment "one", this segment "two", typed suffix "tail" after the caret.
        var rec = _store.Save("s", "one", SampleWav, before: "", after: "tail");
        var svc = new DictationDeliveryService(FakeTranscriber.Returning("two"), _store);
        var submitted = new List<string>();

        await svc.DeliverAsync(rec, t => { submitted.Add(t); return Task.CompletedTask; });

        Assert.Equal(new[] { "one two tail" }, submitted);
    }

    [Fact]
    public async Task DeliverAsync_EmptyTranscript_IsDelivered_AndClipRemoved_SoSilenceIsNotRetriedForever()
    {
        var rec = _store.Save("s", "", SampleWav);
        var svc = new DictationDeliveryService(FakeTranscriber.Returning(""), _store);
        var submitCalls = 0;

        var result = await svc.DeliverAsync(rec, _ => { submitCalls++; return Task.CompletedTask; });

        Assert.Equal(DictationDeliveryOutcome.Delivered, result.Outcome);
        Assert.Equal(1, submitCalls);        // submit is still called; the delegate guards an empty turn
        Assert.Empty(_store.LoadAll());
    }

    [Fact]
    public async Task DeliverAsync_Transient504_KeepsClipPending_ForAutomaticRetry()
    {
        var rec = _store.Save("s", "", SampleWav);
        var ex = new TranscriptionFailedException(504, "Transcription returned 504: upstream_timeout");
        var svc = new DictationDeliveryService(FakeTranscriber.Throwing(ex), _store);
        var submitCalls = 0;

        var result = await svc.DeliverAsync(rec, _ => { submitCalls++; return Task.CompletedTask; });

        Assert.Equal(DictationDeliveryOutcome.HeldWillRetry, result.Outcome);
        Assert.True(result.WillRetryAutomatically);
        Assert.Equal(0, submitCalls);
        var kept = _store.LoadAll();
        Assert.Single(kept);
        Assert.Equal(PendingDictationStatus.Pending, kept[0].Status);   // eligible for retry
        Assert.Equal(1, kept[0].AttemptCount);
    }

    [Fact]
    public async Task DeliverAsync_NetworkException_KeepsClipPending_ForAutomaticRetry()
    {
        var rec = _store.Save("s", "", SampleWav);
        var svc = new DictationDeliveryService(FakeTranscriber.Throwing(new HttpRequestException("connection reset")), _store);

        var result = await svc.DeliverAsync(rec, _ => Task.CompletedTask);

        Assert.Equal(DictationDeliveryOutcome.HeldWillRetry, result.Outcome);
        Assert.Equal(PendingDictationStatus.Pending, _store.LoadAll()[0].Status);
    }

    [Fact]
    public async Task DeliverAsync_OutOfCredits_ParksNeedsAttention_KeepsClip()
    {
        var rec = _store.Save("s", "", SampleWav);
        var svc = new DictationDeliveryService(
            FakeTranscriber.Throwing(new InsufficientCreditsException("insufficient_credits", "out of credits")), _store);

        var result = await svc.DeliverAsync(rec, _ => Task.CompletedTask);

        Assert.Equal(DictationDeliveryOutcome.NeedsCredits, result.Outcome);
        Assert.Equal(PendingDictationStatus.NeedsAttention, _store.LoadAll()[0].Status);
    }

    [Fact]
    public async Task DeliverAsync_NoMethodConfigured_ParksNeedsAttention_KeepsClip()
    {
        var rec = _store.Save("s", "", SampleWav);
        var svc = new DictationDeliveryService(
            FakeTranscriber.Throwing(new TranscriptionUnavailableException("OpenAI key is not set.")), _store);

        var result = await svc.DeliverAsync(rec, _ => Task.CompletedTask);

        Assert.Equal(DictationDeliveryOutcome.NeedsConfiguration, result.Outcome);
        Assert.Equal(PendingDictationStatus.NeedsAttention, _store.LoadAll()[0].Status);
    }

    [Fact]
    public async Task DeliverAsync_PermanentProviderError_ParksNeedsAttention_KeepsClip()
    {
        var rec = _store.Save("s", "", SampleWav);
        var svc = new DictationDeliveryService(
            FakeTranscriber.Throwing(new TranscriptionFailedException(400, "Transcription returned 400: bad request")), _store);

        var result = await svc.DeliverAsync(rec, _ => Task.CompletedTask);

        Assert.Equal(DictationDeliveryOutcome.PermanentError, result.Outcome);
        Assert.Equal(PendingDictationStatus.NeedsAttention, _store.LoadAll()[0].Status);
    }

    [Fact]
    public async Task DeliverAsync_SubmitThrows_KeepsClipForRetry_NeverLosesIt()
    {
        // A transcription that SUCCEEDS but whose submit fails (e.g. the session momentarily unavailable)
        // must not lose the words: the clip stays saved and retryable.
        var rec = _store.Save("s", "", SampleWav);
        var svc = new DictationDeliveryService(FakeTranscriber.Returning("hello"), _store);

        var result = await svc.DeliverAsync(rec, _ => throw new InvalidOperationException("session busy"));

        Assert.Equal(DictationDeliveryOutcome.HeldWillRetry, result.Outcome);
        Assert.Single(_store.LoadAll());
    }

    [Fact]
    public async Task DeliverAsync_SessionNotReady_Defers_WithoutTranscribingSubmittingOrBumpingAttempt()
    {
        // issue #1135: a session that is busy working must not be typed into. The clip is deferred
        // BEFORE any transcription cost, the composer is never touched, and the attempt count is not
        // bumped - so it is not treated as a failure and nothing accumulates in the composer.
        var rec = _store.Save("s", "", SampleWav);
        var transcriber = FakeTranscriber.Returning("hello");
        var svc = new DictationDeliveryService(transcriber, _store);
        var submitCalls = 0;

        var result = await svc.DeliverAsync(
            rec, _ => { submitCalls++; return Task.CompletedTask; }, isSessionReady: () => false);

        Assert.Equal(DictationDeliveryOutcome.DeferredSessionBusy, result.Outcome);
        Assert.False(result.Delivered);
        Assert.Equal(0, submitCalls);            // never typed into the busy composer
        Assert.Equal(0, transcriber.Calls);      // deferred before paying to transcribe
        var kept = _store.LoadAll();
        Assert.Single(kept);                     // clip kept for a later sweep
        Assert.Equal(PendingDictationStatus.Pending, kept[0].Status);
        Assert.Equal(0, kept[0].AttemptCount);   // a deferral is not a failed attempt
    }

    [Fact]
    public async Task DeliverAsync_SessionReady_DeliversNormally()
    {
        var rec = _store.Save("s", "", SampleWav);
        var svc = new DictationDeliveryService(FakeTranscriber.Returning("hello"), _store);
        var submitted = new List<string>();

        var result = await svc.DeliverAsync(
            rec, t => { submitted.Add(t); return Task.CompletedTask; }, isSessionReady: () => true);

        Assert.Equal(DictationDeliveryOutcome.Delivered, result.Outcome);
        Assert.Equal(new[] { "hello" }, submitted);
        Assert.Empty(_store.LoadAll());          // delivered => deleted
    }

    [Fact]
    public async Task DeliverAsync_AudioMissing_ReportsLostNoAudio_NeverSilent()
    {
        var rec = _store.Save("s", "", SampleWav);
        _store.Delete(rec); // audio gone before delivery
        var svc = new DictationDeliveryService(FakeTranscriber.Returning("x"), _store);

        var result = await svc.DeliverAsync(rec, _ => Task.CompletedTask);

        Assert.Equal(DictationDeliveryOutcome.LostNoAudio, result.Outcome);
        Assert.NotNull(result.Error);
    }
}
