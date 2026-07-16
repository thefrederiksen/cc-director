namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body for <c>POST /sessions/{sid}/hold</c>: park or un-park a session in the FIFO
/// voice queue. An empty body defaults to <see cref="OnHold"/> = true (the common case
/// is "hold this one"). Shared by the Director endpoint and the Gateway forwarder.
/// </summary>
public sealed class HoldRequest
{
    public bool OnHold { get; set; } = true;

    /// <summary>
    /// Optional per-call snooze length in whole minutes (issue #1500). When a caller holds a session
    /// (<see cref="OnHold"/> = true) and supplies a value here, the Gateway arms its snooze timer for
    /// exactly this many minutes instead of the per-user default (<c>snooze_default_minutes</c>). Null
    /// keeps the existing behaviour - the Gateway uses the default - so the field is backward
    /// compatible. Read and validated (1..10080) ONLY by the Gateway hold endpoint; the Director never
    /// reads it, so this is a Gateway-only capability and needs no Director change.
    /// </summary>
    public int? SnoozeMinutes { get; set; }

    /// <summary>
    /// The hold state the GATEWAY has decided for this session, as a <see cref="HoldStates"/> value, when
    /// this request is the Gateway pushing its ruling DOWN to the owning Director. The Director writes it
    /// to its display mirror verbatim - it decides nothing.
    ///
    /// Null means the caller is not the Gateway pushing a decision (an old Gateway, or a direct loopback
    /// call), and the Director falls back to reading <see cref="OnHold"/> as before: true -&gt; Held,
    /// false -&gt; None. That is not a guess - a boolean genuinely cannot express DeferredHold, so the
    /// worst an old caller can produce is a mirror that says Held instead of DeferredHold, which the next
    /// Gateway push corrects. Nothing decides anything from it either way.
    /// </summary>
    public string? HoldState { get; set; }
}

/// <summary>
/// Response from <c>POST /sessions/{sid}/hold</c>: the session's hold state after the call. Issue #1177
/// (Phase 1): a typed DTO so the hold verb's result round-trips identically over the stream and over HTTP.
/// </summary>
public sealed class HoldResponse
{
    public bool OnHold { get; set; }

    /// <summary>
    /// True when an explicit hold was DEFERRED because the agent was working: the session is not held yet
    /// (<see cref="OnHold"/> is still false), but it parks the moment the work stops. Lets the caller's
    /// button say "it'll hold when it finishes what it's doing" instead of implying it is already held.
    /// False for an immediate hold, an un-hold, or a Director that predates this field.
    /// </summary>
    public bool Pending { get; set; }
}
