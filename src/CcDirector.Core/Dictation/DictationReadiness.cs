using CcDirector.Core.Sessions;

namespace CcDirector.Core.Dictation;

/// <summary>
/// Whether a saved dictation may be typed into a session right now (issue #1135). A dictation is
/// delivered by typing it into the session's composer and pressing Enter; that is only safe when the
/// composer is idle at the prompt. While the agent is Working its composer repaints as output streams,
/// which makes the echo-verified submit false-negative and throw AFTER the text has already landed - and
/// the durable retry loop then re-types the same words on the next sweep, piling up duplicate copies of
/// the one sentence. So delivery is gated on the session sitting at the prompt; every other state defers
/// until it returns there.
/// </summary>
public static class DictationReadiness
{
    /// <summary>
    /// True only when the session's composer is idle at the prompt and will cleanly accept a typed
    /// dictation: <see cref="ActivityState.WaitingForInput"/> or <see cref="ActivityState.Idle"/>.
    /// <see cref="ActivityState.Working"/> (streaming output), <see cref="ActivityState.WaitingForPerm"/>
    /// (a permission prompt a free-text line would answer wrongly), <see cref="ActivityState.Starting"/>
    /// and <see cref="ActivityState.Exited"/> all defer.
    /// </summary>
    public static bool IsReadyForDelivery(ActivityState state)
        => state is ActivityState.WaitingForInput or ActivityState.Idle;
}
