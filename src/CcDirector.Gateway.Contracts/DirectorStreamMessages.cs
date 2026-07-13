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

    /// <summary>
    /// Gateway Cleanup mission (tunnel-only): the Director's machine name. The tunnel is now the ONLY
    /// registration path (HTTP register is gone), so Hello carries the identity the Gateway's registry
    /// needs to know this Director exists - machine, user, pid, start time. The Gateway registers a
    /// tunnel Director with Source="stream" and NO control/tailnet endpoint (it is never dialed).
    /// </summary>
    public string MachineName { get; set; } = "";

    /// <summary>Gateway Cleanup mission (tunnel-only): the OS user the Director runs as (roster display).</summary>
    public string User { get; set; } = "";

    /// <summary>Gateway Cleanup mission (tunnel-only): the Director process id (roster/diagnostics).</summary>
    public int Pid { get; set; }

    /// <summary>Gateway Cleanup mission (tunnel-only): when the Director process started (UTC).</summary>
    public DateTime StartedAt { get; set; }
}
