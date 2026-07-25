namespace CcDirector.Core.Sessions;

/// <summary>
/// What actually happened when a session was told to compact (issue #2150). Every field is a fact the
/// caller would otherwise have to assume: whether the command reached the tool, whether the tool was
/// seen to FINISH, how long that took, and whether the follow-up prompt went out. A caller reading
/// only "accepted" would call a compaction that never completed a success.
/// </summary>
/// <param name="Submitted">The compaction command reached the tool's composer.</param>
/// <param name="CompactionObserved">The tool's own records confirm a compaction finished after we asked
/// for one. False when the driver cannot report completion - which is a stated limit, not a failure.</param>
/// <param name="WaitedSeconds">Seconds between submitting the command and observing it finish; 0 when
/// completion was not watched.</param>
/// <param name="Continued">The follow-up prompt was submitted after the compaction finished.</param>
/// <param name="Detail">One plain-English sentence for a person or an agent reading the result.</param>
public sealed record CompactContextOutcome(
    bool Submitted,
    bool CompactionObserved,
    double WaitedSeconds,
    bool Continued,
    string Detail);
