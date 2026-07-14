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
}

/// <summary>
/// Response from <c>POST /sessions/{sid}/hold</c>: the session's hold state after the call. Issue #1177
/// (Phase 1): a typed DTO so the hold verb's result round-trips identically over the stream and over HTTP.
/// </summary>
public sealed class HoldResponse
{
    public bool OnHold { get; set; }
}
