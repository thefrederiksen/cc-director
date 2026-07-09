namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body for <c>POST /sessions/{sid}/hold</c>: park or un-park a session in the FIFO
/// voice queue. An empty body defaults to <see cref="OnHold"/> = true (the common case
/// is "hold this one"). Shared by the Director endpoint and the Gateway forwarder.
/// </summary>
public sealed class HoldRequest
{
    public bool OnHold { get; set; } = true;
}

/// <summary>
/// Response from <c>POST /sessions/{sid}/hold</c>: the session's hold state after the call. Issue #1177
/// (Phase 1): a typed DTO so the hold verb's result round-trips identically over the stream and over HTTP.
/// </summary>
public sealed class HoldResponse
{
    public bool OnHold { get; set; }
}
