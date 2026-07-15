namespace CcDirector.Core.HostedAi;

/// <summary>
/// Whether a hosted-AI feature (transcription, voice mode, Wingman, text-to-speech) can run right now
/// for this machine's DevThrottle hosted AI setup (issue #938, epic #937). ONE typed answer that every
/// voice/Wingman/TTS surface consumes, so the "you need credit / finish setup" gate is decided in a
/// single place and shown identically everywhere - never a per-surface hand-written string, a generic
/// "transcription failed", or a silent nothing.
///
/// The pre-flight check (<see cref="HostedAiReadiness.CheckAsync"/>) and the runtime 402 mapper
/// (<see cref="HostedAiErrorMapper"/>) both produce this same enum, so a feature that is blocked
/// up front and one that runs dry mid-use are reported with the identical copy
/// (<see cref="HostedAiMessages.For"/>).
/// </summary>
public enum HostedAiState
{
    /// <summary>The feature can run: the active DevThrottle account has a working resource.</summary>
    Ready = 0,

    /// <summary>DevThrottle hosted mode and the account balance is empty - the user must add credits.</summary>
    NeedsCredits = 1,

    /// <summary>The account's monthly spending limit has been reached - the user must raise it in Billing.</summary>
    CapReached = 2,

    /// <summary>DevThrottle AI setup is incomplete for this machine.</summary>
    NeedsKey = 3,

    /// <summary>
    /// The hosted service itself is failing - it answered with an error, or did not answer at all.
    /// Nothing is wrong with the account, the setup, or this machine: the far end is down.
    ///
    /// This state exists because we USED to throw this reason away. WingmanVoiceService mapped 402 and
    /// NeedsKey and returned a bare null for everything else ("other provider error: logged, no shared
    /// state"), so a real outage reached the phone as "the Gateway has not made one, or this session's
    /// computer is offline" - both false, and neither actionable. On 2026-07-15 speech failed for ~45
    /// minutes (84 failures / 55 successes) and the owner could not tell an outage from a bug, because
    /// the one fact that explained it was discarded three lines from where it was known.
    ///
    /// It is NOT the user's fault and there is nothing for them to fix, so the surface must say so and
    /// RETRY BY ITSELF rather than offering a button that fails the same way.
    /// </summary>
    ServiceDown = 4,
}
