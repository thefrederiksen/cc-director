using CcDirector.Avalonia.Voice;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The durable fire-and-forget Send (issue #1130) saves the recorded audio to disk BEFORE transcribing
/// it, so the recorder had to expose the whole captured clip as a WAV without transcribing. These tests
/// prove <see cref="BatchDictationRecorder.StopAndGetWavAsync"/> returns the complete capture - tail
/// included - and still enforces the completeness gate, driven through the fake-microphone seam with no
/// real mic and no network.
/// </summary>
public sealed class BatchDictationRecorderStopWavTests
{
    private sealed class FakeMic : IAudioSource
    {
        private readonly byte[] _tail;
        public FakeMic(byte[] tail) => _tail = tail;
        public event Action<byte[]>? OnAudioChunk;
        public string Description => "Fake Test Microphone";
        public void Start() { }
        public void Stop() { }
        public void Emit(byte[] chunk) => OnAudioChunk?.Invoke(chunk);
        public async Task StopAsync(TimeSpan drainTimeout)
        {
            await Task.Delay(20);
            if (_tail.Length > 0) OnAudioChunk?.Invoke(_tail);
        }
    }

    private static BatchDictationRecorder NewRecorder(FakeMic fake)
        // The transcribe-override is never called by StopAndGetWavAsync, but the internal test
        // constructor requires one; give it a stub that would fail the test loudly if it ever ran.
        => new(new AgentOptions(), _ => fake,
            (_, _, _) => throw new InvalidOperationException("StopAndGetWavAsync must not transcribe"));

    [Fact]
    public async Task StopAndGetWavAsync_ReturnsWholeCaptureAsWav_IncludingTailDeliveredDuringDrain()
    {
        var body = new byte[] { 1, 2, 3, 4 };
        var tail = new byte[] { 5, 6, 7, 8 };
        var expectedPcm = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var fake = new FakeMic(tail);
        await using var recorder = NewRecorder(fake);

        await recorder.StartAsync();
        fake.Emit(body);

        var captured = await recorder.StopAndGetWavAsync();

        // The clip is a WAV: a header followed by exactly the captured PCM, tail included and in order.
        Assert.NotNull(captured.Wav);
        Assert.True(captured.Wav.Length > expectedPcm.Length, "WAV must include a header plus the PCM");
        var pcmSuffix = captured.Wav.AsSpan(captured.Wav.Length - expectedPcm.Length).ToArray();
        Assert.Equal(expectedPcm, pcmSuffix);
    }

    [Fact]
    public async Task StopAndGetWavAsync_NoAudioCaptured_EnforcesCompletenessGate()
    {
        var fake = new FakeMic(Array.Empty<byte>());
        await using var recorder = NewRecorder(fake);
        await recorder.StartAsync();

        await Assert.ThrowsAsync<NoAudioCapturedException>(() => recorder.StopAndGetWavAsync());
    }
}
