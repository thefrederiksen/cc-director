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
}
