using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Transcription;

/// <summary>
/// Transcribes a saved WAV blob through the Gateway transcription owner. This is the transcription
/// half of the desktop dictation path split out so it can run against a clip read back from disk
/// (issue #1130): the durable delivery loop transcribes here, with no microphone and no dialog, on a
/// live retry or a next-launch re-drive.
///
/// Failures are surfaced as typed exceptions the delivery loop classifies:
/// <see cref="TranscriptionUnavailableException"/> (no method configured),
/// <see cref="InsufficientCreditsException"/> (402), <see cref="TranscriptionFailedException"/>
/// (provider HTTP error with its status code), or a raw network exception.
/// </summary>
public sealed class DictationTranscriber : IDictationTranscriber
{
    private readonly string _profile;
    private readonly GatewayTranscriptionClient _gateway;

    public DictationTranscriber(AgentOptions options, string profile = "default")
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        _profile = string.IsNullOrWhiteSpace(profile) ? "default" : profile;
        _gateway = new GatewayTranscriptionClient();
    }

    public async Task<DictationTranscript> TranscribeAsync(byte[] wav, CancellationToken ct = default)
    {
        if (wav is null || wav.Length == 0)
            throw new ArgumentException("wav is empty", nameof(wav));

        var transcript = await _gateway.TranscribeAsync(wav, "dictation.wav", "audio/wav", applyCorrection: true, ct);

        FileLog.Write($"[DictationTranscriber] transcribed via Gateway: len={transcript.Text.Length}, "
            + $"profile={_profile}, mode={transcript.Mode}, model={transcript.Model}");

        return new DictationTranscript(
            RawTranscript: transcript.Text,
            CleanedTranscript: transcript.Text,
            DictionaryWordsCorrected: 0);
    }
}
