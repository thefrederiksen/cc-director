using CcDirector.Core.Configuration;

namespace CcDirector.Core.Fleet;

/// <summary>The reason a <see cref="MessageSteward"/> allowed or dropped an outgoing fleet message.</summary>
public enum StewardOutcome
{
    /// <summary>Delivered - passed every guard.</summary>
    Allowed,
    /// <summary>An exact duplicate within the dedupe window - safely suppressed.</summary>
    DuplicateSuppressed,
    /// <summary>The source exceeded its per-minute per-target message cap.</summary>
    RateLimited,
    /// <summary>The source exceeded its per-minute broadcast cap.</summary>
    BroadcastThrottled,
}

/// <summary>One steward verdict. <see cref="Allowed"/> is false for every drop; <see cref="Reason"/> is a
/// human-readable message the caller MUST surface to the sender (never a silent drop).</summary>
public sealed record StewardDecision(bool Allowed, StewardOutcome Outcome, string? Reason)
{
    /// <summary>The "delivered, no guard tripped" verdict.</summary>
    public static readonly StewardDecision Ok = new(true, StewardOutcome.Allowed, null);
}

/// <summary>
/// The fleet-message steward (flag: <c>messaging.steward</c>). Placed at the SENDER's Director so it sees
/// 100% of a session's OUTGOING fleet messages - local AND remote - and never touches normal user typing
/// (the <c>/fleet/*</c> paths are dedicated). Pure in-memory, thread-safe, and clock-injectable so the
/// windows are deterministically testable. A shared component: reusable at the Gateway later for
/// cross-machine policy.
///
/// Guards, all keyed on the source session:
///  - DEDUPE: an exact-duplicate (source + target + text) within <see cref="MessageStewardOptions.DedupeWindowMs"/>
///    is a SAFE drop (also absorbs a retry loop). The window slides on each repeat, so a continuous loop
///    stays suppressed until it pauses for at least the window.
///  - RATE-LIMIT: a per-source rolling-60s cap on per-target messages (send + ask).
///  - BROADCAST THROTTLE: a per-source rolling-60s cap on broadcasts (tighter - a broadcast fans out).
///
/// Every drop is a <see cref="StewardDecision"/> the caller surfaces to the sender and logs; nothing is
/// dropped silently.
/// </summary>
public sealed class MessageSteward
{
    private readonly MessageStewardOptions _options;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _lastSeen = new(StringComparer.Ordinal);        // dedupe key -> last time
    private readonly Dictionary<string, Queue<DateTime>> _sends = new(StringComparer.Ordinal);     // per-source send timestamps
    private readonly Dictionary<string, Queue<DateTime>> _broadcasts = new(StringComparer.Ordinal); // per-source broadcast timestamps

    // Key = source|target|text. The first two segments are GUID-shaped (source) or a GUID / "*" (target),
    // neither of which can contain '|', so the segment boundaries stay unambiguous even though the trailing
    // text may contain '|'.
    private const char KeySep = '|';

    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(60);

    /// <param name="utcNow">Test seam for the windows; production passes null and the steward reads UTC now.</param>
    public MessageSteward(MessageStewardOptions options, Func<DateTime>? utcNow = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Whether the steward is enforcing. When false every check returns <see cref="StewardDecision.Ok"/>.</summary>
    public bool Enabled => _options.Enabled;

    /// <summary>Check an outgoing per-target message (a send or an ask): dedupe, then the per-source rate limit.</summary>
    public StewardDecision CheckMessage(string? fromSessionId, string? toSessionId, string? text)
        => Check(fromSessionId, toSessionId ?? "", text ?? "", _sends, _options.PerSourcePerMin, isBroadcast: false);

    /// <summary>Check an outgoing broadcast: dedupe (target is "*"), then the per-source broadcast throttle.</summary>
    public StewardDecision CheckBroadcast(string? fromSessionId, string? text)
        => Check(fromSessionId, "*", text ?? "", _broadcasts, _options.BroadcastsPerMin, isBroadcast: true);

    private StewardDecision Check(string? fromSessionId, string target, string text,
        Dictionary<string, Queue<DateTime>> counters, int perMinuteCap, bool isBroadcast)
    {
        if (!_options.Enabled) return StewardDecision.Ok;
        // Without a source id we cannot key per-session policy; let it through (its framing is generic anyway).
        if (string.IsNullOrWhiteSpace(fromSessionId)) return StewardDecision.Ok;

        var now = _utcNow();
        lock (_gate)
        {
            // 1. Dedupe (safe drop). Checked FIRST so a retry loop is suppressed and never counts as a flood.
            //    The window slides on each repeat, so a continuous loop stays suppressed.
            var dedupeKey = fromSessionId + KeySep + target + KeySep + text;
            if (_lastSeen.TryGetValue(dedupeKey, out var last)
                && (now - last).TotalMilliseconds < _options.DedupeWindowMs)
            {
                _lastSeen[dedupeKey] = now; // slide the window
                return new StewardDecision(false, StewardOutcome.DuplicateSuppressed,
                    "duplicate suppressed (an identical message was just sent)");
            }

            // 2. Per-source rolling-60s cap.
            if (perMinuteCap > 0)
            {
                if (!counters.TryGetValue(fromSessionId, out var q))
                    counters[fromSessionId] = q = new Queue<DateTime>();
                while (q.Count > 0 && (now - q.Peek()) >= RateWindow) q.Dequeue();
                if (q.Count >= perMinuteCap)
                    return isBroadcast
                        ? new StewardDecision(false, StewardOutcome.BroadcastThrottled,
                            $"broadcast throttled ({perMinuteCap} broadcasts/min per session)")
                        : new StewardDecision(false, StewardOutcome.RateLimited,
                            $"rate limited ({perMinuteCap} messages/min per session)");
                q.Enqueue(now);
            }

            // Allowed: record it for dedupe, and bound the dedupe map.
            _lastSeen[dedupeKey] = now;
            PruneDedupe(now);
            return StewardDecision.Ok;
        }
    }

    // Bound the dedupe map: once it grows, drop entries older than the window (they can no longer suppress
    // anything). Called under _gate.
    private void PruneDedupe(DateTime now)
    {
        if (_lastSeen.Count < 256) return; // cheap guard - only sweep when it grows
        var stale = _lastSeen
            .Where(kv => (now - kv.Value).TotalMilliseconds >= _options.DedupeWindowMs)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var k in stale) _lastSeen.Remove(k);
    }
}
