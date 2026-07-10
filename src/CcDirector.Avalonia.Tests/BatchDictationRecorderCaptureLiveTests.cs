using CcDirector.Avalonia.Voice;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Tests for the "microphone is now live" signal (<see cref="BatchDictationRecorder.OnCaptureLive"/>)
/// that drives the desktop dialog's flip to RECORDING and the water-drop ready cue.
///
/// The whole point of the signal is honesty: it must fire only when REAL audio has
/// actually been captured - not merely when the driver was asked to start - so the
/// red state and the ready sound never precede audio. These tests drive that through
/// the injected <see cref="IAudioSource"/> seam with a fake source, so there is no
/// real microphone, no sound device, and no network.
/// </summary>
public sealed class BatchDictationRecorderCaptureLiveTests
{
    /// <summary>Minimal fake mic: emits captured chunks on demand via <see cref="Emit"/>.</summary>
    private sealed class FakeMic : IAudioSource
    {
        public event Action<byte[]>? OnAudioChunk;
        public string Description => "Fake Test Microphone";
        public void Start() { }
        public void Stop() { }
        public Task StopAsync(TimeSpan drainTimeout) => Task.CompletedTask;
        public void Emit(byte[] chunk) => OnAudioChunk?.Invoke(chunk);
    }

    private static BatchDictationRecorder NewRecorder(FakeMic fake) =>
        new(new AgentOptions(), _ => fake,
            (_, _, _) => Task.FromResult(new DictationResult("", "", 0)));

    [Fact]
    public async Task OnCaptureLive_FiresOnceOnFirstRealAudioChunk()
    {
        var fake = new FakeMic();
        await using var recorder = NewRecorder(fake);

        int liveCount = 0;
        recorder.OnCaptureLive += () => liveCount++;

        await recorder.StartAsync();
        // StartRecording merely returning is NOT "live": no audio has been delivered.
        Assert.Equal(0, liveCount);

        fake.Emit(new byte[] { 1, 2, 3, 4 });
        // The first real buffer is the honest "mic is capturing your voice" moment.
        Assert.Equal(1, liveCount);

        fake.Emit(new byte[] { 5, 6, 7, 8 });
        // One-shot: subsequent buffers do not re-fire it.
        Assert.Equal(1, liveCount);
    }

    [Fact]
    public async Task OnCaptureLive_NotFiredForEmptyChunk()
    {
        var fake = new FakeMic();
        await using var recorder = NewRecorder(fake);

        int liveCount = 0;
        recorder.OnCaptureLive += () => liveCount++;

        await recorder.StartAsync();
        fake.Emit(Array.Empty<byte>()); // a zero-length buffer is not real audio

        Assert.Equal(0, liveCount);
    }
}
