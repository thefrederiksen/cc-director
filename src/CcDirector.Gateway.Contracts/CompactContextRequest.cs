namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The payload for the <c>compact-context</c> verb (POST /sessions/{sid}/compact-context), issue #2150:
/// summarize a session's conversation in place and, when a continuation is given, send it once the
/// compaction has actually finished.
/// </summary>
public sealed class CompactContextRequest
{
    /// <summary>
    /// The text to submit once the compaction has finished, or blank/absent to compact only. The wait is
    /// on the tool's own completion signal, so a driver that cannot report completion refuses this
    /// outright rather than firing the prompt at a guessed moment.
    /// </summary>
    public string? ContinuePrompt { get; set; }
}

/// <summary>
/// What the <c>compact-context</c> verb answers. It reports the FINISH, not just the submission: a caller
/// that only knew "accepted" could not tell a completed compaction from one that never landed.
/// </summary>
public sealed class CompactContextResponse
{
    /// <summary>The compaction command reached the tool.</summary>
    public bool Submitted { get; set; }

    /// <summary>The tool's own records confirm the compaction finished.</summary>
    public bool CompactionObserved { get; set; }

    /// <summary>Seconds between submitting the command and seeing it finish; 0 when not watched.</summary>
    public double WaitedSeconds { get; set; }

    /// <summary>The follow-up prompt was submitted after the compaction finished.</summary>
    public bool Continued { get; set; }

    /// <summary>One plain-English sentence describing what happened.</summary>
    public string Detail { get; set; } = "";
}
