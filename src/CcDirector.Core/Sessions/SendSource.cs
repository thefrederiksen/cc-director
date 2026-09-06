namespace CcDirector.Core.Sessions;

/// <summary>
/// Who is driving a <see cref="Session.SendTextAsync(string, SubmissionProvenance, SendSource, InputOrigin?)"/> call (issue #1181,
/// Task 3b - the desktop-side enforced dictation lock). The default is <see cref="UserInput"/>,
/// which is FAIL-CLOSED: a caller that does not name its source is treated as a human typing, so
/// forgetting to tag a new call site over-blocks (safe) rather than leaking past the lock (unsafe).
///
/// While a dictation is inbound to a session (an explicit PENDING delivery marker exists for it -
/// see <see cref="DictationLockReader"/>) that session is LOCKED: <see cref="UserInput"/> is
/// rejected so a half-arrived dictation and the user's own typing cannot collide. The three
/// non-user sources are exempt:
/// - <see cref="Delivery"/>: the dictation's OWN arrival (the Gateway injecting the transcribed
///   text). It must reach the session even while locked - it is what the lock is waiting for.
/// - <see cref="Agent"/>: one agent prompting another across the fleet.
/// - <see cref="Framework"/>: text the product itself authored (handover, queue drain, pre-prompt).
///
/// <see cref="Agent"/> and <see cref="Framework"/> were ONE value ("Internal") until issue #1636.
/// They behave identically for the dictation lock, which is why they were ever conflated, but they
/// are nothing alike as work: a manager prompting a worker is a real turn that drives real
/// development, while a queue drain is plumbing that carries no decision at all. Counting the two
/// together would have counted framing noise as development, so the split has to exist before the
/// agent-to-agent tally can mean anything.
///
/// This enum says WHO SENT the text. It is not the counting gate - that is the InputOrigin passed
/// alongside it (a null origin is not counted). The two are separate on purpose: the desktop
/// dictation path sends <see cref="Internal"/>-class text that IS a human voice turn.
/// </summary>
public enum SendSource
{
    /// <summary>A human typing/submitting. Checked against the lock (fail-closed default).</summary>
    UserInput,

    /// <summary>The inbound dictation's own delivery. Exempt - it is what the lock is held for.</summary>
    Delivery,

    /// <summary>
    /// One agent prompting another: a fleet message, a fleet ask, or a broadcast delivery. A REAL turn -
    /// the sender decided to send it - but driven by an agent rather than by the human. Exempt from the
    /// lock (it is not a human racing the dictation).
    /// </summary>
    Agent,

    /// <summary>
    /// Text the product authored itself: handover context, a queue drain, a pre-prompt, chat relay. Carries
    /// no human keystrokes and no agent's decision, so it is never counted as a turn by anything. Exempt
    /// from the lock.
    /// </summary>
    Framework,
}
