using CcDirector.Core.Audio;
using Xunit;

namespace CcDirector.Core.Tests.Audio;

/// <summary>
/// Tests for <see cref="WavSplitter"/>, the helper that cuts one long PCM WAV into several bounded WAV
/// parts so no transcription request exceeds the provider body limit. They prove the parts stay within
/// the budget, that every sample is preserved in order across the parts (nothing is dropped or
/// duplicated), and that a non-WAV blob reports failure rather than being silently truncated.
/// </summary>
public sealed class WavSplitterTests
{
    private const int SampleRate = 24000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;

    // A recognizable PCM body: a byte ramp so the reassembled data can be compared to the original.
    private static byte[] Ramp(int length)
    {
        var b = new byte[length];
        for (int i = 0; i < length; i++) b[i] = (byte)(i & 0xFF);
        return b;
    }

    [Fact]
    public void TrySplit_ClipWithinBudget_ReturnsSinglePart()
    {
        var wav = PcmWav.Wrap(Ramp(2000), SampleRate, Channels, BitsPerSample);

        Assert.True(WavSplitter.TrySplit(wav, maxPartBytes: 1_000_000, out var parts));
        Assert.NotNull(parts);
        Assert.Single(parts!);
        Assert.Equal(wav.Length, parts![0].Length);
    }

    [Fact]
    public void TrySplit_LongClip_ProducesPartsAllWithinBudget()
    {
        // 900,000 PCM bytes against a 300,000-byte budget -> at least four parts.
        var pcm = Ramp(900_000);
        var wav = PcmWav.Wrap(pcm, SampleRate, Channels, BitsPerSample);
        const int budget = 300_000;

        Assert.True(WavSplitter.TrySplit(wav, budget, out var parts));
        Assert.NotNull(parts);
        Assert.True(parts!.Count >= 4, $"expected the clip to split into several parts, got {parts.Count}");
        Assert.All(parts, p => Assert.True(p.Length <= budget, $"a part is {p.Length} bytes, over the {budget} budget"));
    }

    [Fact]
    public void TrySplit_LongClip_PreservesEverySampleInOrder()
    {
        var pcm = Ramp(500_003); // deliberately not a multiple of the budget so the last part is short
        var wav = PcmWav.Wrap(pcm, SampleRate, Channels, BitsPerSample);

        Assert.True(WavSplitter.TrySplit(wav, maxPartBytes: 120_000, out var parts));
        Assert.NotNull(parts);

        // Concatenate the PCM data (strip each part's 44-byte header) and compare to the original.
        var reassembled = new List<byte>(pcm.Length);
        foreach (var part in parts!)
            reassembled.AddRange(part[WavSplitter.WavHeaderBytes..]);

        Assert.Equal(pcm, reassembled.ToArray());
    }

    [Fact]
    public void TrySplit_PartsAreFrameAligned_ForStereo16Bit()
    {
        // Stereo 16-bit -> block align is 4 bytes; every part's data length must be a multiple of 4 so
        // no part ever starts or ends mid-sample.
        var pcm = Ramp(400_000);
        var wav = PcmWav.Wrap(pcm, SampleRate, channels: 2, BitsPerSample);

        Assert.True(WavSplitter.TrySplit(wav, maxPartBytes: 100_000, out var parts));
        Assert.NotNull(parts);
        // The last part may be the remainder; every part except possibly the last should be aligned,
        // and because the total is a multiple of 4 the last one is too.
        Assert.All(parts!, p => Assert.Equal(0, (p.Length - WavSplitter.WavHeaderBytes) % 4));
    }

    [Fact]
    public void TrySplit_NonWavBytes_ReturnsFalse()
    {
        var notWav = Ramp(500_000); // no RIFF/WAVE header
        Assert.False(WavSplitter.TrySplit(notWav, maxPartBytes: 100_000, out var parts));
        Assert.Null(parts);
    }
}
