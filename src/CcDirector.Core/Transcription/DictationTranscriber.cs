using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Transcription;

/// <summary>
/// Transcribes a saved WAV blob through the Gateway transcription job protocol. This is the
/// transcription half of the desktop dictation path split out so it can run against a clip read back
/// from disk (issue #1130): the durable delivery loop transcribes here, with no microphone and no
/// dialog, on a live retry or a next-launch re-drive.
///
/// Failures are surfaced as typed exceptions the delivery loop classifies:
/// <see cref="TranscriptionUnavailableException"/> (no method configured),
/// <see cref="InsufficientCreditsException"/> (402), <see cref="TranscriptionFailedException"/>
/// (provider HTTP error with its status code), or a raw network exception.
/// </summary>
public sealed class DictationTranscriber : IDictationTranscriber
{
    private readonly string _profile;

    public DictationTranscriber(AgentOptions options, string profile = "default")
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        _profile = string.IsNullOrWhiteSpace(profile) ? "default" : profile;
    }

    public async Task<DictationTranscript> TranscribeAsync(byte[] wav, CancellationToken ct = default)
    {
        if (wav is null || wav.Length == 0)
            throw new ArgumentException("wav is empty", nameof(wav));

        var batch = await new GatewayTranscriptionJobClient().TranscribeAsync(
            wav,
            "dictation.wav",
            "audio/wav",
            applyCorrection: true,
            ct);

        FileLog.Write($"[DictationTranscriber] transcribed through Gateway: job={batch.JobId}, rawLen={batch.RawTranscript.Length}, "
            + $"correctedLen={batch.CleanedTranscript.Length}, profile={_profile}, model={batch.Model}");

        return new DictationTranscript(
            RawTranscript: batch.RawTranscript,
            CleanedTranscript: batch.CleanedTranscript,
            DictionaryWordsCorrected: 0);
    }
}
