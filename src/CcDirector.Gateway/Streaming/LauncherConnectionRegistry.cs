using System.Collections.Concurrent;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// launcher-persistent-join: the Gateway's map of which machine's cc-launcher is currently connected over a
/// persistent stream, keyed by machine name (case-insensitive) with the SignalR connection id as the value.
///
/// It is the launcher twin of the connection-tracking half of <see cref="PushedSessionStore"/>, but far
/// simpler: a launcher pushes no session state, so there is nothing to cache - only the live connection id
/// the Gateway addresses a command DOWN. One machine -> one active connection. A new connection from the
/// same machine (a launcher restart / reconnect) supersedes the prior one.
///
/// Thread-safe via a <see cref="ConcurrentDictionary{TKey,TValue}"/>. Registered as a DI singleton so the
/// hub (constructed per-invocation by SignalR's container) and <c>GatewayHost.SendLauncherCommandAsync</c>
/// share the one instance.
/// </summary>
public sealed class LauncherConnectionRegistry
{
    private readonly ConcurrentDictionary<string, string> _byMachine =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Mark <paramref name="connectionId"/> as the active stream connection for <paramref name="machineName"/>,
    /// superseding any prior connection for that machine (a reconnect wins).
    /// </summary>
    public void RegisterConnection(string machineName, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(machineName))
            throw new ArgumentException("machineName is required", nameof(machineName));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("connectionId is required", nameof(connectionId));

        _byMachine[machineName] = connectionId;
        FileLog.Write($"[LauncherConnectionRegistry] RegisterConnection: machine={machineName}, conn={Short(connectionId)} is now the active connection");
    }

    /// <summary>
    /// Clear the entry whose active connection is <paramref name="connectionId"/>. A late disconnect from a
    /// superseded connection removes nothing (the newer connection owns the machine), because the atomic
    /// key/value remove only succeeds when the stored connection id still matches.
    /// </summary>
    public void Unregister(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return;

        foreach (var kv in _byMachine)
        {
            if (!string.Equals(kv.Value, connectionId, StringComparison.Ordinal))
                continue;

            // Atomic compare-and-remove: only removes when the machine still maps to THIS connection id,
            // so a superseded connection's late disconnect cannot wipe a newer active connection.
            if (((ICollection<KeyValuePair<string, string>>)_byMachine).Remove(kv))
                FileLog.Write($"[LauncherConnectionRegistry] Unregister: machine={kv.Key}, conn={Short(connectionId)} cleared");
            else
                FileLog.Write($"[LauncherConnectionRegistry] Unregister IGNORED (superseded): machine={kv.Key}, conn={Short(connectionId)}");
            return;
        }
    }

    /// <summary>
    /// The active stream connection id for a machine's launcher, or null when none. The Gateway uses it to
    /// address a command DOWN the stream to that launcher.
    /// </summary>
    public string? GetActiveConnectionId(string machineName) =>
        _byMachine.TryGetValue(machineName, out var connectionId) ? connectionId : null;

    /// <summary>True when this machine's launcher currently has an active stream connection.</summary>
    public bool IsStreamConnected(string machineName) =>
        _byMachine.ContainsKey(machineName);

    private static string Short(string? id) =>
        string.IsNullOrEmpty(id) ? "(none)" : (id.Length <= 8 ? id : id[..8]);
}
