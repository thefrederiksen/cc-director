using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Cleanup mission: the ONE choke point the snooze watchdog
/// (<see cref="Snooze.SnoozeExpirySweep"/>) reaches the owning Director through. It reaches the owning
/// Director DOWN its tunnel (the "snapshot" read verb for the raw OnHold read, the "hold" write verb for
/// the expiry nudge). A Director with no active stream is unreachable = offline/dead, the dead-man's-switch
/// case the sweep leaves untouched. This keeps the sweep itself Director-id addressed and free of any
/// endpoint plumbing.
///
/// The <paramref name="sendCommand"/> hook and <paramref name="pushedSessions"/> are the tunnel primitives.
/// </summary>
internal sealed class SnoozeSweepDirectorClient
{
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore? _pushedSessions;
    private readonly DirectorCommandRouter.SendDirectorCommandAsync? _sendCommand;

    public SnoozeSweepDirectorClient(DirectorRegistry registry, PushedSessionStore? pushedSessions,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _pushedSessions = pushedSessions;
        _sendCommand = sendCommand;
    }

    /// <summary>
    /// Whether the owning Director is reachable at all - over the tunnel (a live stream). False =
    /// offline/dead, the dead-man's-switch case the sweep leaves untouched.
    /// </summary>
    public bool IsReachable(string directorId)
    {
        if (_registry.Get(directorId) is null)
            return false;
        return _pushedSessions is not null && _pushedSessions.IsStreamConnected(directorId);
    }

    /// <summary>
    /// Read the owning Director's RAW hold state for a session: true = still held, false = no longer held,
    /// null = the session is absent there or the read did not land. Tunnel-only via the "snapshot" read verb
    /// (a round-trip to the Director's own <see cref="SessionDto"/>). A failed or absent tunnel result maps
    /// to null.
    /// </summary>
    public async Task<bool?> ReadOnHoldAsync(string directorId, string sessionId, CancellationToken ct)
    {
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, directorId, "snapshot", sessionId, null, ct);
        return result is not null && result.Ok ? DirectorCommandRouter.ReadBody<SessionDto>(result)?.OnHold : null;
    }

    /// <summary>
    /// The expiry nudge: forward a hold=false to the owning Director. Tunnel-only via the "hold" write verb.
    /// </summary>
    public async Task NudgeUnholdAsync(string directorId, string sessionId, CancellationToken ct)
    {
        var holdReq = new HoldRequest { OnHold = false };
        await DirectorCommandRouter.TrySendAsync(_sendCommand, directorId, "hold", sessionId, holdReq, ct);
    }
}
