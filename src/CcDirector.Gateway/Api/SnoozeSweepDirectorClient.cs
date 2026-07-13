using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Cleanup mission, Phase 2 (PR E-B3): the ONE choke point the snooze watchdog
/// (<see cref="Snooze.SnoozeExpirySweep"/>) reaches the owning Director through. It replaces the raw
/// <see cref="DirectorEndpointClient"/> HTTP dials the sweep used (GetSession for the raw OnHold read,
/// SetHold for the expiry nudge) with a tunnel-first path: under stream mode the owning Director is reached
/// DOWN its stream (the "snapshot" read verb and the "hold" write verb), and only over HTTP as the
/// byte-identical fallback (stream mode off, or the Director has no active stream). This keeps the sweep
/// itself Director-id addressed and free of any endpoint plumbing, and lands it on the same coexistence
/// pattern as every other Gateway-&gt;Director caller so the Phase 3 deletion of the HTTP surface is a
/// clean removal of the fallback branch.
///
/// The <paramref name="sendCommand"/> hook and <paramref name="pushedSessions"/> are non-null only under
/// stream mode, exactly like every other tunnel caller; with them null the client behaves byte-identically
/// to the old direct HTTP sweep.
/// </summary>
internal sealed class SnoozeSweepDirectorClient
{
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore? _pushedSessions;
    private readonly DirectorEndpointClient _client;
    private readonly DirectorCommandRouter.SendDirectorCommandAsync? _sendCommand;

    public SnoozeSweepDirectorClient(DirectorRegistry registry, PushedSessionStore? pushedSessions,
        DirectorEndpointClient client, DirectorCommandRouter.SendDirectorCommandAsync? sendCommand)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _pushedSessions = pushedSessions;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _sendCommand = sendCommand;
    }

    /// <summary>The HTTP fallback base URL for a Director id (the SAME forward destination the WS proxy
    /// uses - never a raw loopback ControlEndpoint for a remote Director), or null when it advertises none.</summary>
    private string? FallbackEndpoint(string directorId)
    {
        var d = _registry.Get(directorId);
        return d is null ? null : SessionWsProxyEndpoints.ForwardDestination(d);
    }

    /// <summary>
    /// Whether the owning Director is reachable at all - over the tunnel (a live stream) OR over HTTP (an
    /// advertised forward endpoint). False = offline/dead, the dead-man's-switch case the sweep leaves
    /// untouched. Post-cut a stream-only Director has no HTTP endpoint but is still reachable via its stream.
    /// </summary>
    public bool IsReachable(string directorId)
    {
        if (_registry.Get(directorId) is null)
            return false;
        if (_pushedSessions is not null && _pushedSessions.IsStreamConnected(directorId))
            return true;
        return FallbackEndpoint(directorId) is not null;
    }

    /// <summary>
    /// Read the owning Director's RAW hold state for a session: true = still held, false = no longer held,
    /// null = the session is absent there or the read did not land. Tunnel-first via the "snapshot" read verb
    /// (a round-trip to the Director's own <see cref="SessionDto"/>, byte-identical to the old GetSession),
    /// HTTP fallback on the advertised endpoint. A failed tunnel result maps to null exactly as the HTTP dial
    /// returned null on a non-200.
    /// </summary>
    public async Task<bool?> ReadOnHoldAsync(string directorId, string sessionId, CancellationToken ct)
    {
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, directorId, "snapshot", sessionId, null, ct);
        if (result is not null)
            return result.Ok ? DirectorCommandRouter.ReadBody<SessionDto>(result)?.OnHold : null;
        var ep = FallbackEndpoint(directorId);
        return ep is null ? null : (await _client.GetSessionAsync(ep, sessionId, ct))?.OnHold;
    }

    /// <summary>
    /// The expiry nudge: forward a hold=false to the owning Director. Tunnel-first via the "hold" write verb,
    /// HTTP fallback on the advertised endpoint. A non-null tunnel result (success OR a typed failure) is
    /// authoritative, so the fallback runs only when there is no stream.
    /// </summary>
    public async Task NudgeUnholdAsync(string directorId, string sessionId, CancellationToken ct)
    {
        var holdReq = new HoldRequest { OnHold = false };
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, directorId, "hold", sessionId, holdReq, ct);
        if (result is not null)
            return;
        var ep = FallbackEndpoint(directorId);
        if (ep is not null)
            await _client.SetHoldAsync(ep, sessionId, holdReq, ct);
    }
}
