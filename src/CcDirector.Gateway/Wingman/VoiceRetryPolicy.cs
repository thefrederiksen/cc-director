namespace CcDirector.Gateway.Wingman;

/// <summary>
/// One session's record of AUTOMATIC narration attempts that produced no audio: how many, and when the
/// last one finished. In memory only - a Gateway restart starts the count again, exactly as the
/// <see cref="VoiceWaitingClock"/> beside it starts its clock again.
/// </summary>
/// <param name="Count">Attempts so far, for the turn named by <paramref name="TurnKey"/>, that ended with no
/// playable audio.</param>
/// <param name="LastAttemptUtc">When the most recent of those attempts finished.</param>
/// <param name="TurnKey">WHICH turn these attempts were made for - a digest of the reply being narrated.
///
/// Keyed on the turn's own identity rather than reset by an event, deliberately. The obvious design resets
/// the count when the session goes back to Working, and that edge is observed on a sampled boundary which a
/// quick turn can slip through entirely; a spent schedule would then be inherited by a NEW turn, whose
/// narration would never be attempted and whose screen would offer a button for a reply that had never been
/// tried once. This repository has been bitten by exactly that shape before - the bare has-audio guard of
/// issue #1322, fixed by comparing the reply text instead of trusting the transition. Same fix, same reason:
/// a different reply is a different turn, whatever the Gateway did or did not observe in between.</param>
public readonly record struct VoiceAttempts(int Count, DateTime LastAttemptUtc, string TurnKey);

/// <summary>
/// THE schedule on which the Gateway retries a narration by itself, and the point past which it stops
/// and hands the person a button instead.
///
/// The owner set the shape of this on 1 September 2026, looking at a phone that read "Voice did not
/// arrive after 19m" with nothing to press: try again first, a few times, with minutes between the
/// tries - and when that has not worked, put a button on the screen so the person can ask for the voice
/// themselves. Before this the sweep retried a failed session every 45 seconds forever, the screen said
/// "the Gateway is still trying" forever, and the button was deliberately withheld on the argument that
/// it would re-run what had already failed. That argument is right for the first minute and wrong at
/// nineteen: by then the automatic path has had its chance, and the one thing the reader wants is a way
/// to try once more on purpose.
///
/// Both numbers live HERE and nowhere else. The sweep reads them to decide whether a session is due, and
/// <see cref="VoiceDisplayFold"/> reads them to word the verdict ("2 of 5 tries") and to decide when the
/// button appears - so the schedule the screen describes is, by construction, the schedule being run.
/// </summary>
public static class VoiceRetryPolicy
{
    /// <summary>
    /// How long the sweep waits after a failed attempt before it tries that session again. Three minutes,
    /// the low end of the owner's "three to five": a transient fault (a rate limit, a slow model, a tunnel
    /// that was busy) is usually over well inside that, and a spacing longer than the give-up clock
    /// (<see cref="VoiceDisplayFold.GaveUpAfter"/>, also three minutes) would mean the first retry lands
    /// only after the screen had already turned red.
    /// </summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How many automatic attempts a turn gets before the Gateway stops trying on its own and the Voice
    /// screen offers the Generate button. Five, the high end of the owner's "three to five": with the
    /// first attempt at turn-end and the rest three minutes apart, the automatic phase lasts about twelve
    /// minutes, which is long enough to outlast any fault worth waiting out and short enough that the
    /// person is not staring at a red badge for an evening.
    /// </summary>
    public const int MaxAutomaticAttempts = 5;

    /// <summary>
    /// How long a SPENT schedule is taken on trust before one more read is allowed, purely to see whether the
    /// turn has changed.
    ///
    /// BOUNDED, NEVER PERMANENT, and for the same reason the terminal read verdict beside it is bounded. The
    /// only thing that starts a new turn's schedule is an attempt at that turn, and the only ungated attempt
    /// is the one the turn-end edge fires. That edge is observed on a sampled boundary and can be missed, or
    /// coalesced away while the previous turn's last attempt is still running - and if the sweep were also
    /// held back for ever, nothing left in the system could discover that the reply had changed. The session
    /// would sit silent with a button offering to re-narrate a turn that is no longer the current one.
    ///
    /// Ten minutes: long enough that a genuinely stuck turn is not being hammered, short enough that a
    /// stranded new turn is picked up before anyone is still looking at the screen wondering.
    ///
    /// This interval governs the UNNAMED ask only - the sweep, before it has read anything. The moment the
    /// caller knows which turn it is holding it asks again by name, and a spent schedule for that same turn
    /// refuses it outright. That is what makes the pass a look rather than another try, and it is enforced
    /// in <see cref="IsDue"/> rather than left to the caller to remember.
    /// </summary>
    public static readonly TimeSpan RevalidateSpentAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether a narration may be attempted for this session NOW. Pure and total, with the clock injected,
    /// so every boundary is tested rather than waited for.
    ///
    /// The complete answer, because a summary that only describes the ordinary case is how the caller came
    /// to believe something the code did not do:
    ///
    /// <list type="bullet">
    /// <item>Nothing tried yet - due.</item>
    /// <item>Named, and a DIFFERENT turn from the one on record - due immediately, whatever that turn's
    /// history was. Elapsed time does not come into it: a new reply has never been tried.</item>
    /// <item>Named, the same turn, attempts left - due once the last one is <see cref="RetryEvery"/> old.</item>
    /// <item>Named, the same turn, attempts SPENT - never due again. This is the case the screen describes
    /// when it says the Gateway has stopped.</item>
    /// <item>Unnamed (the sweep, which has read nothing yet and so cannot tell which turn it is looking at)
    /// - due on <see cref="RetryEvery"/> while attempts remain, and on the much longer
    /// <see cref="RevalidateSpentAfter"/> once they are spent, so it can go and LOOK for a turn that changed
    /// unobserved. That look becomes a try only if the named ask above also says yes.</item>
    /// </list>
    /// </summary>
    /// <param name="turnKey">The turn being considered now, or null when the caller does not know it yet.
    ///
    /// The two are asked DIFFERENT questions, and this is the whole of the revalidation design:
    ///
    /// * <b>Null - the sweep, before it has read anything.</b> It cannot name the turn, so it cannot know
    ///   whether the spent schedule it can see belongs to the reply that is current now. Once
    ///   <see cref="RevalidateSpentAfter"/> has passed it is let through to go and LOOK.
    /// * <b>Named - the same pass, after reading.</b> Now the turn is known. Attempts recorded against a
    ///   DIFFERENT turn say nothing about this one and never hold it back. Attempts recorded against THIS
    ///   turn, once spent, stop it - permanently, however long ago they were made. There is no second
    ///   revalidation here, because there is nothing left to revalidate: the caller has just read the
    ///   conversation and the answer to "has the turn changed?" is no.
    ///
    /// FOUND IN REVIEW, and it is the defect this shape exists to prevent. When the exhausted branch applied
    /// the revalidation interval to the named ask as well, both calls said "due" ten minutes after the fifth
    /// failure, the model was called again, the attempt was recorded as a sixth - and then a seventh, and an
    /// eighth, every ten minutes for as long as the session lived. The Voice screen said the Gateway had
    /// stopped trying and offered a button, while the Gateway had not stopped at all. A comment in the caller
    /// claimed the pass was "a LOOK, not another try"; only this line makes that true.</param>
    public static bool IsDue(VoiceAttempts? attempts, DateTime utcNow, string? turnKey = null)
    {
        if (attempts is not { } a || a.Count == 0) return true;
        if (turnKey is not null && !string.Equals(a.TurnKey, turnKey, StringComparison.Ordinal)) return true;
        // Spent. An unnamed ask is let through after a long interval so a turn that changed unobserved is
        // never stranded (see RevalidateSpentAfter); a NAMED ask has already established that the turn is
        // this same spent one, and is refused for good.
        if (IsExhausted(a)) return turnKey is null && utcNow - a.LastAttemptUtc >= RevalidateSpentAfter;
        return utcNow - a.LastAttemptUtc >= RetryEvery;
    }

    /// <summary>
    /// True once the turn has used every automatic attempt. From here the Gateway does nothing more on
    /// its own: the screen says so and offers the button, and only a new turn (or a successful manual
    /// generation) resets the count.
    /// </summary>
    public static bool IsExhausted(VoiceAttempts? attempts)
        => attempts is { } a && IsExhausted(a.Count);

    /// <summary>The same question asked of a bare count - what the display fold holds.</summary>
    public static bool IsExhausted(int automaticAttempts) => automaticAttempts >= MaxAutomaticAttempts;
}
