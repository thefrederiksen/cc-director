using System.Text;

namespace CcDirector.Core.Audio;

/// <summary>
/// Splits one canonical PCM WAV blob into several standalone WAV blobs, each no larger than a byte
/// budget, so a long recording is transcribed as several bounded requests instead of one oversized
/// upload. This is what keeps every transcription request under the provider's request-body limit: the
/// DevThrottle managed proxy runs on a serverless function that rejects a body over roughly 4.5
/// megabytes with a FUNCTION_PAYLOAD_TOO_LARGE / HTTP 413 error, which is exactly what a whole
/// twenty-minute clip produced before this existed.
///
/// Only PCM WAV is splittable here, because a WAV has a fixed bytes-per-second rate and its sample
/// data can be cut on any frame boundary. Compressed containers (webm, m4a, ogg) cannot be cut
/// arbitrarily, so <see cref="TrySplit"/> reports failure for them and the caller must send them whole
/// (they are short by nature) or fail loud rather than post an oversized body. The desktop and browser
/// dictation surfaces both capture PCM and wrap it as WAV, so the long-recording paths are all covered.
/// </summary>
public static class WavSplitter
{
    /// <summary>The bytes a minimal RIFF/WAV header adds in front of the sample data.</summary>
    public const int WavHeaderBytes = 44;

    /// <summary>
    /// Parse a canonical PCM WAV and split its sample data into standalone WAV parts, each whose total
    /// size (header plus data) is at most <paramref name="maxPartBytes"/>. Returns false (and leaves
    /// <paramref name="parts"/> null) when the bytes are not a PCM WAV this splitter understands. A WAV
    /// already within the budget comes back as a single part, so a caller can use the returned list
    /// uniformly whether the input was long or short.
    /// </summary>
    /// <param name="wav">The complete WAV blob to split.</param>
    /// <param name="maxPartBytes">The largest a single part may be, header included.</param>
    /// <param name="parts">The ordered WAV parts on success; null on failure.</param>
    public static bool TrySplit(byte[] wav, int maxPartBytes, out IReadOnlyList<byte[]>? parts)
    {
        parts = null;
        if (wav is null) throw new ArgumentNullException(nameof(wav));
        if (maxPartBytes <= WavHeaderBytes)
            throw new ArgumentOutOfRangeException(nameof(maxPartBytes), "the byte budget must leave room for the WAV header");

        if (!TryParse(wav, out var fmt, out var dataOffset, out var dataLength))
            return false;

        int blockAlign = fmt.Channels * fmt.BitsPerSample / 8;
        if (blockAlign <= 0) return false;

        // The largest whole number of frames that keeps a part (header plus data) within the budget.
        // Cutting on a frame boundary keeps every part a valid PCM WAV and never splits a sample.
        int maxDataPerPart = (maxPartBytes - WavHeaderBytes) / blockAlign * blockAlign;
        if (maxDataPerPart <= 0) return false;

        var result = new List<byte[]>();
        int pos = 0;
        while (pos < dataLength)
        {
            int take = Math.Min(maxDataPerPart, dataLength - pos);
            var pcm = new byte[take];
            Array.Copy(wav, dataOffset + pos, pcm, 0, take);
            result.Add(PcmWav.Wrap(pcm, fmt.SampleRate, fmt.Channels, fmt.BitsPerSample));
            pos += take;
        }

        if (result.Count == 0)
            return false;

        parts = result;
        return true;
    }

    private readonly record struct WavFormat(int SampleRate, int Channels, int BitsPerSample);

    /// <summary>
    /// Read the format of a RIFF/WAVE PCM file and locate its sample data. Scans the chunk list so it
    /// tolerates the format and data chunks appearing in any order and any extra chunks in between.
    /// Returns false for anything that is not linear PCM WAV.
    /// </summary>
    private static bool TryParse(byte[] wav, out WavFormat fmt, out int dataOffset, out int dataLength)
    {
        fmt = default;
        dataOffset = 0;
        dataLength = 0;
        if (wav.Length < 12) return false;
        if (!(wav[0] == 'R' && wav[1] == 'I' && wav[2] == 'F' && wav[3] == 'F')) return false;
        if (!(wav[8] == 'W' && wav[9] == 'A' && wav[10] == 'V' && wav[11] == 'E')) return false;

        bool haveFmt = false;
        bool haveData = false;
        int p = 12;
        while (p + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, p, 4);
            int size = BitConverter.ToInt32(wav, p + 4);
            int body = p + 8;

            // A declared size that runs past the buffer (some encoders write a streaming size of 0 or a
            // stale length): trust the bytes actually present rather than read out of range.
            if (size < 0 || body + size > wav.Length)
                size = wav.Length - body;
            if (size < 0) break;

            if (id == "fmt ")
            {
                if (size < 16) return false;
                short audioFormat = BitConverter.ToInt16(wav, body);
                short channels = BitConverter.ToInt16(wav, body + 2);
                int sampleRate = BitConverter.ToInt32(wav, body + 4);
                short bitsPerSample = BitConverter.ToInt16(wav, body + 14);
                if (audioFormat != 1) return false; // linear PCM only
                fmt = new WavFormat(sampleRate, channels, bitsPerSample);
                haveFmt = true;
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataLength = size;
                haveData = true;
            }

            // Chunks are word-aligned: an odd size is followed by a single pad byte.
            int advance = size + (size & 1);
            p = body + advance;
        }

        return haveFmt && haveData && dataLength > 0;
    }
}
