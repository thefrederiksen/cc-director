using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// launcher-persistent-join: the Gateway's map of which machine's cc-launcher is currently connected over a
/// persistent stream, keyed by <see cref="MachineKey"/> - the OWNING TENANT plus the machine name
/// (case-insensitive) - with the SignalR connection id as the value.
///
/// It is the launcher twin of the connection-tracking half of <see cref="PushedSessionStore"/>, but far
/// simpler: a launcher pushes no session state, so there is nothing to cache - only the live connection id
/// the Gateway addresses a command DOWN. One (tenant, machine) -> one active connection. A new connection
/// from the same machine under the same tenant supersedes the prior one; a different tenant's launcher for a
/// machine of the same bare name is a DIFFERENT entry and cannot supersede it - machine names are not unique
/// across tenants, so the tenant is half the key.
///
/// Thread-safe via a <see cref="ConcurrentDictionary{TKey,TValue}"/>. Registered as a DI singleton so the
/// hub (constructed per-invocation by SignalR's container) and <c>GatewayHost.SendLauncherCommandAsync</c>
/// share the one instance.
/// </summary>
public sealed class LauncherConnectionRegistry
{
    /// <summary>The composite key: a machine name is unique only WITHIN a tenant, so the tenant is part of
    /// the key. The machine name is canonicalized (trimmed, lower-cased) so keying is case-insensitive.</summary>
    private readonly record struct MachineKey(TenantId Tenant, string Machine);

    private static MachineKey Key(TenantId tenant, string machineName) =>
        new(tenant, machineName.Trim().ToLowerInvariant());

    private readonly ConcurrentDictionary<MachineKey, string> _byMachine = new();

    /// <summary>
    /// Mark <paramref name="connectionId"/> as the active stream connection for the owning tenant's
    /// <paramref name="machineName"/>, superseding any prior connection for that (tenant, machine).
    /// </summary>
    public void RegisterConnection(TenantId tenant, string machineName, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(machineName))
            throw new ArgumentException("machineName is required", nameof(machineName));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("connectionId is required", nameof(connectionId));

        _byMachine[Key(tenant, machineName)] = connectionId;
        FileLog.Write($"[LauncherConnectionRegistry] RegisterConnection: tenant={tenant.Value}, machine={machineName}, conn={Short(connectionId)} is now the active connection");
    }

    /// <summary>
    /// Clear the entry whose active connection is <paramref name="connectionId"/>. A late disconnect from a
    /// superseded connection removes nothing (the newer connection owns the (tenant, machine)), because the
    /// atomic key/value remove only succeeds when the stored connection id still matches. Scanning by the
    /// connection id needs no tenant - a connection id belongs to exactly one entry.
    /// </summary>
    public void Unregister(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return;

        foreach (var kv in _byMachine)
        {
            if (!string.Equals(kv.Value, connectionId, StringComparison.Ordinal))
                continue;

            if (((ICollection<KeyValuePair<MachineKey, string>>)_byMachine).Remove(kv))
                FileLog.Write($"[LauncherConnectionRegistry] Unregister: tenant={kv.Key.Tenant.Value}, machine={kv.Key.Machine}, conn={Short(connectionId)} cleared");
            else
                FileLog.Write($"[LauncherConnectionRegistry] Unregister IGNORED (superseded): tenant={kv.Key.Tenant.Value}, machine={kv.Key.Machine}, conn={Short(connectionId)}");
            return;
        }
    }

    /// <summary>
    /// The active stream connection id for the tenant's machine launcher, or null when none. The Gateway
    /// uses it to address a command DOWN the stream to that launcher - and only ever finds the caller's own.
    /// </summary>
    public string? GetActiveConnectionId(TenantId tenant, string machineName) =>
        _byMachine.TryGetValue(Key(tenant, machineName), out var connectionId) ? connectionId : null;

    /// <summary>True when this tenant's launcher for the machine currently has an active stream connection.</summary>
    public bool IsStreamConnected(TenantId tenant, string machineName) =>
        _byMachine.ContainsKey(Key(tenant, machineName));

    private static string Short(string? id) =>
        string.IsNullOrEmpty(id) ? "(none)" : (id.Length <= 8 ? id : id[..8]);
}
