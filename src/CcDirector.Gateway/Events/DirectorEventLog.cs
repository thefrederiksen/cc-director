using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Events;

/// <summary>
/// The minimal Phase-1 observable sink for the doorbell event vocabulary (issue #330):
/// a per-director capped ring of received events (newest kept, oldest dropped) plus one
/// structured log line per event. This is deliberately NOT the Phase-3 event hub - no
/// subscriptions, no push, no persistence; just enough that "the Gateway received
/// session-created/session-exited/prompt-detected" is provable from the outside
/// (GET /directors/{id}/events) and from the Gateway log.
///
/// MTR-01 (Codex round 1): the ring is keyed by the COMPOSITE (owning tenant, director id), not by the bare
/// director id. Before this, the ring was one global queue per bare id, so tenant A's cron completions and
/// tenant B's doorbell events for the SAME director id shared one queue - and a hosted account that registered
/// a Local shadow of another account's id could read (or inject into) that shared queue through
/// GET /directors/{id}/events. Keying by (tenant, id) makes A's ring and B's ring for the same id physically
/// distinct queues, so a cross-tenant read or inject is STRUCTURALLY impossible, not merely refused. Self-host
/// is unchanged: there is exactly one tenant (Local), so the composite key degenerates to the bare id it was.
/// </summary>
public sealed class DirectorEventLog
{
    /// <summary>Ring capacity per director - enough to debug a busy Director without growing unbounded.</summary>
    public const int MaxEventsPerDirector = 200;

    /// <summary>
    /// The ring key. Composite - the owning tenant AND the director id - so a producer or reader naming a
    /// director id can only ever reach the queue belonging to the tenant it carries. Matches the composite
    /// keying <see cref="Discovery.DirectorRegistry"/> adopted for the registry entries themselves (#1847).
    /// </summary>
    private readonly record struct RingKey(TenantId Tenant, string DirectorId);

    private readonly ConcurrentDictionary<RingKey, Queue<DirectorEventDto>> _rings = new();

    /// <summary>
    /// Record one received event under its owning tenant and write the structured log line. The
    /// <paramref name="tenant"/> is the tenant of the unit of work that produced the event (the doorbell
    /// leg's Local tenant, the cron pass's tenant, ...) and is REQUIRED - it is half of the ring key, so a
    /// producer can never file into another tenant's ring.
    /// </summary>
    public void Record(TenantId tenant, string directorId, string sessionId, string eventName, string state)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("a valid tenant is required", nameof(tenant));
        if (string.IsNullOrEmpty(directorId))
            throw new ArgumentException("directorId is required", nameof(directorId));
        if (string.IsNullOrEmpty(eventName))
            throw new ArgumentException("eventName is required", nameof(eventName));

        var dto = new DirectorEventDto
        {
            ReceivedAt = DateTime.UtcNow,
            SessionId = sessionId,
            Event = eventName,
            State = state,
        };

        var ring = _rings.GetOrAdd(new RingKey(tenant, directorId), _ => new Queue<DirectorEventDto>());
        lock (ring)
        {
            ring.Enqueue(dto);
            while (ring.Count > MaxEventsPerDirector)
                ring.Dequeue();
        }

        // The structured line the issue's acceptance criteria can be proven against.
        FileLog.Write($"[DirectorEvents] tenant={tenant.Value} director={directorId} session={sessionId} event={eventName} state={state}");
    }

    /// <summary>
    /// Snapshot of a director's recorded events for ONE tenant, oldest first. Empty when none. A reader only
    /// ever sees its own tenant's ring for the id - another tenant's ring for the same id is a different queue.
    /// </summary>
    public IReadOnlyList<DirectorEventDto> For(TenantId tenant, string directorId)
    {
        if (!tenant.IsValid || string.IsNullOrEmpty(directorId)
            || !_rings.TryGetValue(new RingKey(tenant, directorId), out var ring))
            return Array.Empty<DirectorEventDto>();
        lock (ring)
        {
            return ring.ToList();
        }
    }
}
