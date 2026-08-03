using CcDirector.Core.Audio;
using Xunit;

namespace CcDirector.Core.Tests.Audio;

/// <summary>
/// The trailing-silence run-out (dictation end-word fix): a captured clip gets a short pad of digital
/// silence appended before transcription so the model does not clip the final word. These guard the
/// shared C# pad helper - the desktop twin of the browser wav.ts pad.
/// </summary>
public sealed class PcmWavTests
{
    [Fact]
    public void WithTrailingSilence_AppendsFrameAlignedZeroBytes_16kMono16bit()
    {
        // 16 kHz mono 16-bit = 32000 bytes/sec, so 500 ms = 16000 bytes of silence.
        var pcm = new byte[] { 1, 2, 3, 4 };
        var padded = PcmWav.WithTrailingSilence(pcm, 16000, 1, 16, 500);

        Assert.Equal(4 + 16000, padded.Length);
        Assert.Equal(1, padded[0]);
        Assert.Equal(4, padded[3]);
        for (int i = 4; i < padded.Length; i++) Assert.Equal(0, padded[i]);
    }

    [Fact]
    public void WithTrailingSilence_NonPositiveMs_ReturnsSameInstance()
    {
        var pcm = new byte[] { 9, 9 };
        Assert.Same(pcm, PcmWav.WithTrailingSilence(pcm, 16000, 1, 16, 0));
        Assert.Same(pcm, PcmWav.WithTrailingSilence(pcm, 16000, 1, 16, -10));
    }

    [Fact]
    public void WithTrailingSilence_PadIsFrameAligned_ForStereo16bit()
    {
        // blockAlign = 2 channels * 2 bytes = 4; the pad length must be a whole number of frames.
        var pcm = new byte[8];
        var padded = PcmWav.WithTrailingSilence(pcm, 44100, 2, 16, 137); // deliberately odd duration
        Assert.Equal(0, (padded.Length - pcm.Length) % 4);
        Assert.True(padded.Length > pcm.Length);
    }

    [Fact]
    public void TrailingSilenceMs_IsPositive()
    {
        Assert.True(PcmWav.TrailingSilenceMs > 0);
    }

    /// <summary>
    /// INDEPENDENT copy of the ORIGINAL writer, exactly as it stood before the single-allocation
    /// rewrite. It is the ORACLE for the equivalence tests below and must never be changed to match
    /// production - the moment it does, those tests stop proving anything.
    ///
    /// Why it has to exist: the obvious test compares <c>WrapWithTrailingSilence</c> against
    /// <c>Wrap(WithTrailingSilence(pcm))</c>. That is circular, because <c>Wrap</c> now delegates
    /// straight back into <c>WrapWithTrailingSilence</c> - it asserts a function equals itself and
    /// cannot detect a wrong header at all.
    /// </summary>
    private static byte[] OriginalWrap(byte[] pcm, int sampleRate, int channels, int bitsPerSample)
    {
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;
        using var ms = new MemoryStream(44 + pcm.Length);
        using var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcm.Length);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)bitsPerSample);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(pcm.Length);
        bw.Write(pcm, 0, pcm.Length);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// The single-allocation path must be BYTE-IDENTICAL to the old pad-then-wrap chain it replaced.
    /// That chain allocated a full-size copy per step, so a long dictation clip briefly held three or
    /// four copies of itself on the Large Object Heap. Collapsing it to one allocation is only safe if
    /// the output does not move by a single byte - and that is checked against
    /// <see cref="OriginalWrap"/>, an independent copy of the old writer, NOT against today's `Wrap`.
    /// </summary>
    [Theory]
    [InlineData(24000, 1, 16, 600)]   // the desktop dictation capture format
    [InlineData(44100, 2, 16, 137)]   // stereo, deliberately odd pad duration
    [InlineData(16000, 1, 16, 0)]     // no pad at all
    [InlineData(48000, 2, 16, 1000)]
    public void WrapWithTrailingSilence_MatchesTheOriginalWriter(int sampleRate, int channels, int bits, int padMs)
    {
        var pcm = new byte[3211];
        Random.Shared.NextBytes(pcm);

        var viaOriginal = OriginalWrap(
            PcmWav.WithTrailingSilence(pcm, sampleRate, channels, bits, padMs),
            sampleRate, channels, bits);
        var viaSingle = PcmWav.WrapWithTrailingSilence(pcm, sampleRate, channels, bits, padMs);

        Assert.Equal(viaOriginal, viaSingle);
    }

    [Fact]
    public void WrapWithTrailingSilence_EmptyPcm_MatchesTheOriginalWriter()
    {
        var pcm = Array.Empty<byte>();
        var viaOriginal = OriginalWrap(PcmWav.WithTrailingSilence(pcm, 24000, 1, 16, 600), 24000, 1, 16);
        var viaSingle = PcmWav.WrapWithTrailingSilence(pcm, 24000, 1, 16, 600);
        Assert.Equal(viaOriginal, viaSingle);
    }

    /// <summary>
    /// `Wrap` itself must still produce the original writer's bytes after being re-pointed at the
    /// new implementation. This is the test that would catch a wrong header, which the old circular
    /// comparison could not.
    /// </summary>
    [Theory]
    [InlineData(24000, 1, 16)]
    [InlineData(44100, 2, 16)]
    public void Wrap_StillMatchesTheOriginalWriter(int sampleRate, int channels, int bits)
    {
        var pcm = new byte[1777];
        Random.Shared.NextBytes(pcm);
        Assert.Equal(OriginalWrap(pcm, sampleRate, channels, bits), PcmWav.Wrap(pcm, sampleRate, channels, bits));
    }

    /// <summary>
    /// A clip whose WAV would exceed the maximum managed array must fail LOUDLY and predictably.
    /// Production no longer reaches this, but the contract is pinned
    /// anyway because the old code reached 2,147,483,615 bytes in the field, and the header plus the
    /// run-out pad push such a clip over `Int32.MaxValue`. Silent overflow into a negative length
    /// would produce a corrupt WAV rather than an error.
    /// </summary>
    /// <summary>
    /// The size guard must actually REFUSE, not merely exist. These call it directly with logical
    /// lengths, so the rejection is exercised without allocating two gigabytes - deleting the guard
    /// makes these fail, which the previous version of this test did not.
    /// </summary>
    [Fact]
    public void CheckedDataLength_RefusesAPayloadThatCannotFitOneArray()
    {
        // The exact size observed in the field, plus the run-out pad the transcription WAV adds.
        const long observedInTheField = 2_147_483_615;
        long pad = (long)24000 * 1 * 16 / 8 * PcmWav.TrailingSilenceMs / 1000;

        var ex = Assert.Throws<ArgumentException>(() => PcmWav.CheckedDataLength(observedInTheField, pad));
        Assert.Contains("must fit in a", ex.Message);
    }

    [Fact]
    public void CheckedDataLength_RefusesWhenOnlyThePadTipsItOver()
    {
        // A payload that fits on its own, but not once the run-out pad is added. This is the case the
        // old mixed checked/unchecked arithmetic could wrap into a negative length.
        Assert.Throws<ArgumentException>(() => PcmWav.CheckedDataLength(PcmWav.MaxWrappablePcmBytes, 1));
    }

    [Fact]
    public void CheckedDataLength_AcceptsTheLargestPayloadThatDoesFit()
    {
        Assert.Equal(PcmWav.MaxWrappablePcmBytes, PcmWav.CheckedDataLength(PcmWav.MaxWrappablePcmBytes, 0));
        Assert.Equal(1044, PcmWav.CheckedDataLength(1000, 44));
    }

    [Fact]
    public void CheckedDataLength_RefusesWithoutOverflowingOnHugeLongs()
    {
        // The guard must not compute pcmLength + padBytes to decide: that sum wraps negative for
        // large valid longs and would slip straight past an upper-bound comparison.
        Assert.Throws<ArgumentException>(() => PcmWav.CheckedDataLength(long.MaxValue, 1));
        Assert.Throws<ArgumentException>(() => PcmWav.CheckedDataLength(1, long.MaxValue));
        Assert.Throws<ArgumentException>(() => PcmWav.CheckedDataLength(long.MaxValue, long.MaxValue));
    }

    [Fact]
    public void MaxWrappablePcmBytes_LeavesRoomForTheHeader()
    {
        Assert.Equal(int.MaxValue - 44, PcmWav.MaxWrappablePcmBytes);
        Assert.True((long)PcmWav.MaxWrappablePcmBytes + 44 <= int.MaxValue);
    }

    [Fact]
    public void WrapWithTrailingSilence_PadBytesAreSilence()
    {
        var pcm = new byte[] { 1, 2, 3, 4 };
        var wav = PcmWav.WrapWithTrailingSilence(pcm, 24000, 1, 16, 600);
        // Everything after the header and the samples must be digital silence, not stale memory.
        for (int i = 44 + pcm.Length; i < wav.Length; i++)
            Assert.Equal(0, wav[i]);
        Assert.True(wav.Length > 44 + pcm.Length, "a positive pad duration must actually append bytes");
    }

    [Fact]
    public void Wrap_StillMatchesItsOwnHeader_AfterRefactor()
    {
        // Wrap() now delegates to the single-allocation path with a zero pad. Its output must not
        // have changed for any existing caller.
        var pcm = new byte[] { 10, 20, 30, 40, 50, 60 };
        var wav = PcmWav.Wrap(pcm, 24000, 1, 16);
        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal((byte)'F', wav[2]);
        Assert.Equal((byte)'F', wav[3]);
        Assert.Equal(pcm, wav.Skip(44).ToArray());
    }
}
