namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The Gateway's dictation PHASE label rule - which phase an inbound dictation is in, or null when the
/// session has no dictation worth painting. This is the single place that answers "should this session be
/// painted orange for a dictation right now?", and it is deliberately a pure function of three booleans so
/// the rule itself is testable without a running Gateway. <c>GatewayHost</c> supplies the facts; the
/// <c>SessionOrdering</c> fold turns a non-null result into the orange dot and the label.
///
/// DEFECT 19, fixed 14 July 2026 (mission "Session State Truth"). One flag was answering two questions:
///
///   1. "Is there an undelivered dictation for this session?" - a DURABLE fact. It must NEVER expire, or a
///      phone out of signal loses its words. That is <paramref name="undelivered"/>, and it is correct.
///   2. "Should this session be painted orange right now?" - a PRESENTATION question. It must ALWAYS be
///      bounded, or an upload that stopped paints the session orange indefinitely.
///
/// The colour used to be driven by the answer to question 1 alone, so any dictation that reached no
/// terminal state left its session orange forever, reading "Uploading from phone" about an upload that was
/// not happening. OBSERVED (log correlation, 14 July 2026): upload f13cb4b6d9d0 stood undelivered for
/// 1 hour 30 minutes on 12 July 2026 - orange the whole time, across four Gateway restarts - while its
/// transcription failed repeatedly, before finally delivering 362 characters. Both halves of that matter:
/// the durable record was RIGHT to keep the words, and the colour was lying for ninety minutes.
///
/// So the record is untouched and <paramref name="progressing"/> bounds the colour instead: the phone
/// refreshes its progress mark on every stored chunk and every completion attempt, so a genuinely slow
/// upload keeps its label and is never cut short, while one that goes quiet drops back to the session's
/// true colour within the idle window. The dictation still delivers whenever the phone returns - and a
/// delivery submits text, which makes the agent work, which is blue. Nothing is lost except the lie.
/// </summary>
internal static class DictationPhase
{
    /// <summary>The phone is still sending the audio up.</summary>
    public const string Uploading = "Uploading from phone";

    /// <summary>The audio is up and the server is turning it into text.</summary>
    public const string Transcribing = "Transcribing";

    /// <summary>
    /// The phase label for a session, or null when no dictation should paint it.
    /// </summary>
    /// <param name="activelyTranscribing">A transcription run is in flight for this session (bounded by the
    /// run itself, cleared in its finally). Wins outright: it is the most specific true statement available.</param>
    /// <param name="undelivered">A durable PENDING dictation record stands for this session. NEVER expires -
    /// this is the fact that must not be lost, not the one that may paint.</param>
    /// <param name="progressing">The upload has made progress within the idle window (a chunk stored, a
    /// completion attempted). This is what BOUNDS the colour: undelivered alone must never paint, because
    /// "undelivered" is durable and unbounded by design.</param>
    public static string? For(bool activelyTranscribing, bool undelivered, bool progressing)
        => activelyTranscribing ? Transcribing
        // NOTE the AND. Dropping `progressing` here restores defect 19 exactly: an undelivered record is
        // durable and unbounded, so it would paint until the record reached a terminal state - which, for
        // the paths that reach none, is never. If a test ever asks you to remove it, that test is the bug.
        : undelivered && progressing ? Uploading
        : null;
}
