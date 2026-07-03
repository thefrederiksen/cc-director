using System.Net;
using System.Text;
using CcDirector.Core.Audio;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Transcription;
using Xunit;

namespace CcDirector.Core.Tests.Transcription;

/// <summary>
/// Proves the shared pipeline shrinks an oversize dictation upload under the hosted proxy's request
/// cap (issue #898): a whole-turn WAV that would trip Vercel's 4.5 MB <c>FUNCTION_PAYLOAD_TOO_LARGE</c>
/// is transcoded to OGG-Opus before the POST, while a short clip is still sent byte-for-byte as WAV.
/// </summary>
public sealed class BatchTranscriptionPipelineCompressionTests
{
    private const int SampleRate = 24_000;
    private const int PlatformBodyCapBytes = 4_500_000;

    // Captures the raw bytes of the multipart POST to /audio/transcriptions so the test can assert
    // exactly what would go over the wire (size + container), then returns a canned transcript.
    private sealed class RawBodyHandler : HttpMessageHandler
    {
        public byte[] Body { get; private set; } = Array.Empty<byte>();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri?.ToString() ?? "";
            if (url.EndsWith("/audio/transcriptions", StringComparison.Ordinal))
            {
                Body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"text\":\"ok\"}", Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static ResolvedTranscription DevThrottle() => new()
    {
        BaseUrl = TranscriptionEndpointResolver.DevThrottleBaseUrl,
        ApiKey = "dt_live_key",
        Transport = TranscriptionTransport.Batch,
        Model = TranscriptionEndpointResolver.DevThrottleModel,
        Mode = TranscriptionMode.DevThrottle,
    };

    private static byte[] SineWav(int seconds)
    {
        int samples = SampleRate * seconds;
        var pcm = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            short v = (short)(Math.Sin(2 * Math.PI * 220.0 * i / SampleRate) * 8000);
            pcm[i * 2] = (byte)(v & 0xFF);
            pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return PcmWav.Wrap(pcm, SampleRate, 1, 16);
    }

    private static bool Contains(byte[] haystack, string ascii)
    {
        var needle = Encoding.ASCII.GetBytes(ascii);
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { hit = false; break; }
            if (hit) return true;
        }
        return false;
    }

    [Fact]
    public async Task LargeWav_IsTranscodedToOggUnderTheCap_BeforePosting()
    {
        var wav = SineWav(seconds: 100);   // 4.8 MB WAV - over the ~90 s failure point
        Assert.True(wav.Length > PlatformBodyCapBytes, "precondition: the WAV must exceed the cap");

        var handler = new RawBodyHandler();
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));
        await pipeline.TranscribeAsync(wav, "dictation.wav", DevThrottle(), DictationDictionary.Empty);

        // The whole multipart body - what Vercel measures against 4.5 MB - is now well under the cap.
        Assert.True(handler.Body.Length < PlatformBodyCapBytes,
            $"posted body ({handler.Body.Length}) must be under the {PlatformBodyCapBytes}-byte cap");
        // It went out as OGG-Opus named .ogg, and the raw WAV ("RIFF") is gone.
        Assert.True(Contains(handler.Body, "OggS"), "body should carry an OGG-Opus stream");
        Assert.True(Contains(handler.Body, "dictation.ogg"), "the upload should be named .ogg");
        Assert.False(Contains(handler.Body, "RIFF"), "the raw WAV should have been replaced");
    }

    [Fact]
    public async Task ShortWav_IsSentUnchanged_AsWav()
    {
        var wav = SineWav(seconds: 2);     // ~96 KB WAV - well under the threshold
        Assert.True(wav.Length < PlatformBodyCapBytes);

        var handler = new RawBodyHandler();
        using var pipeline = new BatchTranscriptionPipeline(new HttpClient(handler));
        await pipeline.TranscribeAsync(wav, "dictation.wav", DevThrottle(), DictationDictionary.Empty);

        // No regression for the common short-clip case: still a WAV, still named .wav, no transcode.
        Assert.True(Contains(handler.Body, "RIFF"), "a short clip should be sent as-is (WAV)");
        Assert.True(Contains(handler.Body, "dictation.wav"), "a short clip keeps its .wav name");
        Assert.False(Contains(handler.Body, "OggS"), "a short clip should not be transcoded");
    }
}
