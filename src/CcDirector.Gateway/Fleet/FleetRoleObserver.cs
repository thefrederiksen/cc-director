using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// Pushes each session's resolved role DOWN to the Director that owns it, so the desktop rail folds the
/// SAME role the phone and the Cockpit fold (defect 5). This is the PUSH seam, modelled exactly on
/// <c>SnoozeLandingObserver</c>: every session a Director pushes up its tunnel passes through here.
///
/// THE DEFECT. <c>ControlEndpoints.Map</c> never set <see cref="SessionDto.SessionRole"/> - the role is
/// computed at the Gateway from the WHOLE fleet, because "is this session's controller still alive?" cannot
/// be answered from one Director. The desktop's fold is fed by its own Director through that same mapper,
/// so on the desktop the field was always null, so the fold's red-suppression
/// (<c>SessionOrdering.BaseColor</c>) could never fire. A live Worker read slate "Sub-agent" on the phone
/// and red "Needs you" on the desktop AT THE SAME INSTANT - a disagreement by construction, for every red
/// Worker with a live controller.
///
/// THE SHAPE OF THE FIX, and the line it must not cross. The Gateway STAMPS; the Director CARRIES. The
/// Director performs no resolution - it caches a value it was told and reads it back out through its
/// mapper. That is the same distinction Phase 1 drew for the snooze clock: the Director reports the
/// landing, the Gateway owns the clock. The Director computing its own role is what law 3 forbids and is
/// exactly the trap sitting one call away in <c>SessionManager.ResolveLocalRole</c> (which is wrong
/// cross-machine, and must not be wired into the fold).
///
/// WHY THE FLEET, NOT THE PUSHED SESSION. A role change for session X is very often caused by a change to
/// session Y - Y is X's controller and Y just exited, so X stops being a Worker and its red must surface.
/// Y may live on a DIFFERENT Director than X. So a push from ANY Director re-resolves the WHOLE fleet and
/// fans out to every Director whose sessions changed, not just the one that pushed.
///
/// WHY THE CHANGE GATE IS LOAD-BEARING. Sending a role down makes the Director report it back up on its
/// next delta, which lands here again. Ungated, that is an infinite echo: role -> delta -> observe -> role.
/// The send is gated on the role having actually CHANGED from what we last sent, so the echo resolves to
/// the same value, sends nothing, and the loop terminates on its first turn. <see cref="_lastSent"/> is
/// that gate and it is the only thing standing between this class and a spin.
///
/// NO GATEWAY, NO ROLE. A Director with no tunnel never receives a stamp, so its desktop leaves
/// SessionRole null and a Worker's red surfaces. That is the honest answer and the status quo: the Gateway
/// owns the fact, and a client inventing a local one IS the defect.
/// </summary>
public sealed class FleetRoleObserver
{
    private readonly Func<IReadOnlyList<(string DirectorId, SessionDto Session)>> _snapshot;
    // Spelled out rather than using DirectorCommandRouter.SendDirectorCommandAsync (which the auto-dismiss
    // sweeper takes): that delegate is internal, and this observer is public because the public DirectorHub
    // takes one. Structurally identical - GatewayHost.SendCommandAsync satisfies both.
    private readonly Func<string, DirectorCommand, CancellationToken, Task<DirectorCommandResult?>> _sendCommand;

    /// <summary>
    /// The change gate: session id -> the role we last successfully sent down. An entry is only written
    /// AFTER the send is accepted, so a dropped send is retried on the next push rather than being recorded
    /// as delivered. Bounded by pruning sessions that have left the fleet.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _lastSent = new(StringComparer.Ordinal);

    /// <param name="snapshot">The fresh pushed sessions across every stream-connected Director, each paired
    /// with its owning directorId (PushedSessionStore.SnapshotFresh) - the same fleet read the auto-dismiss
    /// sweeper uses. Roles need the WHOLE fleet, not one Director's slice.</param>
    /// <param name="sendCommand">The down-channel command sender (GatewayHost.SendCommandAsync). A null
    /// RESULT means that Director has no stream, which is the documented "no Gateway, no role" floor.</param>
    public FleetRoleObserver(
        Func<IReadOnlyList<(string DirectorId, SessionDto Session)>> snapshot,
        Func<string, DirectorCommand, CancellationToken, Task<DirectorCommandResult?>> sendCommand)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _sendCommand = sendCommand ?? throw new ArgumentNullException(nameof(sendCommand));
    }

    /// <summary>Observe one pushed session. Any push can change any session's role, so this re-resolves the
    /// whole fleet - see the class remarks.</summary>
    public void Observe(SessionDto? session)
    {
        if (session is null || string.IsNullOrEmpty(session.SessionId)) return;
        Sweep();
    }

    /// <summary>Observe a whole pushed snapshot - the reconnect path, where a role change can hide.</summary>
    public void ObserveSnapshot(IReadOnlyList<SessionDto>? sessions)
    {
        if (sessions is null || sessions.Count == 0) return;
        Sweep();
    }

    /// <summary>
    /// Re-resolve every session's role from the whole fleet and push down the ones that CHANGED.
    ///
    /// Fire-and-forget by design: this runs on the hub's push path, and a Director that is slow to answer a
    /// role stamp must not stall the session delta that triggered it. A dropped send costs one stale role
    /// until the next push, which is the same bound every other pushed fact carries.
    /// </summary>
    internal void Sweep()
    {
        var fleet = _snapshot();
        if (fleet is null || fleet.Count == 0) return;

        // Resolve over the WHOLE fleet. SnapshotFresh already hands back deep copies, so stamping these
        // cannot touch the cache - the roster read does its own stamping pass on its own copies.
        var resolved = fleet.Select(f => f.Session).ToList();
        FleetRoleResolver.Stamp(resolved);

        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (directorId, s) in fleet)
        {
            if (string.IsNullOrEmpty(s.SessionId) || string.IsNullOrEmpty(directorId)) continue;
            live.Add(s.SessionId);

            var role = s.SessionRole ?? SessionRoles.Standalone;
            // THE GATE. Unchanged role -> no send -> the echo of our own stamp dies here rather than
            // becoming the next push. Removing this makes the observer spin.
            if (_lastSent.TryGetValue(s.SessionId, out var sent) && string.Equals(sent, role, StringComparison.Ordinal))
                continue;

            _ = SendRoleAsync(directorId, s.SessionId, role);
        }

        // Keep the gate bounded: a session that has left the fleet keeps no entry.
        foreach (var key in _lastSent.Keys)
            if (!live.Contains(key))
                _lastSent.TryRemove(key, out _);
    }

    private async Task SendRoleAsync(string directorId, string sessionId, string role)
    {
        try
        {
            var command = new DirectorCommand
            {
                CommandId = Guid.NewGuid().ToString("N"),
                Verb = "set-resolved-role",
                SessionId = sessionId,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                    new SetResolvedRoleRequest { Role = role },
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            };

            var result = await _sendCommand(directorId, command, CancellationToken.None);
            if (result is null)
            {
                // No tunnel. Not an error - that Director's desktop simply has no stamped role, which is
                // the documented "no Gateway, no role" floor. Record NOTHING, so the stamp is retried the
                // moment the Director reconnects and pushes.
                FileLog.Write($"[FleetRoleObserver] sid={sessionId}: no stream for director={directorId}; role '{role}' not delivered");
                return;
            }
            if (result.Status != DirectorCommandStatus.Ok)
            {
                FileLog.Write($"[FleetRoleObserver] sid={sessionId}: director={directorId} rejected role '{role}': {result.Status} {result.Error}");
                return;
            }

            // Recorded ONLY on a confirmed delivery, so a dropped stamp is re-sent on the next push.
            _lastSent[sessionId] = role;
            FileLog.Write($"[FleetRoleObserver] sid={sessionId}: role '{role}' stamped down to director={directorId}");
        }
        catch (Exception ex)
        {
            // A boundary: this is fire-and-forget off the hub's push path, so a faulting send must not
            // take down the push that triggered it, and must not be recorded as delivered.
            FileLog.Write($"[FleetRoleObserver] sid={sessionId}: role stamp FAILED for director={directorId}: {ex.Message}");
        }
    }
}
