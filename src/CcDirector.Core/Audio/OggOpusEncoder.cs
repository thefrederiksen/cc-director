using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;

namespace CcDirector.Core.Audio;

/// <summary>
/// Encodes raw PCM16 into an OGG-Opus container so long dictation clips fit under the
/// hosted proxy's request-body limit (issue #898).
///
/// Uncompressed PCM16 at 24 kHz mono is 48 KB/s, so a whole-turn WAV crosses the
/// platform's 4.5 MB serverless request-body cap at ~90 seconds and the upload is
/// rejected (Vercel <c>FUNCTION_PAYLOAD_TOO_LARGE</c>) before the proxy code runs.
/// Opus at ~24 kbps is ~10x smaller and speech-transparent, turning that ~90 s ceiling
/// into ~25 minutes while the OpenAI-compatible endpoints (OpenAI and the DevThrottle
/// proxy) decode <c>audio/ogg</c> natively. This is applied only to the remote batch
/// POST (see <see cref="Transcription.BatchTranscriptionPipeline"/>); on-device Whisper
/// keeps raw WAV because it decodes PCM directly and has no network body limit.
/// </summary>
public static class OggOpusEncoder
{
    /// <summary>
    /// Target Opus bitrate for mono speech. 24 kbps is well above telephone quality and
    /// transparent for speech recognition, while keeping the ~25-minute headroom that makes
    /// the 4.5 MB cap a non-issue for any realistic dictation turn.
    /// </summary>
    public const int BitrateBitsPerSecond = 24_000;

    /// <summary>
    /// Encode little-endian PCM16 samples to an OGG-Opus file. The sample rate must be one
    /// Opus supports natively (8/12/16/24/48 kHz); dictation is 24 kHz mono.
    /// </summary>
    /// <param name="pcm">Raw little-endian PCM16 sample bytes (no container header).</param>
    /// <param name="sampleRate">Samples per second (e.g. 24000).</param>
    /// <param name="channels">Channel count (1 = mono, 2 = stereo).</param>
    /// <returns>A complete <c>.ogg</c> (OGG-Opus) file the transcription endpoint can decode.</returns>
    public static byte[] EncodePcm16(byte[] pcm, int sampleRate, int channels)
    {
        if (pcm is null) throw new ArgumentNullException(nameof(pcm));
        if (channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Opus supports 1 or 2 channels.");

        // PCM16 is 2 bytes per sample; drop a dangling odd byte rather than read past the buffer.
        var samples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, samples, 0, samples.Length * 2);

        var encoder = OpusEncoder.Create(sampleRate, channels, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = BitrateBitsPerSecond;

        using var ogg = new MemoryStream();
        // OpusOggWriteStream packages the samples into Opus frames and writes the OGG pages;
        // WriteSamples accepts an arbitrary sample count and buffers internally. Finish flushes
        // the trailing partial frame and finalizes the stream.
        var oggStream = new OpusOggWriteStream(encoder, ogg, null, sampleRate);
        oggStream.WriteSamples(samples, 0, samples.Length);
        oggStream.Finish();
        return ogg.ToArray();
    }
}
