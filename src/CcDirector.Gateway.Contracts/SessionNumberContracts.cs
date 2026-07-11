namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of POST /session-numbers/allocate (issue #1292): a Director asking the Gateway to hand out the
/// fleet-unique three-digit session number for a session it is creating. Idempotent per
/// <see cref="SessionId"/>.
/// </summary>
public sealed class SessionNumberAllocateRequest
{
    /// <summary>The session that needs a number (the Director's internal session GUID as a string).</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The Director asking, so the Gateway can free the number if that Director is later removed.</summary>
    public string DirectorId { get; set; } = "";
}

/// <summary>Response of POST /session-numbers/allocate (issue #1292).</summary>
public sealed class SessionNumberAllocateResponse
{
    /// <summary>The assigned fleet-unique number (100-999), or null when the pool is exhausted.</summary>
    public int? Number { get; set; }
}
