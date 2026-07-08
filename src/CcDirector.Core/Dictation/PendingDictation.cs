namespace CcDirector.Core.Dictation;

/// <summary>
/// Whether a saved dictation is eligible for automatic background retry, or is parked waiting for the
/// user to do something before a retry can possibly succeed.
/// </summary>
public enum PendingDictationStatus
{
    /// <summary>Eligible for automatic retry - the last failure was transient (a slow or briefly
    /// unavailable transcription service, or a session that was not on screen yet).</summary>
    Pending,

    /// <summary>Parked: the last failure needs the user to act before a retry can succeed (out of
    /// credits, or no transcription key set). The background sweeper skips these so it does not hammer a
    /// call that will keep failing; the next Director launch promotes them back to
    /// <see cref="Pending"/> to try once more in case the user has since fixed it.</summary>
    NeedsAttention,

    /// <summary>Parked: a terminal submit probe already typed this dictation into the target session's
    /// composer and it never echoed the text back, so the composer is not accepting input right now (the
    /// session is starting up or wedged). Re-typing would only stack another unsubmitted copy, so the
    /// sweeper skips a clip in this state instead of re-typing it every pass (the issue #1135 pile-up).
    /// The audio is kept; the next Director launch promotes it back to <see cref="Pending"/> for one more
    /// probe once the session has been recreated. Distinct from <see cref="NeedsAttention"/> because the
    /// blocker is the session's composer, not the transcription account, so the user notice differs.</summary>
    ComposerBlocked,
}

/// <summary>
/// One recorded-but-not-yet-delivered desktop dictation, saved durably on disk the instant Send is
/// pressed (issue #1130). The recorded audio lives next to this record as a WAV file; this is its
/// metadata sidecar. A record exists only while its audio has not yet been transcribed and delivered
/// into its session - it is deleted the moment delivery succeeds. Field names are stable (the JSON
/// sidecar is read back on the next launch); add optional fields rather than renaming.
/// </summary>
public sealed record PendingDictation
{
    /// <summary>Stable id; also the WAV/sidecar file stem. A GUID "N" string.</summary>
    public required string Id { get; init; }

    /// <summary>The session this dictation is being delivered into (the desktop Session.Id, a GUID string).</summary>
    public required string SessionId { get; init; }

    /// <summary>Already-transcribed text from earlier Pause/Resume segments, joined ahead of this
    /// clip's transcript to form the full dictation. Empty in the common "just talk and Send" case.</summary>
    public required string Prefix { get; init; }

    /// <summary>Text the user had typed BEFORE the caret when Send was pressed; the dictation is inserted
    /// at the caret inside it. Empty in the common "just talk and Send" case. Persisted so a background
    /// retry reproduces the exact composed turn, never dropping the typed part.</summary>
    public string Before { get; init; } = "";

    /// <summary>Text the user had typed AFTER the caret when Send was pressed. See <see cref="Before"/>.</summary>
    public string After { get; init; } = "";

    /// <summary>When the audio was recorded (ISO 8601 UTC), for the stale-clip prune.</summary>
    public required string CreatedUtc { get; init; }

    /// <summary>How many delivery attempts have failed so far. Purely diagnostic.</summary>
    public int AttemptCount { get; init; }

    /// <summary>The most recent failure message, for diagnostics and the log. Null before the first failure.</summary>
    public string? LastError { get; init; }

    /// <summary>Whether this clip is eligible for automatic retry (see <see cref="PendingDictationStatus"/>).</summary>
    public PendingDictationStatus Status { get; init; } = PendingDictationStatus.Pending;
}
