using System.Text;

namespace CcDirector.Core.Audio;

/// <summary>
/// The ONE place that wraps raw PCM in a minimal RIFF/WAV container. Transcription
/// APIs (OpenAI and the OpenAI-compatible proxies) reject raw PCM without a
/// container header, so every batch surface that captures raw PCM - the desktop
/// mic (<c>BatchDictationRecorder</c>), the browser dictation endpoint
/// (<c>DictationEndpoint</c>), and the in-session batch provider
/// (<c>OpenAiTranscriptionProvider</c>) - wraps it here rather than each carrying
/// its own copy of the byte layout.
/// </summary>
public static class PcmWav
{
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

    /// <summary>
    /// Read a RIFF/WAV blob back into its raw PCM16 samples plus format, for callers that need to
    /// re-encode the audio (issue #898: transcode a large dictation WAV to OGG-Opus before the
    /// remote upload). Returns false for anything that is not linear PCM16 (audioFormat 1, 16
    /// bits/sample) - e.g. an already-compressed container - so the caller can pass those through
    /// untouched. Walks the chunk list rather than assuming a fixed 44-byte layout, so a WAV with
    /// extra chunks (LIST/fact) still reads correctly.
    /// </summary>
    /// <param name="wav">The complete RIFF/WAV bytes.</param>
    /// <param name="pcm">On success, the raw little-endian PCM16 sample bytes (the data chunk).</param>
    /// <param name="sampleRate">On success, samples per second from the fmt chunk.</param>
    /// <param name="channels">On success, the channel count from the fmt chunk.</param>
    public static bool TryReadPcm16(byte[] wav, out byte[] pcm, out int sampleRate, out int channels)
    {
        pcm = Array.Empty<byte>();
        sampleRate = 0;
        channels = 0;
        if (wav is null || wav.Length < 44) return false;
        if (!Matches(wav, 0, "RIFF") || !Matches(wav, 8, "WAVE")) return false;

        int bitsPerSample = 0;
        bool haveFmt = false;
        int pos = 12; // first chunk starts after "RIFF"<size>"WAVE"
        while (pos + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, pos, 4);
            long size = BitConverter.ToUInt32(wav, pos + 4);
            int body = pos + 8;
            if (body + size > wav.Length) size = wav.Length - body; // tolerate a truncated final chunk

            if (id == "fmt " && size >= 16)
            {
                int audioFormat = BitConverter.ToUInt16(wav, body);
                channels = BitConverter.ToUInt16(wav, body + 2);
                sampleRate = (int)BitConverter.ToUInt32(wav, body + 4);
                bitsPerSample = BitConverter.ToUInt16(wav, body + 14);
                if (audioFormat != 1 || bitsPerSample != 16) return false; // not linear PCM16
                haveFmt = true;
            }
            else if (id == "data")
            {
                if (!haveFmt) return false; // data before fmt: malformed
                pcm = new byte[size];
                Buffer.BlockCopy(wav, body, pcm, 0, (int)size);
                return channels is 1 or 2 && sampleRate > 0;
            }

            // Chunks are word-aligned: an odd size is followed by one pad byte.
            pos = body + (int)size + ((size & 1) == 1 ? 1 : 0);
        }
        return false;
    }

    private static bool Matches(byte[] buf, int offset, string ascii)
    {
        for (int i = 0; i < ascii.Length; i++)
            if (buf[offset + i] != (byte)ascii[i]) return false;
        return true;
    }
}
