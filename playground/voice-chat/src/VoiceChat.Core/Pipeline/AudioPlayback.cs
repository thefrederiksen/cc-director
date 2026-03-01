using NAudio.Wave;

namespace VoiceChat.Core.Pipeline;

/// <summary>
/// Plays synthesized audio through speakers using NAudio.
/// Accepts float[] samples at a given sample rate.
/// </summary>
public sealed class AudioPlayback : IDisposable
{
    private WaveOutEvent? _waveOut;

    public event Action<string>? StatusChanged;
    public event Action? PlaybackFinished;

    public Task PlayAsync(float[] samples, int sampleRate, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource();

        // Convert float samples to 16-bit PCM
        var pcmBytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1.0f, 1.0f);
            var sample16 = (short)(clamped * short.MaxValue);
            pcmBytes[i * 2] = (byte)(sample16 & 0xFF);
            pcmBytes[i * 2 + 1] = (byte)((sample16 >> 8) & 0xFF);
        }

        var format = new WaveFormat(sampleRate, 16, 1);
        var stream = new RawSourceWaveStream(new MemoryStream(pcmBytes), format);

        _waveOut?.Dispose();
        _waveOut = new WaveOutEvent();

        _waveOut.PlaybackStopped += (_, _) =>
        {
            stream.Dispose();
            PlaybackFinished?.Invoke();
            tcs.TrySetResult();
        };

        ct.Register(() =>
        {
            _waveOut?.Stop();
            tcs.TrySetCanceled();
        });

        StatusChanged?.Invoke("Playing audio...");
        _waveOut.Init(stream);
        _waveOut.Play();

        return tcs.Task;
    }

    public void Stop()
    {
        _waveOut?.Stop();
    }

    public void Dispose()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
    }
}
