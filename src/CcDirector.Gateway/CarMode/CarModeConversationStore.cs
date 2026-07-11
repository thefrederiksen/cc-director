using System.Collections.Concurrent;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.CarMode;

/// <summary>One stored turn of a Car Mode conversation: a plain text message with its role
/// ("user" or "assistant"). Only clean final text is kept - never the intermediate tool_calls / tool
/// results - so the stored history can never break the strict tool-call/tool-result pairing the model
/// API requires, and a fresh tool call re-reads live fleet state each turn rather than replaying a stale
/// roster.</summary>
public sealed record CarModeMessage(string Role, string Content);

/// <summary>
/// Server-side, per-device Car Mode conversation context (Car Mode mission: "conversation context is
/// kept server-side, keyed by the caller's device, so multi-turn works"). The device's own credential
/// keys the history so "how many need me" then "show me the latest one" resolve, and no history ever
/// crosses the wire. In-memory by design (a Gateway restart simply starts a fresh conversation), bounded
/// per device, and swept by idle age so a device that stops talking does not retain context forever.
/// Thread-safe: turns can arrive concurrently from the same device.
/// </summary>
public sealed class CarModeConversationStore
{
    private sealed class Conversation
    {
        public readonly List<CarModeMessage> Messages = new();
        public DateTime LastUsedUtc = DateTime.UtcNow;
    }

    /// <summary>Keep the last N messages (roughly N/2 exchanges) so context stays useful without growing
    ///  unbounded; older turns fall off the front.</summary>
    private const int MaxMessages = 16;

    /// <summary>Drop a device's context after this much idle time.</summary>
    private static readonly TimeSpan IdleTtl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, Conversation> _byDevice = new(StringComparer.Ordinal);
    private readonly Action<string> _log;

    public CarModeConversationStore(Action<string>? log = null) => _log = log ?? FileLog.Write;

    /// <summary>The prior messages for a device, oldest first, for prepending to this turn's request.
    ///  Returns an empty list for an unknown or expired device.</summary>
    public IReadOnlyList<CarModeMessage> GetHistory(string deviceKey)
    {
        SweepExpired();
        if (_byDevice.TryGetValue(Normalize(deviceKey), out var convo))
        {
            lock (convo)
            {
                convo.LastUsedUtc = DateTime.UtcNow;
                return convo.Messages.ToList();
            }
        }
        return Array.Empty<CarModeMessage>();
    }

    /// <summary>Record a completed exchange (the owner's command and the assistant's final spoken reply)
    ///  for a device, trimming to the most recent <see cref="MaxMessages"/>.</summary>
    public void Append(string deviceKey, string userText, string assistantText)
    {
        var key = Normalize(deviceKey);
        var convo = _byDevice.GetOrAdd(key, _ => new Conversation());
        lock (convo)
        {
            convo.Messages.Add(new CarModeMessage("user", userText));
            convo.Messages.Add(new CarModeMessage("assistant", assistantText));
            if (convo.Messages.Count > MaxMessages)
                convo.Messages.RemoveRange(0, convo.Messages.Count - MaxMessages);
            convo.LastUsedUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Forget a device's context (used by a "start over" request, and available for tests).</summary>
    public void Clear(string deviceKey) => _byDevice.TryRemove(Normalize(deviceKey), out _);

    private void SweepExpired()
    {
        var cutoff = DateTime.UtcNow - IdleTtl;
        foreach (var kv in _byDevice)
        {
            bool expired;
            lock (kv.Value) expired = kv.Value.LastUsedUtc < cutoff;
            if (expired && _byDevice.TryRemove(kv.Key, out _))
                _log($"[CarModeConversation] expired idle context for a device");
        }
    }

    // A blank credential (auth-off debug mode) keys one shared anonymous context rather than throwing.
    private static string Normalize(string? deviceKey) => string.IsNullOrWhiteSpace(deviceKey) ? "anonymous" : deviceKey;
}
