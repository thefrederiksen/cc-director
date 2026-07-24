using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// The Gateway's in-memory cache of the repository/worktree snapshots each Director pushes up its
/// stream (repositories mission, devthrottle_internal#510 phase C) - the sibling of
/// <see cref="PushedSessionStore"/>, tenant-partitioned the same way: tenant first, then Director.
///
/// Acceptance rule (a simplified form of the session store's ownership discipline): a push from a
/// NEW connection always wins (a restarted Director reseeds authoritatively); a push from the SAME
/// connection must carry a higher sequence than the last accepted one (out-of-order or duplicate
/// pushes are dropped). Snapshots only - repositories change slowly; there is no delta path.
///
/// READ-ONLY at the Gateway: this cache exists so agents and the Cockpit can ASK about the fleet's
/// repositories in one call. Destructive actions always run on the owning Director after a live
/// re-verify - never from this relayed state (the trust rule).
/// </summary>
public sealed class PushedRepositoryStore
{
    private sealed class Entry
    {
        public string ConnectionId = "";
        public long LastSequence;
        public DateTime ReceivedAtUtc;
        public List<RepoStatusDto> Repositories = new();
    }

    private readonly ConcurrentDictionary<TenantId, ConcurrentDictionary<string, Entry>> _byTenant = new();

    /// <summary>Apply a full snapshot for a Director. Returns false when the push was rejected as stale.</summary>
    public bool ApplySnapshot(TenantId tenant, string directorId, string connectionId, long sequence, RepoStatusDto[] repositories)
    {
        var directors = _byTenant.GetOrAdd(tenant, _ => new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase));
        var entry = directors.GetOrAdd(directorId, _ => new Entry());
        lock (entry)
        {
            bool sameConnection = string.Equals(entry.ConnectionId, connectionId, StringComparison.Ordinal);
            if (sameConnection && sequence <= entry.LastSequence)
                return false; // duplicate or out-of-order from the same connection
            entry.ConnectionId = connectionId;
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
