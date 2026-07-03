using System.Text;
using CcDirector.ControlApi;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves the web/Cockpit dictation path packages its upload as Ogg Opus (issue #898). The desktop
/// path was fixed in #897, but the WebSocket endpoint still shipped raw WAV and hit the same platform
/// 413 past ~90 s. This tests the packaging decision (<see cref="DictationEndpoint.PackageUpload"/>)
/// directly, without standing up the WebSocket loop, so it needs no network or real key.
/// </summary>
public sealed class DictationEndpointPackagingTests
{
    private const int SampleRate = 24_000;   // the browser capture format
    private const int Channels = 1;
    private const int PlatformBodyCapBytes = 4_500_000;

    /// <summary>Raw PCM16 for a low-amplitude sine tone of the given duration - non-degenerate audio.</summary>
    private static byte[] Pcm16(int seconds)
    {
        int samples = SampleRate * seconds;
        var pcm = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            short v = (short)(Math.Sin(2 * Math.PI * 220.0 * i / SampleRate) * 8000);
            pcm[i * 2] = (byte)(v & 0xFF);
            pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return pcm;
    }

    [Fact]
    public void PackageUpload_LongClip_IsOggOpusFarUnderTheCap()
    {
        // 100 seconds of raw PCM16/24k/mono is a 4.8 MB WAV - past the ~90 s point where the old
        // uncompressed web upload failed with FUNCTION_PAYLOAD_TOO_LARGE.
        var pcm = Pcm16(seconds: 100);
        long equivalentWavBytes = pcm.Length + 44;
        Assert.True(equivalentWavBytes > PlatformBodyCapBytes, "precondition: the raw WAV would exceed the cap");

        var (audio, fileName) = DictationEndpoint.PackageUpload(pcm, SampleRate, Channels);

        Assert.Equal("dictation.ogg", fileName);
        Assert.Equal("OggS", Encoding.ASCII.GetString(audio, 0, 4));    // real Ogg capture pattern
        Assert.True(audio.Length < PlatformBodyCapBytes,
            $"packaged upload ({audio.Length}) must be under the {PlatformBodyCapBytes}-byte cap");
        Assert.True(audio.Length < equivalentWavBytes / 4,
            $"expected >4x reduction vs WAV; got {equivalentWavBytes} -> {audio.Length}");
    }
}
