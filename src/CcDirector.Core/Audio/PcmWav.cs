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
    /// The largest PCM payload (samples plus run-out pad) that can be wrapped, because the finished
    /// WAV is one managed array and must carry a 44-byte header in front of it. Beyond this,
    /// <see cref="WrapWithTrailingSilence"/> throws rather than overflowing into a negative length.
    /// </summary>
    public const int MaxWrappablePcmBytes = int.MaxValue - 44;

    /// <summary>
    /// Total data-chunk length for a payload plus its run-out pad, refusing anything whose finished
    /// WAV could not fit one managed array.
    ///
    /// Split out so the refusal can be TESTED without allocating two gigabytes. The old pad-then-wrap
    /// chain mixed checked and unchecked arithmetic here: the pad addition threw
    /// <see cref="OverflowException"/> while the separate <c>44 + dataLength</c> could wrap to a
    /// negative length and produce a corrupt file. A dictation buffer of 2,147,483,615 bytes was
    /// observed in the field, and the 44-byte header plus the run-out pad push exactly that case over
    /// the limit - so this is a reachable input, not a theoretical one.
    /// </summary>
    internal static int CheckedDataLength(long pcmLength, long padBytes)
    {
        if (pcmLength < 0) throw new ArgumentOutOfRangeException(nameof(pcmLength));
        if (padBytes < 0) throw new ArgumentOutOfRangeException(nameof(padBytes));
        // Compared WITHOUT adding: pcmLength + padBytes can itself overflow for large valid longs
        // (long.MaxValue + 1 wraps negative and would slip past an upper-bound check).
        if (pcmLength > MaxWrappablePcmBytes || padBytes > MaxWrappablePcmBytes - pcmLength)
            throw new ArgumentException(
                $"Cannot wrap {pcmLength} bytes of PCM plus {padBytes} bytes of run-out: a WAV must fit in a "
                + $"single array, so the payload cannot exceed {MaxWrappablePcmBytes} bytes.", nameof(pcmLength));
        return (int)(pcmLength + padBytes);
    }

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
        return WrapWithTrailingSilence(pcm, sampleRate, channels, bitsPerSample, 0);
    }

    /// <summary>
    /// Wrap raw PCM in a RIFF/WAV header AND append the transcription run-out pad, in a SINGLE
    /// allocation.
    ///
    /// WHY THIS EXISTS
    /// ---------------
    /// The old call chain was <c>WithTrailingSilence(pcm)</c> -> <c>Wrap(padded)</c>, and each step
    /// allocated a fresh full-size array on top of the caller's own <c>MemoryStream.ToArray()</c>
    /// snapshot. For a short dictation turn that is irrelevant; for a long one it is not. Every array
    /// over 85 KB lands on the Large Object Heap, which the GC does not compact, so a single long clip
    /// briefly held three-to-four copies of itself there. A 68-hour Director was measured with 12.69 GB
    /// of <c>System.Byte[]</c> - 86% of its heap - and the copy chain multiplied every one of those
    /// buffers on its way to transcription.
    ///
    /// Writing the header, the samples, and the zero-filled pad straight into one correctly-sized
    /// buffer produces byte-identical output with one allocation instead of three.
    /// </summary>
    /// <param name="pcm">The raw PCM sample bytes (no header).</param>
    /// <param name="sampleRate">Samples per second (e.g. 24000).</param>
    /// <param name="channels">Channel count (e.g. 1 for mono).</param>
    /// <param name="bitsPerSample">Bits per sample (e.g. 16).</param>
    /// <param name="trailingSilenceMs">Run-out silence to append; 0 for none.</param>
    public static byte[] WrapWithTrailingSilence(byte[] pcm, int sampleRate, int channels, int bitsPerSample, int trailingSilenceMs)
    {
        if (pcm is null) throw new ArgumentNullException(nameof(pcm));

        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;

        // Frame-align the pad exactly as WithTrailingSilence does, so the two produce identical bytes.
        long padBytes = 0;
        if (trailingSilenceMs > 0)
        {
            int align = Math.Max(1, blockAlign);
            padBytes = (long)sampleRate * channels * bitsPerSample / 8 * trailingSilenceMs / 1000;
            padBytes -= padBytes % align;
            if (padBytes < 0) padBytes = 0;
        }

        int dataLength = CheckedDataLength(pcm.Length, padBytes);
        // ONE allocation: 44-byte header + samples + zero-filled pad (new arrays are already zero = silence).
        var wav = new byte[44 + dataLength];

        using var ms = new MemoryStream(wav, writable: true);
        using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataLength);
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
            bw.Write(dataLength);
        }
        Buffer.BlockCopy(pcm, 0, wav, 44, pcm.Length);
        // Bytes 44+pcm.Length .. end are already zero, which IS the silence pad.
        return wav;
    }
}
