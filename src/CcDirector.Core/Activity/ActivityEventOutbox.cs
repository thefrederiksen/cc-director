using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Activity;

/// <summary>
/// The Director's durable activity-event outbox (docs/PLAN-trustworthy-working-start-2026-07-24.md,
/// increment 2). Activity evidence cannot always be reconstructed from an agent transcript, so "keep the
/// history" requires the events to survive a Director crash between capture and Gateway acknowledgement -
/// an in-memory retry queue is not enough. The GATEWAY remains the only durable history; this file is
/// nothing but the not-yet-acknowledged tail, and an acknowledged event is deleted here the moment the
/// Gateway confirms it holds it.
///
/// MINTED ONCE: <see cref="Enqueue"/> stamps the event's id (a fresh GUID unless the producer supplied a
/// deterministic one) and this Director's monotonic sequence exactly once, BEFORE the event is persisted.
/// A retried batch replays the same identities, which is what makes the Gateway's append idempotent - a
/// crash between push and acknowledgement re-sends the same events and the Gateway answers "duplicates",
/// never a second row.
///
/// Format: one JSON <see cref="ActivityEventRecord"/> per line at <see cref="CcStorage.ActivityOutbox"/>.
/// Enqueue appends one line (cheap, called from event threads); acknowledgement rewrites the file
/// atomically (temp file + move, the <c>IngestState</c> pattern). Load-on-start restores the pending tail
/// and resumes the sequence from the highest value seen; a corrupt line is logged and skipped, never
/// fatal - one bad line must not cost the rest of the evidence.
///
/// BOUNDED: a Gateway that stays unreachable must not grow this file forever. Past
/// <see cref="MaxPending"/> events the OLDEST are dropped, loudly. That is a deliberate, visible safety
/// valve on delivery state - losing the oldest shadow evidence in a multi-day outage is acceptable;
/// filling the disk is not.
/// </summary>
public sealed class ActivityEventOutbox
{
    /// <summary>The hard ceiling on pending events; beyond it the oldest are dropped loudly.</summary>
    public const int MaxPending = 20_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<ActivityEventRecord> _pending = new();
    private long _lastSequence;

    /// <param name="path">The outbox file. Defaults to <see cref="CcStorage.ActivityOutbox"/>; tests pass
    /// an isolated path.</param>
    public ActivityEventOutbox(string? path = null) : this(path, MaxPending)
    {
    }

    /// <summary>Test seam: a small cap so the overflow valve is provable without 20,000 writes.</summary>
    internal ActivityEventOutbox(string? path, int maxPending)
    {
        _maxPending = maxPending;
        _path = string.IsNullOrWhiteSpace(path) ? CcStorage.ActivityOutbox() : path!;
        lock (_gate)
            Load();
    }

    private readonly int _maxPending;

    /// <summary>How many events are waiting for acknowledgement right now.</summary>
    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <summary>
    /// Mint the event's identity and persist it. <paramref name="record"/> arrives with
    /// <c>DirectorSequence = 0</c> and either <see cref="Guid.Empty"/> (mint a fresh id) or a
    /// producer-supplied DETERMINISTIC id (kept as-is - the transcript observer derives ids from content
    /// so a re-detection of the same reply replays the same identity instead of duplicating it). Returns
    /// the minted record.
    /// </summary>
    public ActivityEventRecord Enqueue(ActivityEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            var minted = record with
            {
                EventId = record.EventId == Guid.Empty ? Guid.NewGuid() : record.EventId,
                DirectorSequence = ++_lastSequence,
            };
            _pending.Add(minted);
            AppendLine(minted);

            if (_pending.Count > _maxPending)
            {
                var overflow = _pending.Count - _maxPending;
                _pending.RemoveRange(0, overflow);
                Rewrite();
                FileLog.Write($"[ActivityEventOutbox] OVERFLOW: dropped the {overflow} oldest unacknowledged " +
                              $"events (cap {_maxPending}) - the Gateway has been unreachable too long");
            }
            return minted;
        }
    }

    /// <summary>The oldest pending events, up to <paramref name="max"/>, in mint order - one push batch.</summary>
    public IReadOnlyList<ActivityEventRecord> PendingBatch(int max)
    {
        lock (_gate)
            return _pending.Take(Math.Max(0, max)).ToList();
    }

    /// <summary>
    /// The Gateway confirmed it durably holds these events (written now, or already-held duplicates -
    /// both are acknowledgement): delete them from the delivery tail. Persists the survivors atomically.
    /// </summary>
    public void Acknowledge(IEnumerable<Guid> eventIds)
    {
        var acked = eventIds as HashSet<Guid> ?? new HashSet<Guid>(eventIds);
        if (acked.Count == 0) return;
        lock (_gate)
        {
            var before = _pending.Count;
            _pending.RemoveAll(e => acked.Contains(e.EventId));
            if (_pending.Count != before)
                Rewrite();
        }
    }

    // ---- persistence -------------------------------------------------------------------------------

    private void Load()
    {
        if (!File.Exists(_path)) return;
        var kept = 0;
        var dropped = 0;
        foreach (var line in File.ReadAllLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<ActivityEventRecord>(line, JsonOptions);
                if (record is null || record.EventId == Guid.Empty) { dropped++; continue; }
                _pending.Add(record);
                if (record.DirectorSequence > _lastSequence)
                    _lastSequence = record.DirectorSequence;
                kept++;
            }
            catch (JsonException)
            {
                dropped++; // one corrupt line must not cost the rest of the evidence
            }
        }
        if (kept > 0 || dropped > 0)
            FileLog.Write($"[ActivityEventOutbox] loaded {kept} pending event(s)" +
                          (dropped > 0 ? $", skipped {dropped} corrupt line(s)" : "") +
                          $", sequence resumes at {_lastSequence + 1}");
    }

    private void AppendLine(ActivityEventRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.AppendAllText(_path, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
    }

    /// <summary>Rewrite the whole file atomically (temp + move) - the acknowledgement/overflow path.</summary>
    private void Rewrite()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllLines(tmp, _pending.Select(e => JsonSerializer.Serialize(e, JsonOptions)));
        File.Move(tmp, _path, overwrite: true);
    }
}
