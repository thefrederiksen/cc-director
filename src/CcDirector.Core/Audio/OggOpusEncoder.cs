using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;

namespace CcDirector.Core.Audio;

/// <summary>
/// Encodes raw little-endian PCM16 into a complete Ogg-framed Opus file (issue #896).
///
/// Why this exists: desktop dictation captured uncompressed PCM and, until #896, wrapped the whole
/// clip in a WAV before its single upload. That is about 48 kilobytes per second at 24 kilohertz, so
/// any remote-mode dictation past roughly ninety seconds exceeded the hosted transcription service's
/// fixed four and a half megabyte request-body limit (the platform edge returns
/// FUNCTION_PAYLOAD_TOO_LARGE before our own service code runs). Opus at a voice bitrate turns the
/// same two minutes into a few hundred kilobytes, well under the limit, so long dictations transcribe
/// instead of failing with 413.
///
/// This is the ENCODE direction only. The Gateway's local Whisper branch will need the DECODE
/// direction (Opus back to PCM) when the mobile and Gateway path stops transcoding on the phone (a
/// separate follow-up); that is deliberately NOT built here because this change has no caller for it.
/// The class is named for the codec, not the direction, so the decoder can join it without a rename.
///
/// Container note: this writes Ogg-framed Opus (a .ogg file). Browser MediaRecorder emits Opus inside
/// a WebM container instead, so a future decode path that must accept phone audio needs a WebM
/// demultiplexer, not just Ogg page parsing - the audio boundary is intentionally container-aware,
/// never "Opus means Ogg".
///
/// Uses Concentus (pure managed, no native library) so it needs no extra platform binary.
/// </summary>
public static class OggOpusEncoder
{
    /// <summary>
    /// The Opus target bitrate for dictation speech. Twenty-four thousand bits per second is
    /// transparent for a single mono voice and gives large headroom under the four and a half
    /// megabyte cap (over twenty minutes of speech), while keeping transcription accuracy
    /// indistinguishable from the raw capture.
    /// </summary>
    public const int VoiceBitrateBitsPerSecond = 24_000;

    /// <summary>
    /// Encode raw PCM16 samples into a complete Ogg Opus file the transcription endpoints decode
    /// directly. The sample rate must be an Opus-native rate (8000, 12000, 16000, 24000, or 48000
    /// hertz); the desktop microphone's 24000 hertz is one, so no resampling happens.
    /// </summary>
    /// <param name="pcm">Raw little-endian signed 16-bit PCM sample bytes (no header). Length must be even.</param>
    /// <param name="sampleRate">Samples per second - an Opus-native rate.</param>
    /// <param name="channels">Channel count (1 for the dictation microphone).</param>
    /// <param name="bitrateBitsPerSecond">Opus target bitrate; defaults to <see cref="VoiceBitrateBitsPerSecond"/>.</param>
    public static byte[] EncodePcm16(byte[] pcm, int sampleRate, int channels, int bitrateBitsPerSecond = VoiceBitrateBitsPerSecond)
    {
        if (pcm is null) throw new ArgumentNullException(nameof(pcm));
        if ((pcm.Length & 1) != 0)
            throw new ArgumentException("PCM16 byte length must be even (two bytes per sample).", nameof(pcm));

        // Bytes to short samples. The desktop mic delivers little-endian PCM16, which is the native
        // memory layout on win-x64, so a straight block copy is the correct reinterpretation.
        var samples = new short[pcm.Length / 2];
        Buffer.BlockCopy(pcm, 0, samples, 0, pcm.Length);

        var encoder = OpusCodecFactory.CreateEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = bitrateBitsPerSecond;

        using var ms = new MemoryStream();
        // inputSampleRate equals the encoder sample rate: the capture is already an Opus-native rate,
        // so the writer's resampler is a no-op. OpusOggWriteStream owns all Opus framing and Ogg
        // paging; Finish() pads and flushes the final partial frame so the tail of speech is never
        // clipped. leaveOpen keeps the MemoryStream ours to read via ToArray after Finish.
        var ogg = new OpusOggWriteStream(encoder, ms, fileTags: null, inputSampleRate: sampleRate, leaveOpen: true);
        ogg.WriteSamples(samples, 0, samples.Length);
        ogg.Finish();
        return ms.ToArray();
    }
}
