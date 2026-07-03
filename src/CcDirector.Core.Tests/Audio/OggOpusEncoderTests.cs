using System.Text;
using CcDirector.Core.Audio;
using Xunit;

namespace CcDirector.Core.Tests.Audio;

/// <summary>
/// Proves the OGG-Opus transcode that keeps long dictations under the hosted proxy's 4.5 MB
/// request-body cap (issue #898): a whole-turn PCM16 WAV that would exceed the cap encodes to an OGG
/// blob an order of magnitude smaller, and the WAV reader round-trips the format it was written with.
/// </summary>
public sealed class OggOpusEncoderTests
{
    private const int SampleRate = 24_000;   // dictation capture format
    private const int Channels = 1;
    private const int BitsPerSample = 16;

    // Vercel serverless request-body cap. A whole-turn WAV crosses this at ~90 s; the Opus output
    // must sit far below it. (proxy: docs/architecture/transcription-request-path.md s7)
    private const int PlatformBodyCapBytes = 4_500_000;

    /// <summary>Raw PCM16 for a low-amplitude sine tone of the given duration - non-degenerate audio.</summary>
    private static byte[] MakePcm16(int seconds)
    {
        int samples = SampleRate * seconds;
        var pcm = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / SampleRate;
            short v = (short)(Math.Sin(2 * Math.PI * 220.0 * t) * 8000);
            pcm[i * 2] = (byte)(v & 0xFF);
            pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return pcm;
    }

    [Fact]
    public void EncodePcm16_LongClipThatWouldExceedTheCap_ProducesOggFarUnderIt()
    {
        // 100 seconds of raw PCM16/24k/mono = 4.8 MB WAV - over the ~90 s / 4.5 MB failure point.
        var pcm = MakePcm16(seconds: 100);
        var wav = PcmWav.Wrap(pcm, SampleRate, Channels, BitsPerSample);
        Assert.True(wav.Length > PlatformBodyCapBytes,
            $"precondition: the WAV ({wav.Length}) must exceed the cap to model the failure");

        var ogg = OggOpusEncoder.EncodePcm16(pcm, SampleRate, Channels);

        // The whole point: the compressed upload is comfortably under the platform cap.
        Assert.True(ogg.Length < PlatformBodyCapBytes,
            $"OGG-Opus ({ogg.Length}) must be under the {PlatformBodyCapBytes}-byte cap");
        // Sanity: a real OGG stream starts with the "OggS" capture pattern.
        Assert.Equal("OggS", Encoding.ASCII.GetString(ogg, 0, 4));
        // And it is a genuine reduction, not a no-op.
        Assert.True(ogg.Length < wav.Length / 4, $"expected >4x reduction; got {wav.Length} -> {ogg.Length}");
    }

    [Fact]
    public void TryReadPcm16_RoundTripsWhatWrapWrote()
    {
        var pcm = MakePcm16(seconds: 1);
        var wav = PcmWav.Wrap(pcm, SampleRate, Channels, BitsPerSample);

        Assert.True(PcmWav.TryReadPcm16(wav, out var readPcm, out var rate, out var channels));
        Assert.Equal(SampleRate, rate);
        Assert.Equal(Channels, channels);
        Assert.Equal(pcm, readPcm);
    }

    [Fact]
    public void TryReadPcm16_RejectsNonWavBytes()
    {
        // An already-compressed container (fake OGG header) is not PCM16 WAV and must be left alone.
        var notWav = Encoding.ASCII.GetBytes("OggS").Concat(new byte[64]).ToArray();
        Assert.False(PcmWav.TryReadPcm16(notWav, out _, out _, out _));
    }
}
