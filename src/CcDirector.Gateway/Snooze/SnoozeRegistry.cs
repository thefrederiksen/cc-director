using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Snooze;

/// <summary>
/// The Gateway-owned, restart-surviving snooze registry (Snooze Length mission,
/// docs/architecture/snooze-length-mission-2026-07-11.md). A snooze is a time-bounded hold with a
/// GUARANTEED return: the map <c>sessionId -&gt; SnoozeUntilUtc</c> is the one piece of new
/// Gateway-owned state, and it is the thing that keeps a snoozed session from vanishing when its
/// owning Director dies. The timer MUST live here (not on the Director) precisely so it survives a
/// dead Director - the whole point of the mission.
///
/// Each entry also carries the owning <see cref="SnoozeEntry.DirectorId"/> so the registry can be
/// bounded: when a Director is removed from the fleet (<c>Registry.OnDirectorRemoved</c>), every
/// entry it owned is dropped, so entries for sessions that permanently left the roster do not
/// accumulate on disk.
///
/// PERSISTENCE (WorkListStore precedent): the whole registry lives in ONE plain JSON file at the
/// path the constructor receives (production: snooze.json in the Gateway data dir). Every mutation
/// writes through immediately with an atomic temp-file + rename, so a crash mid-write can never
/// half-truncate the store. On construction the file is loaded back so every pending snooze is
/// re-armed - an entry already past its time at startup simply reads as expired on the first sweep
/// and fires immediately (the mission's "fire any already-past entry immediately" rule), rather than
/// being lost. A corrupt file is quarantined (never silently overwritten) and the registry starts
/// empty so the Gateway still boots.
///
/// NO FALLBACK (CLAUDE.md): a failed persist is a LOGGED error that PROPAGATES - a snooze that
/// cannot be written to disk would not survive a restart, so the caller fails loudly rather than
/// silently running a snooze that will not come back.
/// </summary>
public sealed class SnoozeRegistry
{
    private readonly object _gate = new();
    private readonly string _path;

    // sessionId -> entry. Ordinal keys: a session id is an exact GUID string.
    private readonly Dictionary<string, SnoozeEntry> _entries = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <param name="path">
    /// The JSON file the registry persists to. REQUIRED so no caller can silently land on the real
    /// user's file: production (<see cref="GatewayHost"/>) passes snooze.json in the Gateway data
    /// dir; tests pass an isolated temp path.
    /// </param>
    /// <exception cref="ArgumentException">The path is null/empty/whitespace.</exception>
    public SnoozeRegistry(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("registry path is required", nameof(path));
        _path = path;
        Load();
    }

    /// <summary>One pending snooze: the session, the absolute UTC time it returns, and the Director
    /// that owned it when the snooze was set (used to bound the registry on Director removal).</summary>
    public sealed record SnoozeEntry(string SessionId, DateTime SnoozeUntilUtc, string DirectorId);

    /// <summary>
    /// Record (or refresh) a snooze for <paramref name="sessionId"/> that returns at
    /// <paramref name="untilUtc"/>. Re-snoozing is just calling this again - it overwrites the prior
    /// entry with the fresh time (an alarm-clock, no escalation, no cap). Written through to disk
    /// before returning.
    /// </summary>
    /// <exception cref="ArgumentException">The session id is null/empty/whitespace.</exception>
    public void Snooze(string sessionId, DateTime untilUtc, string directorId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("session id is required", nameof(sessionId));

        lock (_gate)
        {
            _entries[sessionId] = new SnoozeEntry(sessionId, untilUtc.ToUniversalTime(), directorId ?? "");
            Save();
            FileLog.Write($"[SnoozeRegistry] Snooze: sid={sessionId}, untilUtc={untilUtc.ToUniversalTime():O}, director={directorId}");
        }
    }

    /// <summary>
    /// Remove the snooze entry for <paramref name="sessionId"/>. Returns true when an entry was
    /// removed (and persisted), false when there was none. This is the ONE clear path - a manual
    /// unsnooze, the #470 early return, and the post-expiry confirm all funnel through it.
    /// </summary>
    public bool Clear(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (_gate)
        {
            if (!_entries.Remove(sessionId)) return false;
            Save();
            FileLog.Write($"[SnoozeRegistry] Clear: sid={sessionId}");
            return true;
        }
    }

    /// <summary>
    /// Drop every entry owned by <paramref name="directorId"/>. Called from
    /// <c>Registry.OnDirectorRemoved</c> so entries for sessions whose Director permanently left the
    /// fleet do not accumulate on disk. Returns the number of entries removed; persists once if any.
    /// </summary>
    public int ClearForDirector(string directorId)
    {
        if (string.IsNullOrWhiteSpace(directorId)) return 0;
        lock (_gate)
        {
            var gone = _entries.Values
                .Where(e => string.Equals(e.DirectorId, directorId, StringComparison.Ordinal))
                .Select(e => e.SessionId)
                .ToList();
            foreach (var sid in gone)
                _entries.Remove(sid);
            if (gone.Count > 0)
            {
                Save();
                FileLog.Write($"[SnoozeRegistry] ClearForDirector: director={directorId}, removed={gone.Count}");
            }
            return gone.Count;
        }
    }

    /// <summary>
    /// Compare-and-clear: remove the entry for <paramref name="sessionId"/> ONLY if it still carries
    /// exactly <paramref name="expectedUntilUtc"/>. The sweep uses this so a stale decision (taken from a
    /// snapshot at the start of a pass) can never clobber a snooze the user re-armed in the meantime - a
    /// re-snooze moves the time, so the compare fails and the fresh snooze stands. This protects the one
    /// invariant that matters most: a live snooze is never silently lost. Returns true when it cleared.
    /// </summary>
    public bool ClearIfUnchanged(string sessionId, DateTime expectedUntilUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (_gate)
        {
            if (_entries.TryGetValue(sessionId, out var e) && e.SnoozeUntilUtc == expectedUntilUtc.ToUniversalTime())
            {
                _entries.Remove(sessionId);
                Save();
                FileLog.Write($"[SnoozeRegistry] ClearIfUnchanged: sid={sessionId} (unchanged since the sweep read it)");
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Drop every entry owned by <paramref name="directorId"/> whose session is NOT in
    /// <paramref name="liveSessionIds"/>. Called from the aggregation for a Director that actually
    /// answered (its returned list is authoritative), so a session that has permanently exited is
    /// pruned - without ever guessing from a transient miss, since it runs only for a reachable
    /// Director. Returns the number removed; persists once if any.
    /// </summary>
    public int PruneNotLive(string directorId, ISet<string> liveSessionIds)
    {
        if (string.IsNullOrWhiteSpace(directorId)) return 0;
        liveSessionIds ??= new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            var gone = _entries.Values
                .Where(e => string.Equals(e.DirectorId, directorId, StringComparison.Ordinal)
                            && !liveSessionIds.Contains(e.SessionId))
                .Select(e => e.SessionId)
                .ToList();
            foreach (var sid in gone)
                _entries.Remove(sid);
            if (gone.Count > 0)
            {
                Save();
                FileLog.Write($"[SnoozeRegistry] PruneNotLive: director={directorId}, removed={gone.Count}");
            }
            return gone.Count;
        }
    }

    /// <summary>
    /// True when <paramref name="sessionId"/> has an entry whose return time is at or before
    /// <paramref name="nowUtc"/> - i.e. the snooze has elapsed. This is the ONE expiry predicate the
    /// aggregation overlay uses to flip the session back into "needs you", and the sweep uses to
    /// decide whether to nudge the owning Director off hold. Pure (no mutation), so it is safe to
    /// call on the hot read path.
    /// </summary>
    public bool IsExpired(string sessionId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (_gate)
            return _entries.TryGetValue(sessionId, out var e) && nowUtc.ToUniversalTime() >= e.SnoozeUntilUtc;
    }

    /// <summary>True when <paramref name="sessionId"/> has a pending entry (expired or not).</summary>
    public bool Contains(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (_gate)
            return _entries.ContainsKey(sessionId);
    }

    /// <summary>A snapshot of every pending entry, for the expiry sweep. A copy, so the sweep can
    /// iterate and mutate the registry (Clear) without touching the live collection.</summary>
    public IReadOnlyList<SnoozeEntry> Entries()
    {
        lock (_gate)
            return _entries.Values.ToList();
    }

    // ---- persistence (WorkListStore precedent) -----------------------------------------------

    /// <summary>The on-disk shape: one document holding every pending snooze.</summary>
    private sealed class StoreFile
    {
        public List<SnoozeEntry> Entries { get; set; } = new();
    }

    /// <summary>
    /// Load the registry file written by a previous Gateway run - this is the re-arm on startup.
    /// Missing file = the normal first boot (empty registry, logged). A corrupt file is quarantined
    /// (renamed next to the original with a timestamp suffix) so its bytes are preserved for the
    /// operator and never silently overwritten; the registry then starts empty so the Gateway still
    /// boots. An entry already past its time is kept as-is: the first sweep reads it as expired and
    /// fires it immediately (mission rule), so nothing is dropped and nothing all-fires-at-once.
    /// </summary>
    private void Load()
    {
        if (!File.Exists(_path))
        {
            FileLog.Write($"[SnoozeRegistry] Load: no registry file at {_path}; starting empty");
            return;
        }

        StoreFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_path), FileJsonOptions);
        }
        catch (JsonException ex)
        {
            Quarantine(ex.Message);
            return;
        }

        if (parsed is null)
        {
            Quarantine("file deserialized to null (no registry document)");
            return;
        }

        var rearmed = 0;
        foreach (var e in parsed.Entries)
        {
            if (string.IsNullOrWhiteSpace(e.SessionId))
                continue; // skip a malformed row rather than fail the whole boot
            _entries[e.SessionId] = e with { SnoozeUntilUtc = e.SnoozeUntilUtc.ToUniversalTime() };
            rearmed++;
        }

        FileLog.Write($"[SnoozeRegistry] Load: re-armed {rearmed} pending snooze(s) from {_path}");
    }

    /// <summary>
    /// Preserve an unreadable registry file as "&lt;path&gt;.corrupt-&lt;stamp&gt;" and log loudly.
    /// The original path is then free for the next write-through. The move is not allowed to fail
    /// silently: if even the quarantine fails, the exception propagates and the Gateway does not
    /// start half-blind.
    /// </summary>
    private void Quarantine(string reason)
    {
        var quarantinePath = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(_path, quarantinePath);
        FileLog.Write($"[SnoozeRegistry] Load FAILED: registry file at {_path} is corrupt ({reason}); quarantined to {quarantinePath}; starting empty. Operator action: inspect the quarantined file to recover pending snoozes.");
    }

    /// <summary>
    /// Write-through: serialize the whole registry and atomically replace the file (temp + rename),
    /// so a concurrent reader or a crash mid-write never sees a half-written registry. Called inside
    /// the lock by every mutation. A failed save is a LOGGED error that PROPAGATES (the caller's
    /// request fails loudly) - never a silent skip, because a snooze that did not persist would not
    /// survive a restart.
    /// </summary>
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var file = new StoreFile { Entries = _entries.Values.ToList() };
            var json = JsonSerializer.Serialize(file, FileJsonOptions);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SnoozeRegistry] Save FAILED: path={_path}: {ex.Message}");
            throw;
        }
    }
}
