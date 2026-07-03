using System.Text;
using CcDirector.Core.Audio;
using Concentus;
using Concentus.Oggfile;
using Xunit;

namespace CcDirector.Core.Tests.Audio;

/// <summary>
/// Tests for <see cref="OggOpusEncoder"/> (issue #896): the desktop dictation compressor that keeps a
/// long remote-mode upload under the hosted endpoint's four and a half megabyte body limit.
/// </summary>
public sealed class OggOpusEncoderTests
{
    private const int SampleRate = 24_000; // MicAudioCapture.SampleRate - an Opus-native rate
    private const int Channels = 1;
    private const int BitsPerSample = 16;

    // A synthetic mono PCM16 sine, `seconds` long, so the tests exercise real audio without a mic.
    private static byte[] SinePcm16(double seconds, double freqHz = 220.0)
    {
        int totalSamples = (int)(SampleRate * seconds);
        var pcm = new byte[totalSamples * 2];
        for (int i = 0; i < totalSamples; i++)
        {
            double t = (double)i / SampleRate;
            short s = (short)(Math.Sin(2 * Math.PI * freqHz * t) * 12000);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return pcm;
    }

    [Fact]
    public void EncodePcm16_ProducesOggOpusContainer()
    {
        var pcm = SinePcm16(1.0);

        var ogg = OggOpusEncoder.EncodePcm16(pcm, SampleRate, Channels);

        Assert.True(ogg.Length > 0);
        // The Ogg capture pattern begins the first page.
        Assert.Equal("OggS", Encoding.ASCII.GetString(ogg, 0, 4));
        // The Opus identification header magic ("OpusHead") sits in that first page's payload; its
        // presence proves this is a well-formed Ogg Opus stream, not just any Ogg.
        Assert.Contains("OpusHead", Encoding.ASCII.GetString(ogg));
    }

    [Fact]
    public void EncodePcm16_IsFarSmallerThanWav_GivingHeadroomUnderTheFourAndAHalfMegabyteCap()
    {
        // Three minutes of 24 kHz mono PCM16 is about 8.6 megabytes as WAV - well over the hosted
        // endpoint's 4.5 megabyte body limit, which is exactly the #896 failure. Opus must bring the
        // same clip comfortably under it.
        var pcm = SinePcm16(180.0);
        var wav = PcmWav.Wrap(pcm, SampleRate, Channels, BitsPerSample);

        var ogg = OggOpusEncoder.EncodePcm16(pcm, SampleRate, Channels);

        const int cap = 4_500_000;
        Assert.True(wav.Length > cap, $"WAV should exceed the cap to reproduce #896 (was {wav.Length})");
        Assert.True(ogg.Length < cap, $"Ogg Opus must be under the cap (was {ogg.Length})");
        // Voice Opus should be at least ten times smaller than WAV for this clip.
        Assert.True(ogg.Length * 10 < wav.Length, $"Opus not compressing as expected: ogg={ogg.Length}, wav={wav.Length}");
    }

    [Fact]
    public void EncodePcm16_RoundTripsThroughAnOpusDecoder_ProvingTheOggIsValid()
    {
        var pcm = SinePcm16(2.0);
        long inputSamples = pcm.Length / 2;

        var ogg = OggOpusEncoder.EncodePcm16(pcm, SampleRate, Channels);

        // Decode the produced .ogg back to PCM with an independent Opus decoder. That it decodes at
        // all, to roughly the original duration, proves the encoder emits a genuinely valid Ogg Opus
        // stream - stronger than a magic-byte check. (Opus adds a small pre-skip and pads the final
        // frame with silence, so the decoded length is close to, not exactly, the input.)
        var decoder = OpusCodecFactory.CreateDecoder(SampleRate, Channels);
        using var ms = new MemoryStream(ogg);
        var reader = new OpusOggReadStream(decoder, ms);
        long decodedSamples = 0;
        while (reader.HasNextPacket)
        {
            short[]? packet = reader.DecodeNextPacket();
            if (packet != null) decodedSamples += packet.Length;
        }

        Assert.True(decodedSamples >= inputSamples * 0.9, $"decoded too short: {decodedSamples} vs input {inputSamples}");
        Assert.True(decodedSamples <= inputSamples * 1.2, $"decoded too long: {decodedSamples} vs input {inputSamples}");
    }

    [Fact]
    public void EncodePcm16_WhiteNoiseThreeMinutes_StillUnderTheCap()
    {
        // White noise is the incompressible worst case (a stricter proxy than a sine). Opus targets a
        // bitrate rather than a compression ratio, so even here three minutes must stay under the cap.
        var rng = new Random(20260702);
        var pcm = new byte[SampleRate * 180 * 2];
        rng.NextBytes(pcm);

        var ogg = OggOpusEncoder.EncodePcm16(pcm, SampleRate, Channels);

        Assert.True(ogg.Length < 4_500_000, $"white-noise Opus must be under the cap (was {ogg.Length})");
    }

    [Fact]
    public void EncodePcm16_OddLength_Throws()
        => Assert.Throws<ArgumentException>(() => OggOpusEncoder.EncodePcm16(new byte[3], SampleRate, Channels));

    [Fact]
    public void EncodePcm16_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => OggOpusEncoder.EncodePcm16(null!, SampleRate, Channels));
}
