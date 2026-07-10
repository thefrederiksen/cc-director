using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Utilities;
using NAudio.Wave;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// Plays short user-interface sound cues on the desktop by SYNTHESIZING the
/// waveform in code - there are no bundled audio files to ship or resolve. The
/// only cue today is the dictation "ready" signal: a brief water-drop "bloop"
/// played the instant the microphone is confirmed capturing real audio, so the
/// user hears exactly when to start speaking (the same courtesy the Windows
/// dictation panel gives with its ready beep).
///
/// The tone is generated as 16-bit mono PCM and played through NAudio's default
/// output device, mirroring <see cref="DesktopTtsPlayer"/>'s playback pattern.
/// Playback is fire-and-forget and best-effort by design: a cue is a courtesy,
/// so a missing or busy output device must never disrupt the dictation turn - a
/// failure is logged and swallowed, exactly as a missing text-to-speech voice is.
/// </summary>
public sealed class DesktopAudioCue
{
    private const int SampleRate = 44_100;

    /// <summary>
    /// Play the dictation "ready" water-drop bloop once. Returns immediately and
    /// plays on a background thread. Never throws: a cue failure is logged and
    /// swallowed so it can never take down the caller's dictation turn.
    /// </summary>
    public void PlayReady()
    {
        byte[] pcm;
        try
        {
            pcm = BuildWaterDropBloop();
        }
        catch (Exception ex)
        {
            // Synthesis is pure arithmetic and should never fail; if it somehow does,
            // the cue is skipped rather than propagating into the dictation flow.
            FileLog.Write($"[DesktopAudioCue] bloop synthesis failed: {ex.Message}");
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                using var ms = new MemoryStream(pcm);
                var source = new RawSourceWaveStream(ms, new WaveFormat(SampleRate, 16, 1));
                using var output = new WaveOutEvent();
                output.Init(source);
                output.Play();
                while (output.PlaybackState == PlaybackState.Playing)
                    Thread.Sleep(20);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DesktopAudioCue] bloop playback failed: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Synthesize a short water-drop "bloop": a sine tone whose pitch glides quickly
    /// UPWARD while its amplitude decays exponentially. The rising-pitch glide paired
    /// with a fast decay is the perceptual signature of a droplet (a "plink"), and the
    /// whole cue is only ~200 ms so it reads as a single drop rather than a beep. A
    /// short raised-cosine fade at each end removes the click a hard start/stop would
    /// make. Returned as 16-bit little-endian mono PCM at <see cref="SampleRate"/>.
    /// </summary>
    private static byte[] BuildWaterDropBloop()
    {
        const double durationSeconds = 0.20;
        const double startHz = 380.0;    // pitch glides up...
        const double endHz = 1150.0;     // ...to give the droplet "bloop" chirp
        const double decayRate = 26.0;   // exponential amplitude decay (higher = shorter tail)
        const double amplitude = 0.55;   // peak level, with headroom below clipping
        const double fadeSeconds = 0.006; // click-free attack/release

        int sampleCount = (int)(SampleRate * durationSeconds);
        var pcm = new byte[sampleCount * 2];

        // Advance the phase per sample so the instantaneous frequency can rise without
        // introducing a phase discontinuity (a naive sin(2*pi*f(t)*t) would click).
        double phase = 0.0;
        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / SampleRate;
            double progress = (double)i / sampleCount;

            double freq = startHz + (endHz - startHz) * progress;
            phase += 2.0 * Math.PI * freq / SampleRate;

            double envelope = Math.Exp(-decayRate * t);
            double attack = t < fadeSeconds ? t / fadeSeconds : 1.0;
            double remaining = durationSeconds - t;
            double release = remaining < fadeSeconds ? remaining / fadeSeconds : 1.0;

            double value = Math.Sin(phase) * envelope * attack * release * amplitude;
            short sample = (short)Math.Clamp(value * short.MaxValue, short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(sample & 0xFF);
            pcm[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return pcm;
    }
}
