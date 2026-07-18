using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// Pushes each session's FOLDED display state DOWN to the Director that owns it, so the desktop rail renders
/// exactly what the phone and the Cockpit render instead of re-folding from local facts it cannot see. This
/// is the PUSH seam for the fold, modelled exactly on <see cref="FleetRoleObserver"/> - which pushed ONE
/// fold input (the role) down and closed one of five measured desktop-versus-phone disagreements. This
/// pushes the fold ANSWER, closing the other four (a phone dictation, the Gateway's transcription, a voice
/// summary being prepared, and the snooze clock) and the disease behind them: the desktop folding at all.
///
/// THE FOLD RUNS ONCE, HERE AND EVERYWHERE. The delegate is <c>GatewayEndpoints.StampFleetRolesAndFold</c> -
/// the SAME method the roster, /exes/list and the single-session read fold through - so the answer pushed to
/// the desktop is byte-identical to the answer served to every browser. A second fold would be a second
/// authority, and a drifting second authority IS the disagreement this exists to end.
///
/// WHY THE WHOLE FLEET. The fold reads roles resolved across the whole fleet (a controller's liveness), so a
/// push for session X can change session Y's answer on another Director. Every trigger re-folds the fleet
/// and fans the CHANGED answers out, exactly as the role observer does.
///
/// WHY THE CHANGE GATE IS LOAD-BEARING. Sending a fold down makes the Director report it back up on its next
/// delta (ControlEndpoints.Map echoes the cached values), which lands here again. Ungated that is an
/// infinite echo. The send is gated on the fold SIGNATURE having actually changed from what we last sent, so
/// the echo resolves to the same signature, sends nothing, and the loop terminates on its first turn.
/// <see cref="_lastSent"/> is that gate and it is the only thing between this class and a spin.
///
/// NO GATEWAY, NO FOLD. A Director with no tunnel never receives a stamp, so its rail leaves the fold null
/// and shows a neutral waiting-for-gateway placeholder. That is the honest answer and the status quo: the
/// Gateway owns the fold, and a client inventing a local one IS the defect.
/// </summary>
public sealed class FleetDisplayStateObserver
{
    private readonly Func<IReadOnlyList<(string DirectorId, SessionDto Session)>> _snapshot;
    private readonly Func<string, DirectorCommand, CancellationToken, Task<DirectorCommandResult?>> _sendCommand;
    // The shared fold. Given a list, it stamps EffectiveColor / StateLabel / TriageBucket / NeedsYouSince /
    // SnoozeUntil / SnoozeExpired / HoldState onto each entry - the identical pass the roster runs. Injected
    // rather than referenced so this observer does not reach across into the Api layer's endpoint type.
    private readonly Action<List<SessionDto>> _stampFold;

    /// <summary>
    /// The change gate: session id -> the fold signature we last successfully sent down. Written only AFTER
    /// a send is accepted, so a dropped send is retried on the next trigger rather than recorded as
    /// delivered. Bounded by pruning sessions that have left the fleet.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _lastSent = new(StringComparer.Ordinal);

    /// <param name="snapshot">The fresh pushed sessions across every stream-connected Director, each paired
    /// with its owning directorId (PushedSessionStore.SnapshotFresh) - the same fleet read the role observer
    /// and the auto-dismiss sweeper use. The fold needs the WHOLE fleet, not one Director's slice.</param>
    /// <param name="stampFold">The shared fold (GatewayEndpoints.StampFleetRolesAndFold over the fleet as
    /// both universe and stamp set, with the roster's NeedsYouClock and the snooze registry) - the ONE
    /// implementation, so the pushed answer equals the served answer.</param>
    /// <param name="sendCommand">The down-channel command sender (GatewayHost.SendCommandAsync). A null
    /// RESULT means that Director has no stream, which is the documented "no Gateway, no fold" floor.</param>
    public FleetDisplayStateObserver(
        Func<IReadOnlyList<(string DirectorId, SessionDto Session)>> snapshot,
        Action<List<SessionDto>> stampFold,
        Func<string, DirectorCommand, CancellationToken, Task<DirectorCommandResult?>> sendCommand)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _stampFold = stampFold ?? throw new ArgumentNullException(nameof(stampFold));
        _sendCommand = sendCommand ?? throw new ArgumentNullException(nameof(sendCommand));
    }

    /// <summary>Observe one pushed session. Any push can change any session's fold, so this re-folds the
    /// whole fleet - see the class remarks.</summary>
    public void Observe(SessionDto? session)
    {
        if (session is null || string.IsNullOrEmpty(session.SessionId)) return;
        Sweep();
    }

    /// <summary>Observe a whole pushed snapshot - the reconnect path, where a fold change can hide.</summary>
    public void ObserveSnapshot(IReadOnlyList<SessionDto>? sessions)
    {
        if (sessions is null || sessions.Count == 0) return;
        Sweep();
    }

    /// <summary>Observe a session LEAVING the fleet. A departure changes other sessions' folds (a
    /// controller's tombstone un-suppresses its workers' red) exactly as an arrival does.</summary>
    public void ObserveRemoval(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        Sweep();
    }

    /// <summary>
    /// Re-fold every session from the whole fleet and push down the ones whose fold CHANGED.
    ///
    /// Fire-and-forget by design: this runs on the hub's push path and on the periodic sweep, and a Director
    /// slow to answer a stamp must not stall the delta that triggered it. A dropped send costs one stale
    /// fold until the next trigger, the same bound every other pushed fact carries.
    ///
    /// Public so a periodic timer (the backstop for Gateway-only overlay changes - voice generation,
    /// transcription, a snooze expiring - which arrive on no Director push) can drive it too.
    /// </summary>
    public void Sweep()
    {
        var fleet = _snapshot() ?? Array.Empty<(string, SessionDto)>();

        // Fold over the WHOLE fleet. SnapshotFresh hands back deep copies, so stamping these cannot touch the
        // cache - the roster read does its own stamping pass on its own copies. The tuple entries reference
        // these same objects, so iterating the fleet below sees the stamped values. An empty fleet still runs
        // the prune below (a Director whose last session left must not keep a stale gate entry that would
        // suppress the stamp when that session returns).
        var toFold = fleet.Select(f => f.Session).ToList();
        if (toFold.Count > 0)
            _stampFold(toFold);

        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (directorId, s) in fleet)
        {
            if (string.IsNullOrEmpty(s.SessionId) || string.IsNullOrEmpty(directorId)) continue;
            live.Add(s.SessionId);

            var signature = Signature(s);
            // THE GATE. Unchanged fold -> no send -> the echo of our own stamp dies here rather than
            // becoming the next push. Removing this makes the observer spin.
            if (_lastSent.TryGetValue(s.SessionId, out var sent) && string.Equals(sent, signature, StringComparison.Ordinal))
                continue;

            _ = SendDisplayStateAsync(directorId, s, signature);
        }

        // Keep the gate bounded: a session that has left the fleet keeps no entry.
        foreach (var key in _lastSent.Keys)
            if (!live.Contains(key))
                _lastSent.TryRemove(key, out _);
    }

    /// <summary>
    /// Fold the fleet and stamp ONE session's display state down NOW, awaiting delivery. This is the PROMPT
    /// trigger the hold endpoint uses so a Snooze / Unsnooze click reaches the desktop rail immediately
    /// instead of on the next periodic sweep - and it is what keeps this observer the SINGLE writer of the
    /// Director's raw hold: the endpoint records the registry and calls this, rather than sending its own
    /// hold command. Because it stamps the CURRENT fold of the CURRENT registry (not a value decided earlier),
    /// it can never write a stale hold: if the session was worked or unsnoozed between the mutation and this
    /// call, the fold already reads that, so there is no descheduled-writer race.
    ///
    /// Goes through the SAME change gate as <see cref="Sweep"/>, so an unchanged fold sends nothing and there
    /// is never a double-send. Best-effort, like every send here: a slow or dead Director cannot fail the
    /// hold (already recorded in the registry) - the periodic sweep and the next push reconcile the rail.
    /// </summary>
    public async Task PushSessionAsync(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        var fleet = _snapshot() ?? Array.Empty<(string, SessionDto)>();
        var toFold = fleet.Select(f => f.Session).ToList();
        if (toFold.Count > 0)
            _stampFold(toFold);

        foreach (var (directorId, s) in fleet)
        {
            if (!string.Equals(s.SessionId, sessionId, StringComparison.Ordinal) || string.IsNullOrEmpty(directorId))
                continue;
            var signature = Signature(s);
            // The same gate Sweep uses: if the desktop already holds this exact fold, there is nothing to
            // send - and nothing to double-send if a push delivered it a moment ago.
            if (_lastSent.TryGetValue(s.SessionId, out var sent) && string.Equals(sent, signature, StringComparison.Ordinal))
                return;
            await SendDisplayStateAsync(directorId, s, signature);
            return;
        }
    }

    /// <summary>The fold answer as one comparable string. Any field the desktop renders changing must re-push,
    /// so every rendered field is in the signature.</summary>
    private static string Signature(SessionDto s) =>
        string.Join(
            '|',
            s.EffectiveColor ?? "",
            s.StateLabel ?? "",
            s.TriageBucket ?? "",
            s.NeedsYouSince?.ToUniversalTime().ToString("O") ?? "",
            s.SnoozeUntil?.ToUniversalTime().ToString("O") ?? "",
            s.SnoozeExpired ? "1" : "0",
            s.HoldState ?? "");

    private async Task SendDisplayStateAsync(string directorId, SessionDto s, string signature)
    {
        try
        {
            var command = new DirectorCommand
            {
                CommandId = Guid.NewGuid().ToString("N"),
                Verb = "set-display-state",
                SessionId = s.SessionId,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                    new SetDisplayStateRequest
                    {
                        EffectiveColor = s.EffectiveColor,
                        StateLabel = s.StateLabel,
                        TriageBucket = s.TriageBucket,
                        NeedsYouSince = s.NeedsYouSince,
                        SnoozeUntil = s.SnoozeUntil,
                        SnoozeExpired = s.SnoozeExpired,
                        HoldState = s.HoldState,
                    },
                    new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            };

            var result = await _sendCommand(directorId, command, CancellationToken.None);
            if (result is null)
            {
                // No tunnel. Not an error - that Director's rail simply has no stamped fold, the documented
                // "no Gateway, no fold" floor. Record NOTHING, so the stamp is retried the moment the
                // Director reconnects and pushes.
                FileLog.Write($"[FleetDisplayStateObserver] sid={s.SessionId}: no stream for director={directorId}; fold not delivered");
                return;
            }

            // A DEFINITIVE response - success OR rejection - counts as delivered for the gate, so record it
            // either way. A rejection is almost always an OLD Director that does not know this verb yet
            // during a Gateway-ahead-of-Directors rollout (the normal deploy order: the Gateway ships first).
            // Re-sending every sweep would be a rejection STORM for every session that Director owns. Recorded
            // here, it gets ONE send per fold change instead. When that Director upgrades it RECONNECTS, which
            // drops its sessions from the fleet and prunes this gate (see Sweep), so the new code then
            // receives a fresh stamp. Only "no stream" (null) is retried, because that alone is transient
            // with no response to record.
            _lastSent[s.SessionId] = signature;
            if (result.Status != DirectorCommandStatus.Ok)
            {
                FileLog.Write($"[FleetDisplayStateObserver] sid={s.SessionId}: director={directorId} rejected fold (recorded, not re-stormed): {result.Status} {result.Error}");
                return;
            }
            FileLog.Write($"[FleetDisplayStateObserver] sid={s.SessionId}: fold '{s.EffectiveColor}'/'{s.StateLabel}' stamped down to director={directorId}");
        }
        catch (Exception ex)
        {
            // A boundary: fire-and-forget off the hub's push path, so a faulting send must not take down the
            // push that triggered it, and must not be recorded as delivered.
            FileLog.Write($"[FleetDisplayStateObserver] sid={s.SessionId}: fold stamp FAILED for director={directorId}: {ex.Message}");
        }
    }
}
