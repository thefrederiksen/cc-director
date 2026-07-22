using System.Text;

namespace CcDirector.Core.Audio;

/// <summary>
/// The ONE place that wraps raw PCM in a minimal RIFF/WAV container. Transcription
/// hosted speech APIs reject raw PCM without a container header, so
/// capture surfaces that produce raw PCM - the desktop mic (<c>BatchDictationRecorder</c>) and the
/// browser dictation endpoint (<c>DictationEndpoint</c>) - wrap it here before handing audio to the
/// Gateway transcription endpoint.
/// </summary>
public static class PcmWav
{
    /// <summary>
    /// Trailing silence appended to a captured clip before transcription (the dictation end-word fix).
    /// Capture keeps every byte the user spoke, but the batch transcription model (Whisper) clips the
    /// FINAL word when a clip ends abruptly on speech with no run-out - exactly what happens when the
    /// user stops the instant he finishes talking. A short pad of digital silence gives the model the
    /// trailing anchor it needs so the last word survives. This is the C# twin of the browser
    /// <c>TRANSCRIBE_TRAILING_SILENCE_MS</c> (packages/client-core/src/dictation/wav.ts); keep the two in
    /// step so every surface transcribes with the same run-out room.
    /// </summary>
    public const int TrailingSilenceMs = 600;

    /// <summary>
    /// Return <paramref name="pcm"/> with <see cref="TrailingSilenceMs"/> of digital silence (zero,
    /// frame-aligned bytes) appended for the given format. The pad is run-out room for the transcription
    /// model, NOT captured audio, so callers append it only to the bytes they send to transcription and
    /// keep the unpadded length for any capture-health / bytes-received accounting.
    /// </summary>
    public static byte[] WithTrailingSilence(byte[] pcm, int sampleRate, int channels, int bitsPerSample, int milliseconds)
    {
        if (pcm is null) throw new ArgumentNullException(nameof(pcm));
        if (milliseconds <= 0) return pcm;
        int blockAlign = Math.Max(1, channels * bitsPerSample / 8);
        long padBytes = (long)sampleRate * channels * bitsPerSample / 8 * milliseconds / 1000;
        padBytes -= padBytes % blockAlign; // frame-align so a sample is never split
        if (padBytes <= 0) return pcm;
        var padded = new byte[pcm.Length + padBytes]; // new arrays are zero-filled = silence
        Buffer.BlockCopy(pcm, 0, padded, 0, pcm.Length);
        return padded;
    }

    /// <summary>
    /// Wrap raw little-endian PCM samples in a RIFF/WAV header. The returned blob is
    /// a complete <c>.wav</c> file the transcription endpoint can decode directly.
    /// </summary>
    /// <param name="pcm">The raw PCM sample bytes (no header).</param>
    /// <param name="sampleRate">Samples per second (e.g. 24000).</param>
    /// <param name="channels">Channel count (e.g. 1 for mono).</param>
    /// <param name="bitsPerSample">Bits per sample (e.g. 16).</param>
    public static byte[] Wrap(byte[] pcm, int sampleRate, int channels, int bitsPerSample)
    {
        if (pcm is null) throw new ArgumentNullException(nameof(pcm));

        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;
        using var ms = new MemoryStream(44 + pcm.Length);
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcm.Length);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)bitsPerSample);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(pcm.Length);
        bw.Write(pcm, 0, pcm.Length);
        bw.Flush();
        return ms.ToArray();
    }
}
