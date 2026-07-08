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

    /// <summary>
    /// Split a PCM WAV into standalone WAV parts by DURATION, preferring a cut at a quiet point
    /// (near-silence) close to each target boundary so a word is not sliced across two parts. Every
    /// part is at most <paramref name="maxSeconds"/> long AND at most <paramref name="maxPartBytes"/>,
    /// so each is a valid, bounded, single-shot upload. The split is LOSSLESS and NON-OVERLAPPING:
    /// concatenating the parts' sample data reproduces the input exactly, so the per-part transcripts
    /// join with a single space and no de-duplication. A clip already within one target window comes
    /// back as a single part. Returns false (and null parts) for anything that is not linear PCM WAV.
    /// </summary>
    /// <param name="wav">The complete PCM WAV blob.</param>
    /// <param name="targetSeconds">Preferred chunk length; the cut is nudged to nearby silence.</param>
    /// <param name="maxSeconds">Hard cap on a chunk's length (used when no silence is found).</param>
    /// <param name="silenceWindowSeconds">How far before/after the target to search for a quiet cut.</param>
    /// <param name="maxPartBytes">Hard cap on a chunk's total byte size (header included).</param>
    /// <param name="parts">The ordered WAV parts on success; null on failure.</param>
    public static bool TrySplitByDuration(
        byte[] wav, int targetSeconds, int maxSeconds, int silenceWindowSeconds, int maxPartBytes,
        out IReadOnlyList<byte[]>? parts)
    {
        parts = null;
        if (wav is null) throw new ArgumentNullException(nameof(wav));
        if (targetSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(targetSeconds));
        if (maxSeconds < targetSeconds) throw new ArgumentOutOfRangeException(nameof(maxSeconds));
        if (maxPartBytes <= WavHeaderBytes)
            throw new ArgumentOutOfRangeException(nameof(maxPartBytes), "the byte budget must leave room for the WAV header");

        if (!TryParse(wav, out var fmt, out var dataOffset, out var dataLength))
            return false;

        int blockAlign = fmt.Channels * fmt.BitsPerSample / 8;
        if (blockAlign <= 0 || fmt.SampleRate <= 0) return false;

        int framesTotal = dataLength / blockAlign;
        if (framesTotal <= 0) return false;

        // A single part may never exceed the byte budget OR the max duration, whichever is smaller.
        long maxFramesByBytes = (maxPartBytes - WavHeaderBytes) / blockAlign;
        long maxFramesByDuration = (long)maxSeconds * fmt.SampleRate;
        int hardMaxFrames = (int)Math.Min(maxFramesByBytes, maxFramesByDuration);
        if (hardMaxFrames <= 0) return false;

        int targetFrames = (int)Math.Min((long)targetSeconds * fmt.SampleRate, hardMaxFrames);
        int windowFrames = (int)Math.Max(0, Math.Min((long)silenceWindowSeconds * fmt.SampleRate, targetFrames - 1));
        int probeFrames = Math.Max(1, fmt.SampleRate * 20 / 1000);   // ~20 ms energy window

        var result = new List<byte[]>();
        int pos = 0;
        while (pos < framesTotal)
        {
            int remaining = framesTotal - pos;
            int cutLen;
            if (remaining <= hardMaxFrames && remaining <= targetFrames + windowFrames)
            {
                cutLen = remaining;                       // the rest fits in one final part
            }
            else
            {
                int lo = Math.Max(1, targetFrames - windowFrames);
                int hi = Math.Min(hardMaxFrames, Math.Min(remaining, targetFrames + windowFrames));
                if (hi < lo) hi = lo;
                cutLen = QuietestCut(wav, dataOffset, blockAlign, fmt.BitsPerSample, pos, lo, hi, probeFrames);
            }
            if (cutLen <= 0) cutLen = Math.Min(hardMaxFrames, remaining);

            int byteLen = cutLen * blockAlign;
            var pcm = new byte[byteLen];
            Array.Copy(wav, dataOffset + pos * blockAlign, pcm, 0, byteLen);
            result.Add(PcmWav.Wrap(pcm, fmt.SampleRate, fmt.Channels, fmt.BitsPerSample));
            pos += cutLen;
        }

        if (result.Count == 0) return false;
        parts = result;
        return true;
    }

    /// <summary>
    /// The cut length (frames from <paramref name="posFrames"/>) at the center of the quietest ~20 ms
    /// window whose center lies in [lo, hi]. Deterministic. For non-16-bit PCM the energy scan is
    /// skipped and the hard boundary <paramref name="hi"/> is returned (a plain duration cut).
    /// </summary>
    private static int QuietestCut(byte[] wav, int dataOffset, int blockAlign, int bitsPerSample,
        int posFrames, int lo, int hi, int probeFrames)
    {
        if (hi <= lo) return Math.Max(1, hi);
        if (bitsPerSample != 16) return hi;

        int step = Math.Max(1, probeFrames / 2);
        long best = long.MaxValue;
        int bestCut = hi;
        for (int c = lo; c <= hi; c += step)
        {
            long e = FrameEnergy(wav, dataOffset, blockAlign, posFrames + c, probeFrames);
            if (e < best) { best = e; bestCut = c; }
        }
        return bestCut;
    }

    /// <summary>Sum of absolute 16-bit sample values over a small window centered on a frame.</summary>
    private static long FrameEnergy(byte[] wav, int dataOffset, int blockAlign, int centerFrame, int probeFrames)
    {
        int start = Math.Max(0, centerFrame - probeFrames / 2);
        long sum = 0;
        for (int f = 0; f < probeFrames; f++)
        {
            int frameByte = dataOffset + (start + f) * blockAlign;
            if (frameByte + blockAlign > wav.Length) break;
            for (int b = 0; b + 1 < blockAlign; b += 2)
            {
                short s = (short)(wav[frameByte + b] | (wav[frameByte + b + 1] << 8));
                sum += Math.Abs((int)s);
            }
        }
        return sum;
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
