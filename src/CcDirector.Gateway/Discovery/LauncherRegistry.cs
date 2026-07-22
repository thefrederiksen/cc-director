using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// In-memory registry of live cc-launcher processes.
///
/// A launcher POSTs <see cref="LauncherRegistrationRequest"/> to /launchers/register on
/// startup and heartbeats every 30 s. The Gateway relay uses the stored port + token to
/// forward lifecycle verbs to the remote launcher's loopback REST API.
///
/// Keys are the composite <see cref="LauncherKey"/> - the OWNING TENANT plus the machine name
/// (case-insensitive). A machine name is unique only within a tenant, so the tenant is half the key:
/// one tenant naming a machine can only ever reach its OWN launcher entry, never another tenant's launcher
/// registered under the same bare machine name. This mirrors the composite (tenant, id) keying of
/// <see cref="DirectorRegistry"/>.
///
/// Issue #331.
/// </summary>
public sealed class LauncherRegistry
{
    /// <summary>How long without a heartbeat before a launcher is considered stale and swept.</summary>
    public static TimeSpan HeartbeatTimeout { get; } = TimeSpan.FromSeconds(90);

    /// <summary>The composite key: the owning tenant plus the machine name (canonicalized, case-insensitive).</summary>
    private readonly record struct LauncherKey(TenantId Tenant, string Machine);

    private static LauncherKey Key(TenantId tenant, string machineName) =>
        new(tenant, machineName.Trim().ToLowerInvariant());

    private sealed class Entry
    {
        public required LauncherDto Dto;
        /// <summary>The launcher's Bearer token (NOT exposed in <see cref="LauncherDto"/>).</summary>
        public required string Token;
        public DateTime LastSeenAt;
    }

    private readonly ConcurrentDictionary<LauncherKey, Entry> _launchers = new();

    private Timer? _sweepTimer;

    /// <summary>
    /// Register or refresh the calling tenant's launcher. Returns the stored <see cref="LauncherDto"/> for
    /// the 201/200 response body (no token exposed).
    /// </summary>
    public LauncherDto Upsert(TenantId tenant, LauncherRegistrationRequest req)
    {
        var now = DateTime.UtcNow;
        var dto = new LauncherDto
        {
            MachineName = req.MachineName,
            Port = req.Port,
            NetworkAddress = req.NetworkAddress ?? "",
            Pid = req.Pid,
            Version = req.Version,
            StartedAt = req.StartedAt,
            LastSeenAt = now,
        };

        _launchers[Key(tenant, req.MachineName)] = new Entry
        {
            Dto = dto,
            Token = req.Token,
            LastSeenAt = now,
        };

        FileLog.Write($"[LauncherRegistry] Upsert: tenant={tenant.Value}, machine={req.MachineName}, port={req.Port}, networkAddress={req.NetworkAddress}, pid={req.Pid}, version={req.Version}");
        return dto;
    }

    /// <summary>
    /// Record a heartbeat from a known launcher of the calling tenant. Returns true if found;
    /// false (-> 410) if unknown (it should re-register).
    /// </summary>
    public bool Heartbeat(TenantId tenant, string machineName)
    {
        if (!_launchers.TryGetValue(Key(tenant, machineName), out var entry))
        {
            FileLog.Write($"[LauncherRegistry] Heartbeat: unknown tenant={tenant.Value}, machine={machineName} -> 410");
            return false;
        }

        var now = DateTime.UtcNow;
        entry.LastSeenAt = now;
        entry.Dto.LastSeenAt = now;
        FileLog.Write($"[LauncherRegistry] Heartbeat: tenant={tenant.Value}, machine={machineName}");
        return true;
    }

    /// <summary>
    /// Remove the calling tenant's launcher entry (called on graceful launcher shutdown).
    /// </summary>
    public void Remove(TenantId tenant, string machineName)
    {
        if (_launchers.TryRemove(Key(tenant, machineName), out _))
            FileLog.Write($"[LauncherRegistry] Remove: tenant={tenant.Value}, machine={machineName}");
    }

    /// <summary>
    /// Look up the calling tenant's launcher by machine name. Returns null if not registered.
    /// </summary>
    public LauncherDto? Get(TenantId tenant, string machineName) =>
        _launchers.TryGetValue(Key(tenant, machineName), out var e) ? e.Dto : null;

    /// <summary>
    /// Retrieve the relay token for the calling tenant's launcher. Returns null if not registered.
    /// </summary>
    public string? GetToken(TenantId tenant, string machineName) =>
        _launchers.TryGetValue(Key(tenant, machineName), out var e) ? e.Token : null;

    /// <summary>
    /// Retrieve the network address for the calling tenant's launcher (tailnet hostname or IP).
    /// Empty string when co-located with the Gateway (loopback); null when not registered.
    /// </summary>
    public string? GetNetworkAddress(TenantId tenant, string machineName) =>
        _launchers.TryGetValue(Key(tenant, machineName), out var e) ? e.Dto.NetworkAddress : null;

    /// <summary>
    /// The calling tenant's registered launchers, as public DTOs (no tokens). Never another tenant's.
    /// </summary>
    public IReadOnlyList<LauncherDto> ListLaunchers(TenantId tenant) =>
        _launchers
            .Where(kv => kv.Key.Tenant.Equals(tenant))
            .Select(kv => kv.Value.Dto)
            .ToList();

    /// <summary>
    /// Start the periodic stale-entry sweep (removes launchers that have not heartbeat
    /// within <see cref="HeartbeatTimeout"/>). Tenant-agnostic: it removes stale entries by full key.
    /// </summary>
    public void StartSweep()
    {
        _sweepTimer = new Timer(Sweep, null,
            dueTime: TimeSpan.FromSeconds(30),
            period: TimeSpan.FromSeconds(30));
        FileLog.Write("[LauncherRegistry] Sweep timer started (30s interval)");
    }

    private void Sweep(object? _)
    {
        var cutoff = DateTime.UtcNow - HeartbeatTimeout;
        foreach (var kv in _launchers)
        {
            if (kv.Value.LastSeenAt < cutoff)
            {
                if (_launchers.TryRemove(kv.Key, out var removed))
                    FileLog.Write($"[LauncherRegistry] Sweep: removed stale launcher tenant={kv.Key.Tenant.Value}, machine={kv.Key.Machine} (last-seen={removed.LastSeenAt:o})");
            }
        }
    }

    public void Dispose()
    {
        _sweepTimer?.Dispose();
    }
}
