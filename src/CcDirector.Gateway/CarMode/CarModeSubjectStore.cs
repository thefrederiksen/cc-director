using System.Collections.Concurrent;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.CarMode;

/// <summary>The session the owner is currently talking ABOUT - the resolved id plus the human name and
/// repository to say back. Enough for an act tool to act (by id) and to name what it did or is confirming,
/// with no re-resolve.</summary>
public sealed record CarModeSubject(string SessionId, string Name, string Repo);

/// <summary>
/// Per-device "current subject" for Car Mode (Car Mode mission, Voice-screen-actions phase, design B
/// approved by the Architect 2026-07-12). Any brain tool that resolves a session sets the subject here;
/// an act tool the owner phrases without naming a session ("read me the next one" then "answer it",
/// "snooze it") falls back to it. This is deterministic conversational state, not a guess left to the fast
/// model - the known Car Mode gotcha is context degrading across a multi-session triage loop, so the
/// server tracks who "it" is rather than trusting the model to carry the reference.
///
/// In-memory, keyed by the device credential (never crosses the wire), with the same idle time-to-live as
/// the conversation context so a device that stops talking does not keep a stale subject forever. The
/// subject is intentionally NOT used to make the destructive delete less safe: delete always speaks the
/// resolved name and repository back for a spoken confirmation, so a stale subject is heard before it acts.
/// Thread-safe: turns can arrive concurrently from the same device.
/// </summary>
public sealed class CarModeSubjectStore
{
    private sealed record Entry(CarModeSubject Subject, DateTime SetUtc);

    /// <summary>Drop a device's subject after this much idle time (matches the conversation context TTL).</summary>
    private static readonly TimeSpan IdleTtl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, Entry> _byDevice = new(StringComparer.Ordinal);
    private readonly Action<string> _log;

    public CarModeSubjectStore(Action<string>? log = null) => _log = log ?? FileLog.Write;

    /// <summary>Set the current subject for a device (called whenever a tool resolves a session), replacing
    ///  any prior one.</summary>
    public void Set(string deviceKey, CarModeSubject subject)
    {
        _byDevice[Normalize(deviceKey)] = new Entry(subject, DateTime.UtcNow);
        _log($"[CarModeSubject] subject set to {subject.Name}");
    }

    /// <summary>The current subject for a device, or null when none is set or it has gone idle past the TTL
    ///  (a fresh, bounded check on read - no timer).</summary>
    public CarModeSubject? Get(string deviceKey)
    {
        var key = Normalize(deviceKey);
        if (!_byDevice.TryGetValue(key, out var entry)) return null;
        if (DateTime.UtcNow - entry.SetUtc > IdleTtl)
        {
            _byDevice.TryRemove(key, out _);
            return null;
        }
        return entry.Subject;
    }

    /// <summary>Forget a device's subject (available for tests and a "start over").</summary>
    public void Clear(string deviceKey) => _byDevice.TryRemove(Normalize(deviceKey), out _);

    private static string Normalize(string? deviceKey) => string.IsNullOrWhiteSpace(deviceKey) ? "anonymous" : deviceKey;
}
