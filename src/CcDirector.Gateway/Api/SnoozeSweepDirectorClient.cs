using CcDirector.Core.Sessions;
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
    /// Read the owning Director's RAW hold state for a session: the full tri-state, or null when the
    /// session is absent there or the read did not land. Tunnel-only via the "snapshot" read verb (a
    /// round-trip to the Director's own <see cref="SessionDto"/>). A failed or absent tunnel result maps
    /// to null.
    ///
    /// Defect 20: this reads <see cref="SessionDto.HoldState"/>, NOT the derived <c>OnHold</c> boolean. A
    /// DeferredHold reports <c>OnHold=false</c>, so a boolean read here cannot tell "not held" from "about
    /// to be held" - and the sweep's two answers to those are opposite (clear the snooze / leave it
    /// alone). Reading the boolean is what deleted a 12-hour timer 15 seconds after it was asked for.
    ///
    /// An unrecognised hold-state string maps to null - "I do not know" - and never to a guess: the sweep
    /// treats null as a missed read and changes nothing, which is the only safe answer when the Director's
    /// answer is not understood.
    /// </summary>
    public async Task<HoldState?> ReadHoldStateAsync(string directorId, string sessionId, CancellationToken ct)
    {
        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, directorId, "snapshot", sessionId, null, ct);
        if (result is null || !result.Ok)
            return null;
        var raw = DirectorCommandRouter.ReadBody<SessionDto>(result)?.HoldState;
        return HoldStates.Normalize(raw) switch
        {
            HoldStates.None => HoldState.None,
            HoldStates.Held => HoldState.Held,
            HoldStates.DeferredHold => HoldState.DeferredHold,
            _ => null,
        };
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
