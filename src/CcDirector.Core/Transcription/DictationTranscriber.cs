using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Transcription;

/// <summary>
/// Transcribes a saved WAV blob through the ONE shared batch pipeline
/// (<see cref="BatchTranscriptionPipeline"/>), resolving the user-selected method and dictation
/// dictionary exactly as the live desktop recorder does, then applying the validated dictionary
/// corrector only. This is the transcription half of the desktop dictation path split out so it can run
/// against a clip read back from disk (issue #1130): the durable delivery loop transcribes here, with
/// no microphone and no dialog, on a live retry or a next-launch re-drive.
///
/// Failures are surfaced as typed exceptions the delivery loop classifies:
/// <see cref="TranscriptionUnavailableException"/> (no method configured),
/// <see cref="InsufficientCreditsException"/> (402), <see cref="TranscriptionFailedException"/>
/// (provider HTTP error with its status code), or a raw network exception.
/// </summary>
public sealed class DictationTranscriber : IDictationTranscriber
{
    private readonly OpenAiKeyResolver _keyResolver;
    private readonly DictionaryResolver _dictionaryResolver;
    private readonly string? _cleanupModel;
    private readonly string _profile;

    public DictationTranscriber(AgentOptions options, string profile = "default")
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        // Same resolution the live recorder uses: the Gateway vault / routing when attached, the local
        // key vault and dictionary cache when standalone.
        _keyResolver = new OpenAiKeyResolver();
        _dictionaryResolver = new DictionaryResolver(options);
        _cleanupModel = options.DictationCleanupModel;
        _profile = string.IsNullOrWhiteSpace(profile) ? "default" : profile;
    }

    public async Task<DictationTranscript> TranscribeAsync(byte[] wav, CancellationToken ct = default)
    {
        if (wav is null || wav.Length == 0)
            throw new ArgumentException("wav is empty", nameof(wav));

        var routing = await _keyResolver.ResolveEndpointAsync(ct);
        if (routing is null)
            throw new TranscriptionUnavailableException(_keyResolver.UnavailableMessage);

        var dictionary = await _dictionaryResolver.ResolveAsync(ct);

        using var pipeline = new BatchTranscriptionPipeline(cleanupModel: _cleanupModel);
        var batch = await pipeline.TranscribeAsync(wav, "dictation.wav", routing, dictionary, _profile, ct);

        FileLog.Write($"[DictationTranscriber] transcribed: rawLen={batch.RawTranscript.Length}, "
            + $"correctedLen={batch.CorrectedTranscript.Length}, changed={batch.ChangedWords.Count}, "
            + $"mode={routing.Mode.ToConfigString()}, model={routing.Model}");

        return new DictationTranscript(
            RawTranscript: batch.RawTranscript,
            CleanedTranscript: batch.CorrectedTranscript,
            DictionaryWordsCorrected: batch.ChangedWords.Count);
    }
}
