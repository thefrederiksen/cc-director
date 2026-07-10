namespace CcDirector.Core.Sessions;

/// <summary>
/// Who is driving a <see cref="Session.SendTextAsync(string, SendSource)"/> call (issue #1181,
/// Task 3b - the desktop-side enforced dictation lock). The default is <see cref="UserInput"/>,
/// which is FAIL-CLOSED: a caller that does not name its source is treated as a human typing, so
/// forgetting to tag a new call site over-blocks (safe) rather than leaking past the lock (unsafe).
///
/// While a dictation is inbound to a session (an explicit PENDING delivery marker exists for it -
/// see <see cref="DictationLockReader"/>) that session is LOCKED: <see cref="UserInput"/> is
/// rejected so a half-arrived dictation and the user's own typing cannot collide. The two
/// non-user sources are exempt:
/// - <see cref="Delivery"/>: the dictation's OWN arrival (the Gateway injecting the transcribed
///   text). It must reach the session even while locked - it is what the lock is waiting for.
/// - <see cref="Internal"/>: framework-authored, non-user sends (handover text, queue drain) that
///   carry no human keystrokes and must not be blocked by the lock.
/// </summary>
public enum SendSource
{
    /// <summary>A human typing/submitting. Checked against the lock (fail-closed default).</summary>
    UserInput,

    /// <summary>The inbound dictation's own delivery. Exempt - it is what the lock is held for.</summary>
    Delivery,

    /// <summary>Framework-authored, non-user text (handover, queue drain). Exempt.</summary>
    Internal,
}
