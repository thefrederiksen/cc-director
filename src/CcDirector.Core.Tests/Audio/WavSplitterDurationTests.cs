using CcDirector.Core.Audio;
using Xunit;

namespace CcDirector.Core.Tests.Audio;

/// <summary>
/// Tests for <see cref="WavSplitter.TrySplitByDuration"/> - the duration + silence aware splitter behind
/// the transcription reliability epic (#324). It cuts a long PCM WAV into short, bounded parts, nudging
/// each cut to a nearby quiet point so a word is not sliced, and it is LOSSLESS and NON-OVERLAPPING so
/// the per-part transcripts join with a single space and no de-duplication.
/// </summary>
public sealed class WavSplitterDurationTests
{
    private const int Rate = 16000;   // 16 kHz mono 16-bit throughout

    // Build a mono 16-bit WAV of `frames`, each sample = `sample`, with an optional zero (silent) gap.
    private static byte[] BuildWav(int frames, short sample, int gapStart = -1, int gapEnd = -1)
    {
        var pcm = new byte[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            short v = (gapStart >= 0 && f >= gapStart && f < gapEnd) ? (short)0 : sample;
            pcm[f * 2] = (byte)(v & 0xFF);
            pcm[f * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return PcmWav.Wrap(pcm, Rate, 1, 16);
    }

    private static byte[] PatternWav(int frames)
    {
        var pcm = new byte[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            short v = (short)(f % 1000 - 500);   // a varied, non-silent pattern
            pcm[f * 2] = (byte)(v & 0xFF);
            pcm[f * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return PcmWav.Wrap(pcm, Rate, 1, 16);
    }

    private static int DataFrames(byte[] wavPart) => (wavPart.Length - WavSplitter.WavHeaderBytes) / 2;

    [Fact]
    public void TrySplitByDuration_LongWav_EachPartWithinDurationAndByteBudget()
    {
        var wav = PatternWav(300 * Rate);   // 300 seconds
        Assert.True(WavSplitter.TrySplitByDuration(wav, 60, 90, 5, 4_000_000, out var parts));
        Assert.NotNull(parts);
        Assert.True(parts!.Count >= 4, $"expected several parts, got {parts.Count}");
        Assert.All(parts, p =>
        {
            Assert.True(DataFrames(p) <= 90 * Rate, "a part exceeded the 90 s max duration");
            Assert.True(p.Length <= 4_000_000, "a part exceeded the 4 MB byte budget");
        });
    }

    [Fact]
    public void TrySplitByDuration_IsLosslessAndNonOverlapping()
    {
        int frames = 250 * Rate;
        var wav = PatternWav(frames);
        Assert.True(WavSplitter.TrySplitByDuration(wav, 60, 90, 5, 4_000_000, out var parts));

        // Concatenating the parts' sample data reproduces the original PCM exactly: no overlap, no gap,
        // no resample. This is what lets the per-part transcripts join with a plain space.
        var rejoined = new List<byte>(frames * 2);
        foreach (var p in parts!)
            rejoined.AddRange(p[WavSplitter.WavHeaderBytes..]);

        var originalPcm = wav[WavSplitter.WavHeaderBytes..];
        Assert.Equal(originalPcm.Length, rejoined.Count);
        Assert.True(originalPcm.AsSpan().SequenceEqual(rejoined.ToArray()), "rejoined PCM differs from the original");
    }

    [Fact]
    public void TrySplitByDuration_ShortClip_SinglePart()
    {
        var wav = PatternWav(10 * Rate);   // 10 s: within one target window
        Assert.True(WavSplitter.TrySplitByDuration(wav, 60, 90, 5, 4_000_000, out var parts));
        Assert.Single(parts!);
    }

    [Fact]
    public void TrySplitByDuration_NotPcmWav_ReturnsFalse()
    {
        var notWav = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 };
        Assert.False(WavSplitter.TrySplitByDuration(notWav, 60, 90, 5, 4_000_000, out var parts));
        Assert.Null(parts);
    }

    [Fact]
    public void TrySplitByDuration_PrefersASilentCutNearTheTarget()
    {
        // 4 s of loud audio with a 62.5 ms silent gap at 1.875 s - inside the [target-window, target+window]
        // = [1 s, 3 s] search band around the 2 s target. The first cut must land in that gap, not at the
        // naive 2 s target boundary, so a word straddling the target is not sliced.
        int gapStart = 30000, gapEnd = 31000;                 // frames (1.875 s .. 1.9375 s)
        var wav = BuildWav(4 * Rate, sample: 8000, gapStart, gapEnd);

        // Large byte budget so DURATION (not bytes) governs; target 2 s, max 3 s, window 1 s.
        Assert.True(WavSplitter.TrySplitByDuration(wav, 2, 3, 1, 100_000_000, out var parts));
        Assert.True(parts!.Count >= 2);

        int firstCut = DataFrames(parts[0]);
        Assert.True(firstCut >= gapStart - 400 && firstCut <= gapEnd + 400,
            $"first cut {firstCut} was not inside the silent gap [{gapStart},{gapEnd}]");
        Assert.NotEqual(2 * Rate, firstCut);   // it was nudged off the naive 2 s target
    }
}
