using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Supervision;

/// <summary>
/// How a supervised turn ended (issue #915, phase 1 step 2). This is the closed set the deterministic
/// classifier and the model fallback both answer in, and the only vocabulary the recovery state machine
/// understands.
/// </summary>
public enum SessionFaultClass
{
    /// <summary>No fault on the screen - the turn ended cleanly and the session is supposed to be waiting.
    /// The majority answer, and the one that costs nothing: the supervisor stops here.</summary>
    None,

    /// <summary>A transport fault: name resolution failed, the connection reset, the socket dropped. Clears
    /// itself in seconds. This is the class the whole feature exists for.</summary>
    TransientTransport,

    /// <summary>The provider rate-limited the call. Recoverable, but only after backing off.</summary>
    RateLimited,

    /// <summary>The agent's context window is full. Recoverable ONLY by compacting first, which is phase 2
    /// (thefrederiksen/devthrottle_internal#1403) - so phase 1 raises its hand rather than sending a prompt
    /// into a session that swallows prompts.</summary>
    ContextFull,

    /// <summary>The work itself cannot proceed: out of allowance or credits, a failed sign-in, an invalid
    /// key. Never auto-continued - it must reach a human.</summary>
    NonRecoverable,

    /// <summary>The turn ended abnormally but on nothing the deterministic table recognizes. Step 3 (the
    /// model fallback) is the only thing that may resolve this; with the fallback off it escalates.</summary>
    Unclassified,
}

/// <summary>
/// One classified fault: the class, plus the SIGNATURE that matched.
///
/// The signature is deliberately OUR OWN token ("ENOTFOUND", "socket hang up", "credit balance"), never the
/// terminal line it was found in. It is what goes in the recovery log and the process log, so the log stays
/// diagnostic without carrying a customer's terminal content out of the tenant partition - the same line the
/// activity ledger already draws (<see cref="ActivityEventRecord.Detail"/> is a control-flow note and never
/// terminal text).
/// </summary>
public sealed record SessionFault(SessionFaultClass Class, string Signature)
{
    /// <summary>The no-fault answer.</summary>
    public static readonly SessionFault None = new(SessionFaultClass.None, "");

    /// <summary>True when this class is one the state machine may act on by re-sending "continue".</summary>
    public bool IsRecoverable => Class is SessionFaultClass.TransientTransport or SessionFaultClass.RateLimited;

    /// <summary>The activity-ledger cause for this class - the closed wire vocabulary the recovery log writes.</summary>
    public string LedgerCause => Class switch
    {
        SessionFaultClass.TransientTransport => ActivityCauses.TransientTransport,
        SessionFaultClass.RateLimited => ActivityCauses.RateLimited,
        SessionFaultClass.ContextFull => ActivityCauses.ContextFull,
        SessionFaultClass.NonRecoverable => ActivityCauses.NonRecoverable,
        SessionFaultClass.Unclassified => ActivityCauses.UnclassifiedFault,
        _ => ActivityCauses.Unknown,
    };
}
