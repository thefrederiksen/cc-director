using System.Collections.Concurrent;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.CarMode;

/// <summary>A destructive action armed and waiting for the owner's spoken confirmation: which tool, the
/// resolved session id it will act on, and the human name to say back. Addressed by id (not re-resolved)
/// so the confirmation deletes exactly the session the owner was told about.</summary>
public sealed record CarModePendingAction(string Tool, string SessionId, string TargetName);

/// <summary>
/// Per-device store of the ONE destructive action currently held for a spoken confirmation (Car Mode
/// mission, decision 3). The brain arms it when the model calls a destructive tool and does not execute;
/// the next turn either confirms (execute) or does not (drop). In-memory, keyed by the device credential,
/// with a short TTL so a forgotten confirmation disarms itself rather than lingering as a live delete.
/// </summary>
public sealed class CarModePendingStore
{
    private sealed record Entry(CarModePendingAction Action, DateTime ArmedUtc);

    /// <summary>A held confirmation older than this is treated as disarmed (the owner moved on). Short,
    ///  because an armed destructive action must not survive a conversation.</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Entry> _byDevice = new(StringComparer.Ordinal);
    private readonly Action<string> _log;

    public CarModePendingStore(Action<string>? log = null) => _log = log ?? FileLog.Write;

    /// <summary>Arm a destructive action for a device, replacing any prior one.</summary>
    public void Arm(string deviceKey, CarModePendingAction action)
    {
        _byDevice[Normalize(deviceKey)] = new Entry(action, DateTime.UtcNow);
        _log($"[CarModePending] armed {action.Tool} for {action.TargetName}");
    }

    /// <summary>The armed action for a device, or null when none is armed or it has expired (a fresh,
    ///  bounded TTL check on read - no timer).</summary>
    public CarModePendingAction? Get(string deviceKey)
    {
        var key = Normalize(deviceKey);
        if (!_byDevice.TryGetValue(key, out var entry)) return null;
        if (DateTime.UtcNow - entry.ArmedUtc > Ttl)
        {
            _byDevice.TryRemove(key, out _);
            return null;
        }
        return entry.Action;
    }

    /// <summary>Disarm a device's held action (after it is confirmed, cancelled, or superseded).</summary>
    public void Clear(string deviceKey) => _byDevice.TryRemove(Normalize(deviceKey), out _);

    private static string Normalize(string? deviceKey) => string.IsNullOrWhiteSpace(deviceKey) ? "anonymous" : deviceKey;
}
