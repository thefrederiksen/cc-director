namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Issue #1176 (Phase 1a): the first message a Director sends after opening its stream to the Gateway,
/// declaring which Director this connection speaks for. The hub binds the connection to this identity;
/// from then on every snapshot/delta/remove from that connection applies only to this Director, so one
/// connection can never push into another Director's cache.
/// </summary>
public sealed class DirectorStreamHello
{
    /// <summary>The stable Director id (the same id used for HTTP registration and the /sessions aggregation).</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>Director build version, for diagnostics.</summary>
    public string Version { get; set; } = "";
}
