using CcDirector.Avalonia.Voice;
using CcDirector.Core.Audio;
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
///
/// The WAV also carries the transcription run-out pad (<see cref="PcmWav.TrailingSilenceMs"/> of trailing
/// silence, #2003) AFTER the capture, so the captured audio is the DATA region's PREFIX (right after the
/// header), not its suffix. Asserting on the suffix would read the pad, not the capture.
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

        // The clip is a WAV: a 44-byte header, then EXACTLY the captured PCM (body + tail delivered during
        // drain, in order), then the transcription run-out pad of trailing silence (#2003). The capture is
        // therefore the DATA region's PREFIX - asserting on the suffix reads the pad, not the audio.
        Assert.NotNull(captured.Wav);
        const int headerBytes = 44;
        Assert.True(captured.Wav.Length > headerBytes + expectedPcm.Length, "WAV must include a header, the PCM, and the pad");

        // The captured audio (tail included) sits immediately after the header - this is the real guarantee:
        // a clipped tail would break here.
        var pcmPrefix = captured.Wav.AsSpan(headerBytes, expectedPcm.Length).ToArray();
        Assert.Equal(expectedPcm, pcmPrefix);

        // Everything after the capture is the pad: pure trailing silence, and nothing but silence.
        for (int i = headerBytes + expectedPcm.Length; i < captured.Wav.Length; i++)
            Assert.True(captured.Wav[i] == 0, $"pad byte at {i} must be silence");
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
