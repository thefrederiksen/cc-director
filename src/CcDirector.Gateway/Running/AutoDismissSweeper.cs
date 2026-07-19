using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Scheduled-run auto-dismiss (issue #1200): the Gateway-side actor that CLOSES an automated run once it has
/// declared it is done. On each sweep it reads the fresh pushed-session snapshot, selects the sessions that
/// are auto-dismiss AND carry the agent's <c>done</c> verdict AND have settled at a turn-end, and closes each
/// one by sending the <c>kill</c> verb DOWN that Director's stream (never the Director's REST Control API) -
/// the same graceful kill+remove the Cockpit close uses, so no phantom "interrupted" journal entry is left.
///
/// The verdict is what lets this override the dumb red "needs you" badge: a finished run always goes red on
/// the 10-second silence timer, but an explicit <c>done</c> means nothing actually needs the human, so the
/// session is closed rather than left cluttering the rail. A <c>needs-human</c> verdict (or no verdict) is
/// never closed - it stays open exactly like a normal session.
///
/// The whole feature rides the stream: with stream mode off there is no down-channel, so the Gateway does not
/// run this sweep at all (there is deliberately no REST fallback for the close).
/// </summary>
internal sealed class AutoDismissSweeper
{
    private readonly Func<IReadOnlyList<(string DirectorId, SessionDto Session)>> _snapshot;
    private readonly DirectorCommandRouter.SendDirectorCommandAsync _sendCommand;
    private readonly Func<string> _tenantKey;

    // Sessions we have already issued a kill for, so a session that lingers one extra sweep (before its
    // removal tombstone propagates up the stream) is not killed twice. Pruned opportunistically each sweep.
    //
    // Hosted Multi-Tenancy (session-serving PR2): keyed by (TENANT, session id), not session id alone. This
    // sweeper now runs ONE PASS PER TENANT, and each pass prunes marks against ITS OWN snapshot - so a
    // session-id-only key would have every tenant's pass delete every other tenant's marks, destroying the
    // duplicate-kill protection entirely, and would let two tenants that happen to use the same session id
    // suppress each other's close. The tenant prefix keeps each tenant's marks private to its own pass.
    private readonly ConcurrentDictionary<string, byte> _closing = new(StringComparer.Ordinal);

    /// <param name="snapshot">Returns the fresh pushed sessions across all stream-connected Directors (PushedSessionStore.SnapshotFresh).</param>
    /// <param name="sendCommand">The down-channel command sender (GatewayHost.SendCommandAsync); required - the feature only runs with the stream on.</param>
    /// <param name="tenantKey">Hosted Multi-Tenancy (session-serving PR2): identifies the tenant of the pass
    /// currently running, so the close-marks below are partitioned per tenant. Omitted (null) means the
    /// single-tenant shape - one constant partition - which is self-host and the unit tests.</param>
    public AutoDismissSweeper(
        Func<IReadOnlyList<(string DirectorId, SessionDto Session)>> snapshot,
        DirectorCommandRouter.SendDirectorCommandAsync sendCommand,
        Func<string>? tenantKey = null)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _sendCommand = sendCommand ?? throw new ArgumentNullException(nameof(sendCommand));
        _tenantKey = tenantKey ?? (static () => "local");
    }

    /// <summary>The close-mark key for a session within the tenant of the pass currently running.</summary>
    private string MarkKey(string sessionId) => _tenantKey() + "|" + sessionId;

    /// <summary>The verdict string (see <c>Session.DismissVerdict</c>) that authorizes an auto-close.</summary>
    public const string VerdictDone = "done";

    /// <summary>
    /// Select the sessions that should be auto-closed this pass. Pure and testable: a session qualifies when
    /// it is auto-dismiss, its verdict is <c>done</c>, and it has SETTLED at a turn-end - Running lifecycle,
    /// and an activity state of WaitingForInput or Idle (NOT Working: it may have started a fresh turn after
    /// emitting the verdict; NOT WaitingForPerm: a real permission prompt must never be steam-rolled). Already
    /// exited/exiting sessions and ones we have already issued a kill for are excluded.
    /// </summary>
    public static IReadOnlyList<(string DirectorId, SessionDto Session)> SelectDismissable(
        IEnumerable<(string DirectorId, SessionDto Session)> sessions,
        Func<string, bool>? alreadyClosing = null)
    {
        var picks = new List<(string, SessionDto)>();
        foreach (var (directorId, s) in sessions)
        {
            if (s is null || string.IsNullOrEmpty(s.SessionId))
                continue;
            if (!s.AutoDismiss)
                continue;
            if (!string.Equals(s.DismissVerdict, VerdictDone, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsSettledAtTurnEnd(s))
                continue;
            if (alreadyClosing is not null && alreadyClosing(s.SessionId))
                continue;
            picks.Add((directorId, s));
        }
        return picks;
    }

    private static bool IsSettledAtTurnEnd(SessionDto s)
    {
        // Only close a live session that is quietly waiting - not one mid-turn, mid-permission, or gone.
        if (!string.Equals(s.Status, "Running", StringComparison.OrdinalIgnoreCase))
            return false;
        return string.Equals(s.ActivityState, "WaitingForInput", StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.ActivityState, "Idle", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Run one sweep: select the dismissable sessions and close each over its Director's stream. Best-effort
    /// and isolated per session (one close failure never blocks the others). Returns the count closed. A
    /// session whose stream close returns null (no active stream) is left for a later sweep - the feature has
    /// no REST fallback by design.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var snapshot = _snapshot();
        PruneClosing(snapshot);

        var picks = SelectDismissable(snapshot, sid => _closing.ContainsKey(MarkKey(sid)));
        var closed = 0;
        foreach (var (directorId, session) in picks)
        {
            ct.ThrowIfCancellationRequested();
            // Mark before sending so a slow round-trip cannot let the next sweep issue a duplicate kill.
            _closing.TryAdd(MarkKey(session.SessionId), 0);
            try
            {
                var result = await DirectorCommandRouter.TrySendAsync(
                    _sendCommand, directorId, "kill", session.SessionId, payload: null, ct);
                if (result is null)
                {
                    // No active stream right now; drop the mark so a later sweep retries once the stream is back.
                    _closing.TryRemove(MarkKey(session.SessionId), out _);
                    FileLog.Write($"[AutoDismissSweeper] session={session.SessionId} director={directorId}: no stream, will retry");
                    continue;
                }
                if (result.Ok)
                {
                    closed++;
                    FileLog.Write($"[AutoDismissSweeper] closed session={session.SessionId} director={directorId} (verdict=done) over the stream");
                }
                else
                {
                    // A typed failure (e.g. NotFound = already gone) is terminal for this session; keep the
                    // mark so we do not hammer it, and let its removal tombstone drop it from the snapshot.
                    FileLog.Write($"[AutoDismissSweeper] session={session.SessionId} director={directorId}: close returned {result.Status}: {result.Error}");
                }
            }
            catch (Exception ex)
            {
                _closing.TryRemove(MarkKey(session.SessionId), out _);
                FileLog.Write($"[AutoDismissSweeper] close FAILED: session={session.SessionId} director={directorId}: {ex.Message}");
            }
        }

        if (closed > 0)
            FileLog.Write($"[AutoDismissSweeper] sweep closed {closed} auto-dismiss session(s)");
        return closed;
    }

    /// <summary>
    /// Forget close-marks for sessions no longer present in the snapshot (they are gone, or their Director
    /// dropped). Scoped to THIS PASS'S TENANT: the snapshot only ever contains that tenant's sessions, so
    /// pruning across the whole map would delete every other tenant's marks on every pass.
    /// </summary>
    private void PruneClosing(IReadOnlyList<(string DirectorId, SessionDto Session)> snapshot)
    {
        if (_closing.IsEmpty)
            return;
        var prefix = _tenantKey() + "|";
        var present = new HashSet<string>(snapshot.Select(t => MarkKey(t.Session.SessionId)), StringComparer.Ordinal);
        foreach (var key in _closing.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && !present.Contains(key))
                _closing.TryRemove(key, out _);
        }
    }
}
