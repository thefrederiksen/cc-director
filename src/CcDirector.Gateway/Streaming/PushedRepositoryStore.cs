using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// The Gateway's in-memory cache of the repository/worktree snapshots each Director pushes up its
/// stream (repositories mission, devthrottle_internal#510 phase C) - the sibling of
/// <see cref="PushedSessionStore"/>, tenant-partitioned the same way: tenant first, then Director.
///
/// Acceptance rule (the session store's ownership discipline): only the Director's CURRENT stream
/// connection - the one bound at Hello, the same identity command routing trusts - may push. A push
/// from any other connection is dropped, so a late push from a superseded connection can never
/// overwrite the current connection's newer data. Within the current connection a push must carry a
/// higher sequence than the last accepted one (out-of-order or duplicate pushes are dropped).
/// Snapshots only - repositories change slowly; there is no delta path.
///
/// READ-ONLY at the Gateway: this cache exists so agents and the Cockpit can ASK about the fleet's
/// repositories in one call. Destructive actions always run on the owning Director after a live
/// re-verify - never from this relayed state (the trust rule).
/// </summary>
public sealed class PushedRepositoryStore
{
    private sealed class Entry
    {
        public string? ActiveConnectionId;

        // Connection RECENCY (issue 516). Each connection is stamped with a monotonically increasing
        // epoch the first time it registers. The current owner is the highest epoch seen among live
        // connections; a superseded (lower-epoch) connection re-Helloing can never reclaim ownership,
        // so two overlapping connections cannot alternate the authoritative snapshot indefinitely.
        public long CurrentEpoch;                 // 0 = no owner
        public long NextEpoch = 1;
        public readonly Dictionary<string, long> ConnectionEpochs = new(StringComparer.Ordinal);

        public long LastSequence = -1;
        public DateTime ReceivedAtUtc;
        public List<RepoStatusDto> Repositories = new();
    }

    private readonly ConcurrentDictionary<TenantId, ConcurrentDictionary<string, Entry>> _byTenant = new();

    /// <summary>
    /// Mark <paramref name="connectionId"/> as the Director's current stream connection - the hub
    /// calls this at Hello, the same moment the session store learns it. The sequence baseline
    /// resets ONLY when ownership actually changes to a different connection (so the new
    /// connection's first push, at any sequence, is authoritative). A repeated Hello from the
    /// CURRENT connection keeps the existing baseline (ruling R2-4) - resetting it would let an
    /// older sequence replay over newer data.
    /// </summary>
    public void RegisterConnection(TenantId tenant, string directorId, string connectionId)
    {
        var directors = _byTenant.GetOrAdd(tenant, _ => new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase));
        var entry = directors.GetOrAdd(directorId, _ => new Entry());
        lock (entry)
        {
            if (!entry.ConnectionEpochs.TryGetValue(connectionId, out var epoch))
            {
                epoch = entry.NextEpoch++;
                entry.ConnectionEpochs[connectionId] = epoch;
            }

            if (string.Equals(entry.ActiveConnectionId, connectionId, StringComparison.Ordinal))
            {
                FileLog.Write($"[PushedRepositoryStore] repeated Hello from the current connection - sequence baseline kept: director={directorId}");
                return;
            }

            if (epoch < entry.CurrentEpoch)
            {
                // A SUPERSEDED connection re-Helloing (its periodic reseed) must NOT reclaim
                // ownership (issue 516): otherwise two overlapping connections alternate the
                // authoritative snapshot forever. It keeps its older epoch and stays superseded.
                FileLog.Write($"[PushedRepositoryStore] Hello IGNORED from a superseded connection (epoch {epoch} < current {entry.CurrentEpoch}): director={directorId}");
                return;
            }

            // A genuinely newer connection takes ownership and starts a fresh sequence baseline.
            entry.ActiveConnectionId = connectionId;
            entry.CurrentEpoch = epoch;
            entry.LastSequence = -1;
        }
    }

    /// <summary>
    /// Clear the current connection IF <paramref name="connectionId"/> is still it. A late
    /// disconnect from a superseded connection is ignored so it cannot wipe a newer one.
    /// </summary>
    public void UnregisterConnection(TenantId tenant, string directorId, string connectionId)
    {
        if (!_byTenant.TryGetValue(tenant, out var directors) || !directors.TryGetValue(directorId, out var entry))
            return;
        lock (entry)
        {
            entry.ConnectionEpochs.Remove(connectionId);
            if (string.Equals(entry.ActiveConnectionId, connectionId, StringComparison.Ordinal))
            {
                entry.ActiveConnectionId = null;
                // Drop the ownership epoch so a still-live older connection can reclaim on its next
                // Hello - it was only held back while a newer connection was present.
                entry.CurrentEpoch = 0;
            }
        }
    }

    /// <summary>
    /// Apply a full snapshot for a Director. Returns false when the push was rejected: it did not
    /// come from the Director's current connection, or its sequence is stale.
    /// </summary>
    public bool ApplySnapshot(TenantId tenant, string directorId, string connectionId, long sequence, RepoStatusDto[] repositories)
    {
        if (!_byTenant.TryGetValue(tenant, out var directors) || !directors.TryGetValue(directorId, out var entry))
            return false; // never registered (no Hello bound this Director) - nothing may push

        lock (entry)
        {
            if (!string.Equals(entry.ActiveConnectionId, connectionId, StringComparison.Ordinal))
            {
                FileLog.Write($"[PushedRepositoryStore] push REJECTED (not the current connection): director={directorId}, seq={sequence}");
                return false; // a superseded (or unknown) connection may never overwrite current data
            }
            if (sequence <= entry.LastSequence)
                return false; // duplicate or out-of-order from the current connection
            entry.LastSequence = sequence;
            entry.ReceivedAtUtc = DateTime.UtcNow;
            entry.Repositories = repositories.ToList();
        }
        return true;
    }

    /// <summary>
    /// The repositories a Director last pushed, or null when it never pushed or its data is older
    /// than <paramref name="staleAfter"/>. Also returns the data age for honesty at the surface.
    /// </summary>
    public (IReadOnlyList<RepoStatusDto> Repositories, double DataAgeSeconds)? TryGetFresh(TenantId tenant, string directorId, TimeSpan staleAfter)
    {
        if (!_byTenant.TryGetValue(tenant, out var directors) || !directors.TryGetValue(directorId, out var entry))
            return null;
        lock (entry)
        {
            var age = DateTime.UtcNow - entry.ReceivedAtUtc;
            if (age > staleAfter)
                return null;
            // Shallow copy of the list; DTOs are treated as immutable after receive.
            return (entry.Repositories.ToList(), age.TotalSeconds);
        }
    }

    /// <summary>Director ids that have pushed repositories for this tenant.</summary>
    public IReadOnlyList<string> DirectorIdsFor(TenantId tenant)
        => _byTenant.TryGetValue(tenant, out var directors) ? directors.Keys.ToList() : Array.Empty<string>();
}
