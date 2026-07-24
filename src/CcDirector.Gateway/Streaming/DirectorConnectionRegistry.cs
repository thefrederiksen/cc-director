using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Tenant-indexed live Director tunnel connections, each with a server-side abort callback. DirectorHub
/// populates it on Hello and clears it on disconnect. The cancellation cutoff (MTR-15) calls
/// <see cref="AbortForTenant"/> to sever every live tunnel a just-revoked tenant holds ON THIS Gateway
/// instance: the durable device tombstone already denies any NEW authentication, and this ends the
/// connections that are already up so a cancelled customer's active session actually stops.
///
/// Per-instance: each Gateway replica tracks only its own connections and aborts only those; a tenant whose
/// tunnels are spread across replicas has each replica's own monitor read NotEntitled and abort its local
/// connections within the sweep bound. Idempotent and race-safe: a disconnect that removes an entry, or a
/// second abort, is a harmless no-op. No PII is logged - only counts and the fixed reason.
/// </summary>
public sealed class DirectorConnectionRegistry
{
    private readonly ConcurrentDictionary<string, Entry> _byConnection = new(StringComparer.Ordinal);

    private readonly record struct Entry(string Tenant, Action Abort);

    /// <summary>Index a live connection under its tenant with the server-side abort to end it later.</summary>
    public void Register(TenantId tenant, string connectionId, Action abort)
    {
        if (!tenant.IsValid || string.IsNullOrEmpty(connectionId) || abort is null)
            return;
        _byConnection[connectionId] = new Entry(tenant.Value, abort);
    }

    /// <summary>Drop a connection's entry (on disconnect). Idempotent.</summary>
    public void Unregister(string connectionId)
    {
        if (!string.IsNullOrEmpty(connectionId))
            _byConnection.TryRemove(connectionId, out _);
    }

    /// <summary>Abort every live connection this instance holds for the tenant. Returns the count aborted.
    /// Idempotent: connections already gone are simply not present.</summary>
    public int AbortForTenant(TenantId tenant, string reason)
    {
        if (!tenant.IsValid)
            return 0;

        var key = tenant.Value;
        var aborted = 0;
        foreach (var kv in _byConnection)
        {
            if (!string.Equals(kv.Value.Tenant, key, StringComparison.Ordinal))
                continue;
            if (_byConnection.TryRemove(kv.Key, out var entry))
            {
                try { entry.Abort(); aborted++; }
                catch (Exception ex) { FileLog.Write($"[DirectorConnectionRegistry] abort failed for a connection, continuing ({ex.GetType().Name})"); }
            }
        }

        if (aborted > 0)
            FileLog.Write($"[DirectorConnectionRegistry] aborted {aborted} live Director connection(s) for a tenant (reason={reason})");
        return aborted;
    }
}
