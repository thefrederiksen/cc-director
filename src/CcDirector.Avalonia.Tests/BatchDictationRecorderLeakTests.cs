using CcDirector.Avalonia.Voice;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Regression tests for the dictation memory leak (issues #2333, #2423).
///
/// WHAT WENT WRONG
/// ---------------
/// A <see cref="BatchDictationRecorder"/> owns a NAudio capture thread, and that thread ROOTS the
/// whole object graph: source -> MicAudioCapture -> Action&lt;byte[]&gt; -> recorder -> MemoryStream ->
/// byte[]. So a recorder dropped without being disposed can never be garbage collected, AND it keeps
/// appending captured audio into an unbounded buffer forever.
///
/// Measured on a 68-hour Director: 87 live <c>BatchDictationRecorder</c> instances, 9 of them still
/// recording, holding 12.69 GB - 86% of the entire heap. Two of their buffers had reached
/// 2,147,483,615 bytes, the .NET maximum array size, which is over twelve hours of audio in one turn.
///
/// The fix is OWNERSHIP: every path that builds a recorder disposes it, including the ones that fail
/// or are abandoned part-way (see <see cref="SpeakDialogCloseDuringStartupTests"/> for the close race,
/// which is the path that actually produced those 87).
///
/// These tests pin what that relies on:
///   1. Disposal is idempotent and always stops the microphone, which is what lets every failure path
///      dispose the instance it built without having to know whether it was already torn down.
///   2. A disposed recorder ignores late driver chunks rather than resurrecting its buffer.
///   3. A normal turn's audio survives capture byte-for-byte, so the ownership work changed nothing
///      about what the user actually gets.
/// </summary>
public sealed class BatchDictationRecorderLeakTests
{
    /// <summary>
    /// Fake microphone that records whether it was stopped, and lets a test push chunks in.
    /// No real device, no NAudio, no network.
    /// </summary>
    private sealed class FakeMic : IAudioSource
    {
        public event Action<byte[]>? OnAudioChunk;
        public string Description => "Fake Test Microphone";
        public bool Started { get; private set; }
        public int StopCallCount { get; private set; }

        public void Start() => Started = true;
        public void Stop() => StopCallCount++;

        public void Emit(byte[] chunk) => OnAudioChunk?.Invoke(chunk);

        public Task StopAsync(TimeSpan drainTimeout)
        {
            StopCallCount++;
            return Task.CompletedTask;
        }
    }

    private static BatchDictationRecorder NewRecorder(FakeMic fake) =>
        new(new AgentOptions(),
            _ => fake,
            (_, _, _) => Task.FromResult(new DictationResult("raw", "clean", 0)));



    [Fact]
    public async Task ANormalTurn_ReachesTranscriptionByteForByte()
    {
        // The ownership work must not change what the user actually gets: everything captured, in
        // order, unaltered, all the way to the transcriber.
        var fake = new FakeMic();
        byte[]? transcribed = null;
        await using var recorder = new BatchDictationRecorder(
            new AgentOptions(),
            _ => fake,
            (pcm, _, _) => { transcribed = pcm; return Task.FromResult(new DictationResult("raw", "clean", 0)); });
        await recorder.StartAsync();

        var thirtySeconds = new byte[MicAudioCapture.SampleRate * (MicAudioCapture.BitsPerSample / 8) * 30];
        Random.Shared.NextBytes(thirtySeconds);
        fake.Emit(thirtySeconds);

        await recorder.TranscribeAsync();

        Assert.Equal(thirtySeconds, transcribed);
    }




    [Fact]
    public async Task DisposeAsync_IsIdempotent_AndStopsTheMicrophone()
    {
        // Every failure path now disposes the instance it built, without knowing whether some inner
        // cleanup already did. That is only safe because disposal is idempotent - pin it.
        var fake = new FakeMic();
        var recorder = NewRecorder(fake);
        await recorder.StartAsync();

        await recorder.DisposeAsync();
        var afterFirst = fake.StopCallCount;
        await recorder.DisposeAsync();
        await recorder.DisposeAsync();

        Assert.True(afterFirst > 0, "disposing must stop the microphone");
        Assert.Equal(afterFirst, fake.StopCallCount);
    }

    [Fact]
    public async Task DisposedRecorder_IgnoresLateChunks()
    {
        // A real driver can deliver one more buffer after teardown begins. That must not resurrect
        // the buffer of a recorder whose owner has already let go of it.
        var fake = new FakeMic();
        var recorder = NewRecorder(fake);
        await recorder.StartAsync();
        await recorder.DisposeAsync();

        // The recorder unsubscribed on dispose, so this goes nowhere and must not throw.
        fake.Emit(new byte[1024]);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => recorder.TranscribeAsync());
    }
}
