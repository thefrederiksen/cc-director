namespace CcDirector.Core.HostedAi;

/// <summary>
/// Whether a hosted-AI feature (transcription, voice mode, Wingman, text-to-speech) can run right now
/// for this machine (issue #938, epic #937). ONE typed answer that every voice/Wingman/TTS surface
/// consumes, so the "you need credit" gate is decided in a single place and shown identically
/// everywhere - never a per-surface hand-written string, a generic "transcription failed", or a silent
/// nothing.
///
/// The pre-flight check (<see cref="HostedAiReadiness.CheckAsync"/>) and the runtime 402 mapper
/// (<see cref="HostedAiErrorMapper"/>) both produce this same enum, so a feature that is blocked
/// up front and one that runs dry mid-use are reported with the identical copy
/// (<see cref="HostedAiMessages.For"/>).
/// </summary>
public enum HostedAiState
{
    /// <summary>The feature can run: the active mode has a working resource (credits or a key).</summary>
    Ready = 0,

    /// <summary>DevThrottle hosted mode and the account balance is empty - the user must add credits.</summary>
    NeedsCredits = 1,

    /// <summary>The account's monthly spending limit has been reached - the user must raise it in Billing.</summary>
    CapReached = 2,
}
