namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The body of <c>GET /account/trial</c> (issue #1243): whether the calling account's free Pro trial is
/// running, when it ends, and how many days are left.
///
/// WHY THIS CONTRACT EXISTS AT ALL. The trial already worked. It is granted at hosted enrolment, written to
/// the Gateway's own <c>account_trials</c> ledger, and read for the entitlement decision - and there was no
/// read path out of it. No endpoint returned trial state, so no screen could show one: a search of the whole
/// product for a trial end, a day count, or an active flag found nothing. We granted something valuable and
/// never told the person, and a promise kept silently is indistinguishable from a promise broken.
///
/// THE STATE IS NOT A BOOLEAN, AND THERE IS DELIBERATELY NO BOOLEAN ON THIS CONTRACT. A
/// <c>trialActive: false</c> field would answer "is a trial running?" with "no" in the one case where the
/// honest answer is "I could not find out" - and every consumer that read the boolean would make that
/// substitution silently, at every site, forever. The reader is forced to look at <see cref="State"/> and
/// decide what to do about <see cref="StateUnknown"/> because there is no comfortable field to reach for
/// instead.
///
/// FOUR VALUES, THREE PARTITIONS. <see cref="StateExpired"/> and <see cref="StateNone"/> are separated
/// because a sentence shown to a member cannot fold them - "your Pro trial ended on 17 August" is false for
/// someone who never had one. The SAFETY partition is still three, and it is the one that must never be
/// collapsed: running / known-not-running / unknown.
///
/// The Gateway folds <see cref="Message"/> once and the client renders it verbatim (CLAUDE.md rule 7). A
/// client that composes its own sentence from these fields will, the first time it meets a state it did not
/// expect, render something plausible instead of something true.
/// </summary>
public sealed class AccountTrialDto
{
    /// <summary>A free Pro trial is running right now. <see cref="EndsAtUtc"/> and
    /// <see cref="DaysRemaining"/> are populated.</summary>
    public const string StateActive = "active";

    /// <summary>This account HAD a trial and its window has closed. It is never extended or re-granted, so
    /// this is permanent. <see cref="EndsAtUtc"/> is populated and names the day it ended.</summary>
    public const string StateExpired = "expired";

    /// <summary>The read succeeded and this account was never granted a trial. NOT the same as
    /// <see cref="StateUnknown"/>, and never to be shown as "expired".</summary>
    public const string StateNone = "none";

    /// <summary>
    /// The trial could not be determined - the ledger read failed, no account is bound to the caller, or this
    /// Gateway is not the one holding the caller's trial. IGNORANCE, NOT ABSENCE. A surface must say so out
    /// loud; rendering this as "no trial" tells a member with twelve days left that they have nothing.
    /// </summary>
    public const string StateUnknown = "unknown";

    /// <summary>
    /// Which of the four states this account is in - one of <see cref="StateActive"/>,
    /// <see cref="StateExpired"/>, <see cref="StateNone"/>, <see cref="StateUnknown"/>. Always populated. A
    /// consumer that does not recognise the value must treat it as <see cref="StateUnknown"/>, never as an
    /// absence of trial.
    /// </summary>
    public string State { get; set; } = StateUnknown;

    /// <summary>
    /// The finished, user-facing sentence, computed on the Gateway and rendered verbatim. Always populated,
    /// including on <see cref="StateUnknown"/> - a surface that cannot determine the trial still owes the
    /// member a sentence, and the client must never compose one from the other fields.
    /// </summary>
    public string Message { get; set; } = "";

    /// <summary>When the trial was granted. Populated whenever a trial row was read (active or expired);
    /// null otherwise. Never fabricated.</summary>
    public DateTime? StartedAtUtc { get; set; }

    /// <summary>
    /// When the trial ends, or ended. Populated whenever a trial row was read (active or expired); null
    /// otherwise - unknown, never a fabricated date.
    /// </summary>
    public DateTime? EndsAtUtc { get; set; }

    /// <summary>
    /// Whole days left, counting a part-day as a day, so the last day of a trial reads "1 day left" rather
    /// than "0 days left" while access still works. Populated ONLY on <see cref="StateActive"/> and at least
    /// 1 there; null in every other state. A null is not a zero and must never be rendered as one.
    /// </summary>
    public int? DaysRemaining { get; set; }
}
