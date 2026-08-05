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
    /// The hosted service ANSWERED with a failure - a 5xx, or a 429 asking us to slow down. The service
    /// itself is genuinely struggling: nothing is wrong with the account, the setup, or this machine.
    ///
    /// This is reserved for an ANSWER. A call that simply did not answer in time (a timeout or transport
    /// failure) is <see cref="Retrying"/>, NOT this - because a non-answer is the absence of evidence,
    /// and stamping "the service is down" on it is a claim we cannot support. That distinction is the
    /// whole point of splitting the two: the phone renders a red "Voice service down" panel on this
    /// state, which is right for a real answered outage and wrong for one slow call.
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

    /// <summary>
    /// The speech call did not answer in time - a timeout, or a transport failure - so we have NO
    /// evidence about the service. The audio is simply not ready yet, and a retry is already underway.
    ///
    /// This is deliberately NOT <see cref="ServiceDown"/>. A timeout is the ABSENCE of an answer, which
    /// tells us nothing about whether the service is healthy; on 2026-07-15/16 the service answered
    /// hand-made calls in ~2 seconds while the sweep's calls were timing out on a cold start. Reporting
    /// "the voice service is not responding" / "Voice service down" on that is a lie the user cannot
    /// act on - it sends them hunting for an outage that is not there. The honest thing to say is that
    /// the audio is on its way and we are trying again, which is exactly what is happening.
    ///
    /// Like <see cref="ServiceDown"/> it is not the user's fault and offers no call to action - the
    /// surface retries by itself. Unlike it, the surface shows a calm "on its way" state, not a red
    /// outage panel.
    /// </summary>
    Retrying = 5,

    /// <summary>
    /// The account has no live entitlement for the included AI features (issue #1360, proxy 402 code
    /// <c>subscription_required</c>): no active trial and no active plan. This is NOT a credits
    /// condition - the copy points at the pricing page and never mentions credits, balances, or a
    /// top-up, because the owner ruled that a normal member sees no cost anywhere in the product.
    /// </summary>
    SubscriptionRequired = 6,

    /// <summary>
    /// The account's monthly fair-use allowance for the included AI features is used up (issue #1360,
    /// proxy 402 code <c>fair_use_limit_reached</c>). It resets at the start of next month. The copy
    /// carries NO numbers and NO cost - the fair-use cap is deliberately invisible ("They should never
    /// know this" - the owner) - and no call-to-action, because there is nothing the member can click
    /// to lift a month-long condition.
    /// </summary>
    FairUseLimitReached = 7,

    /// <summary>
    /// A money-shaped refusal (HTTP 402) whose machine code this build does not recognize. Deliberately
    /// NEUTRAL: an unknown code must never claim the account is out of credits (issue #1360 - the old
    /// mapper defaulted unknowns to <see cref="NeedsCredits"/>, which would have shown "add credits" to
    /// members the owner ruled must never see a cost). Says the feature is unavailable, offers no
    /// call-to-action, names no money.
    /// </summary>
    Unavailable = 8,
}
