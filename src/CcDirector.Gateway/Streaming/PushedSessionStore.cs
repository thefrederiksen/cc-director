using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Streaming;

/// <summary>
/// Issue #1176 (Phase 1a): the Gateway's in-memory cache of the session state each Director pushes UP
/// over its persistent stream, replacing the on-demand pull for stream-connected Directors. One entry
/// per Director (keyed by directorId), holding the sessions that Director last pushed.
///
/// Correctness rules this store enforces (Phase 1 plan, section 4.4):
///  - CONNECTION GENERATION: each stream connection is identified by its SignalR connection id. A new
///    connection becomes the active connection for its Director and its first message is authoritative.
///    This is what lets a RESTARTED Director - which dials a brand-new connection - reseed even though a
///    prior connection had pushed higher sequence numbers (a plain persistent integer epoch would wrongly
///    reject the restarted Director's snapshot).
///  - SEQUENCE ORDERING: within one connection, messages carry a monotonic sequence; a message with a
///    sequence at or below the last applied one is dropped as stale/duplicate.
///  - ACTIVE-CONNECTION OWNERSHIP: snapshot/delta/remove are accepted only from the currently active
///    connection. A late disconnect from a superseded connection (a reconnect overlap) does NOT clear the
///    active connection or the cache.
///  - SNAPSHOT IS AUTHORITATIVE: a full snapshot replaces the Director's whole session set, pruning any
///    session not present in it.
///
/// Reads (<see cref="TryGetFresh"/>) return deep COPIES so the aggregation may stamp them without
/// contaminating the cache, and recompute relative time fields (idle seconds) from the absolute
/// LastActivityAt so a quiet session's idle clock keeps advancing between pushes.
///
/// Thread-safe: a <see cref="ConcurrentDictionary{TKey,TValue}"/> of per-Director entries, each guarded
/// by its own lock.
/// </summary>
public sealed class PushedSessionStore
{
    private readonly ConcurrentDictionary<string, DirectorEntry> _byDirector = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _utcNow;

    /// <summary>
    /// Create the store. <paramref name="utcNow"/> is a test seam for the staleness and idle-clock logic;
    /// production passes null and the store reads <see cref="DateTime.UtcNow"/>.
    /// </summary>
    public PushedSessionStore(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Mark <paramref name="connectionId"/> as the active stream connection for <paramref name="directorId"/>.
    /// Existing cached sessions are kept (so a fast reconnect keeps roster continuity), but the sequence
    /// baseline resets so the new connection's first message - at any sequence - is authoritative. The
    /// last-received timestamp is deliberately NOT refreshed here: until the new connection actually pushes
    /// a snapshot, the cache is treated as stale so aggregation falls back to pull.
    /// </summary>
    public void RegisterConnection(string directorId, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(directorId))
            throw new ArgumentException("directorId is required", nameof(directorId));
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("connectionId is required", nameof(connectionId));

        var entry = _byDirector.GetOrAdd(directorId, _ => new DirectorEntry());
        lock (entry.Gate)
        {
            entry.ActiveConnectionId = connectionId;
            entry.LastSequence = -1;
        }
        FileLog.Write($"[PushedSessionStore] RegisterConnection: director={directorId}, conn={Short(connectionId)} is now the active connection");
    }

    /// <summary>
    /// Clear the active connection for <paramref name="directorId"/> IF <paramref name="connectionId"/> is
    /// still the active one. A late disconnect from a superseded connection is logged and ignored so it
    /// cannot wipe a newer active connection. Cached sessions are retained; while no connection is active,
    /// <see cref="TryGetFresh"/> returns null so aggregation pulls instead.
    /// </summary>
    public void UnregisterConnection(string directorId, string connectionId)
    {
        if (!_byDirector.TryGetValue(directorId, out var entry))
            return;

        lock (entry.Gate)
        {
            if (!string.Equals(entry.ActiveConnectionId, connectionId, StringComparison.Ordinal))
            {
                FileLog.Write($"[PushedSessionStore] UnregisterConnection IGNORED (not active): director={directorId}, conn={Short(connectionId)}, active={Short(entry.ActiveConnectionId)}");
                return;
            }
            entry.ActiveConnectionId = null;
        }
        FileLog.Write($"[PushedSessionStore] UnregisterConnection: director={directorId}, conn={Short(connectionId)} cleared; aggregation will fall back to pull");
    }

    /// <summary>
    /// Apply a full snapshot: replace the Director's whole session set, pruning any session not present.
    /// </summary>
    /// <returns>true if applied; false if rejected (not the active connection, or a stale sequence).</returns>
    public bool ApplySnapshot(string directorId, string connectionId, long sequence, IReadOnlyList<SessionDto> sessions)
    {
        if (sessions is null)
            throw new ArgumentNullException(nameof(sessions));
        if (!_byDirector.TryGetValue(directorId, out var entry))
            return false;

        lock (entry.Gate)
        {
            if (!IsAcceptable(entry, directorId, connectionId, sequence, "snapshot"))
                return false;

            entry.Sessions.Clear();
            foreach (var s in sessions)
            {
                if (!string.IsNullOrEmpty(s.SessionId))
                    entry.Sessions[s.SessionId] = s;
            }
            entry.LastSequence = sequence;
            entry.ReceivedAtUtc = _utcNow();
        }
        return true;
    }

    /// <summary>Apply a single-session delta: upsert one session.</summary>
    /// <returns>true if applied; false if rejected.</returns>
    public bool ApplyDelta(string directorId, string connectionId, long sequence, SessionDto session)
    {
        if (session is null)
            throw new ArgumentNullException(nameof(session));
        if (string.IsNullOrEmpty(session.SessionId))
            throw new ArgumentException("session.SessionId is required", nameof(session));
        if (!_byDirector.TryGetValue(directorId, out var entry))
            return false;

        lock (entry.Gate)
        {
            if (!IsAcceptable(entry, directorId, connectionId, sequence, "delta"))
                return false;

            entry.Sessions[session.SessionId] = session;
            entry.LastSequence = sequence;
            entry.ReceivedAtUtc = _utcNow();
        }
        return true;
    }

    /// <summary>Apply a remove/tombstone: drop one session from the Director's set.</summary>
    /// <returns>true if applied; false if rejected.</returns>
    public bool ApplyRemove(string directorId, string connectionId, long sequence, string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            throw new ArgumentException("sessionId is required", nameof(sessionId));
        if (!_byDirector.TryGetValue(directorId, out var entry))
            return false;

        lock (entry.Gate)
        {
            if (!IsAcceptable(entry, directorId, connectionId, sequence, "remove"))
                return false;

            entry.Sessions.Remove(sessionId);
            entry.LastSequence = sequence;
            entry.ReceivedAtUtc = _utcNow();
        }
        return true;
    }

    /// <summary>
    /// Return deep COPIES of the Director's cached sessions when the stream is connected and the last push
    /// is within <paramref name="staleAfter"/>; otherwise null so the caller pulls. Copies are returned so
    /// the /sessions aggregation may stamp them without contaminating the cache. Relative time fields (idle
    /// seconds) are recomputed from the absolute LastActivityAt at serve time so a quiet session's idle
    /// clock keeps advancing between pushes.
    /// </summary>
    public IReadOnlyList<SessionDto>? TryGetFresh(string directorId, TimeSpan staleAfter)
    {
        if (!_byDirector.TryGetValue(directorId, out var entry))
            return null;

        lock (entry.Gate)
        {
            if (entry.ActiveConnectionId is null)
                return null;
            if (entry.ReceivedAtUtc == DateTime.MinValue)
                return null;

            var now = _utcNow();
            if (now - entry.ReceivedAtUtc > staleAfter)
                return null;

            var copies = new List<SessionDto>(entry.Sessions.Count);
            foreach (var s in entry.Sessions.Values)
                copies.Add(RecomputeClocks(s.Clone(), now));
            return copies;
        }
    }

    /// <summary>True when this Director currently has an active stream connection (used for diagnostics).</summary>
    public bool IsStreamConnected(string directorId) =>
        _byDirector.TryGetValue(directorId, out var entry) && entry.ActiveConnectionId is not null;

    /// <summary>
    /// The active stream connection id for a Director, or null when none. The Gateway uses it to address a
    /// message DOWN the stream to that Director (issue #1176, Phase 1b down-channel).
    /// </summary>
    public string? GetActiveConnectionId(string directorId)
    {
        if (!_byDirector.TryGetValue(directorId, out var entry))
            return null;
        lock (entry.Gate)
            return entry.ActiveConnectionId;
    }

    private static bool IsAcceptable(DirectorEntry entry, string directorId, string connectionId, long sequence, string kind)
    {
        if (!string.Equals(entry.ActiveConnectionId, connectionId, StringComparison.Ordinal))
        {
            FileLog.Write($"[PushedSessionStore] {kind} DROPPED (not the active connection): director={directorId}, conn={Short(connectionId)}, active={Short(entry.ActiveConnectionId)}");
            return false;
        }
        if (sequence <= entry.LastSequence)
        {
            FileLog.Write($"[PushedSessionStore] {kind} DROPPED (stale sequence {sequence} <= last {entry.LastSequence}): director={directorId}, conn={Short(connectionId)}");
            return false;
        }
        return true;
    }

    private static SessionDto RecomputeClocks(SessionDto s, DateTime nowUtc)
    {
        if (s.LastActivityAt is DateTime last)
        {
            var idle = (nowUtc - last).TotalSeconds;
            s.IdleSeconds = idle > 0 ? idle : 0;
        }
        return s;
    }

    private static string Short(string? id) =>
        string.IsNullOrEmpty(id) ? "(none)" : (id.Length <= 8 ? id : id[..8]);

    private sealed class DirectorEntry
    {
        public readonly object Gate = new();
        public string? ActiveConnectionId;
        public long LastSequence = -1;
        public DateTime ReceivedAtUtc = DateTime.MinValue;
        public readonly Dictionary<string, SessionDto> Sessions = new(StringComparer.Ordinal);
    }
}
