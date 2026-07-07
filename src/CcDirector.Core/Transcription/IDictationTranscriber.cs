namespace CcDirector.Core.Transcription;

/// <summary>
/// Transcribes a complete WAV audio blob into text, applying the dictation dictionary corrector only
/// (never a free-text reword). This is the seam the durable dictation delivery loop (issue #1130) uses
/// so it can transcribe a clip read back from disk - during a live retry or a next-launch re-drive -
/// without any microphone or dialog. Tests supply a fake to drive the retry/keep/delete logic with no
/// network.
/// </summary>
public interface IDictationTranscriber
{
    /// <summary>
    /// Transcribe the WAV bytes. Throws on failure - never returns a partial or empty result to paper
    /// over a provider error: <see cref="InsufficientCreditsException"/> for 402,
    /// <see cref="TranscriptionUnavailableException"/> when no method is configured,
    /// <see cref="TranscriptionFailedException"/> for a provider HTTP error (carrying the status so the
    /// caller can tell transient from permanent), or a network exception when the call could not be made.
    /// </summary>
    Task<DictationTranscript> TranscribeAsync(byte[] wav, CancellationToken ct = default);
}

/// <summary>
/// The text of one transcribed dictation: the raw transcript, the dictionary-corrected transcript, and
/// how many dictionary terms were swapped. <see cref="CleanedTranscript"/> equals
/// <see cref="RawTranscript"/> byte-for-byte when no dictionary term matched.
/// </summary>
public sealed record DictationTranscript(string RawTranscript, string CleanedTranscript, int DictionaryWordsCorrected);
