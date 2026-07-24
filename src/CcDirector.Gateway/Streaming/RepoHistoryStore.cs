using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Streaming;

/// <summary>One repository's daily snapshot row - the Gateway's memory of drift over time.</summary>
public sealed record RepoDailySnapshot
{
    public string Tenant { get; init; } = "";
    public DateOnly Date { get; init; }
    public string MachineName { get; init; } = "";

    /// <summary>The pushing Director's id - part of the identity key (ruling R2-9). Two Directors
    /// can report the same machine name and repository path (overlapping upgrades, duplicate
    /// registrations); only the Director id tells their rows apart.</summary>
    public string DirectorId { get; init; } = "";

    /// <summary>The repository's full path - part of the identity key. Two repositories can share a
    /// leaf name on one machine; only the path tells them apart.</summary>
    public string Path { get; init; } = "";

    /// <summary>The leaf name, kept for display only - never part of the key.</summary>
    public string Name { get; init; } = "";
    public int UncommittedCount { get; init; }
    public int DirtyDays { get; init; }
    public int BehindMainCount { get; init; }
    public int WorktreeCount { get; init; }
    public int WorktreesSafeToReap { get; init; }
    public long WorktreeBytes { get; init; }
}

/// <summary>The weekly report's numbers for one week.</summary>
public sealed record RepoWeekTrend
{
    public DateOnly WeekStart { get; init; }
    public int MaxWorktrees { get; init; }
    public int MaxSafeToReap { get; init; }
    public long MaxWorktreeBytes { get; init; }
    public int ReposDirtyOverThreshold { get; init; }
}

/// <summary>
/// The repositories memory (devthrottle_internal#510 phase D): daily snapshots of every repository
/// the fleet pushes, persisted as JSON lines on the Gateway's disk, aggregated into weekly trends
/// for the dev-effectiveness report.
///
/// Deliberately FILE-backed in v1, not Postgres: EF migrations are serialized fleet-wide and a
/// long-lived unmerged migration on a mission branch is a conflict magnet. The store is a seam -
/// moving it to real tables later changes persistence, not shape. One row per (tenant, day,
/// machine, repo); re-observing the same day upserts (last write wins), so the daily write is
/// idempotent however often Directors push.
/// </summary>
public sealed class RepoHistoryStore
{
    public const int DirtyThresholdDays = 7;

    /// <summary>How long a daily row is kept. The only reader caps its window at 26 weeks, so rows
    /// older than that (plus a week of margin) are pruned - otherwise the file and the in-memory
    /// dictionary grow forever and every push rewrites all of history (issue 516).</summary>
    public const int RetentionDays = 26 * 7 + 7;

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, RepoDailySnapshot>? _rows; // key: tenant|date|machine|repo

    public RepoHistoryStore(string path)
    {
        _path = path;
    }

    /// <summary>
    /// Fold a pushed snapshot into today's rows. Idempotent per day; cheap on every push.
    ///
    /// The snapshot is a Director's FULL current view, so it also RECONCILES: rows for a Director
    /// whose repository disappeared from the snapshot (removed, moved, or renamed) are dropped for
    /// today, rather than left in the dirty callouts and double-counted (issue 516). An empty
    /// snapshot cannot name its Director from the rows, so <paramref name="reconcileDirectorId"/>
    /// (the connection's bound Director) lets an all-removed day still be cleared.
    /// </summary>
    public void ObserveSnapshot(TenantId tenant, IReadOnlyList<RepoStatusDto> repositories, DateOnly? today = null, string? reconcileDirectorId = null)
    {
        var date = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        lock (_gate)
        {
            Load();

            var presentKeys = new HashSet<string>(StringComparer.Ordinal);
            var directorsInScope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(reconcileDirectorId))
                directorsInScope.Add(reconcileDirectorId);

            // Change detection (issue 516): every accepted push re-pushes the FULL snapshot, and on a
            // hosted Gateway this happens on a periodic cadence for every tenant. Rewriting the whole
            // global file when nothing actually changed is pure noisy-neighbour cost, so the disk
            // write is skipped unless this observation added, changed, removed, or pruned a row.
            bool changed = false;

            foreach (var r in repositories)
            {
                if (r.Provisional)
                    continue; // unverified warm-start data never becomes history
                if (string.IsNullOrWhiteSpace(r.Path))
                {
                    FileLog.Write($"[RepoHistoryStore] snapshot row without a path ignored (name={r.Name})");
                    continue; // the path IS the identity - a pathless row cannot be keyed
                }
                if (string.IsNullOrWhiteSpace(r.DirectorId))
                {
                    FileLog.Write($"[RepoHistoryStore] snapshot row without a Director id ignored (name={r.Name})");
                    continue; // the Director id is part of the identity (ruling R2-9)
                }
                int dirtyDays = r.DirtySinceUtc is { } since && !r.IsClean
                    ? (int)Math.Max(0, (DateTime.UtcNow - since).TotalDays)
                    : 0;
                var row = new RepoDailySnapshot
                {
                    Tenant = tenant.Value,
                    Date = date,
                    MachineName = r.MachineName,
                    DirectorId = r.DirectorId,
                    Path = r.Path,
                    Name = r.Name,
                    UncommittedCount = r.UncommittedCount,
                    DirtyDays = dirtyDays,
                    BehindMainCount = r.BehindMainCount,
                    WorktreeCount = r.WorktreeCount,
                    WorktreesSafeToReap = r.WorktreesSafeToReap,
                    WorktreeBytes = r.WorktreeBytes,
                };
                var key = Key(row);
                if (!_rows!.TryGetValue(key, out var existing) || !existing.Equals(row))
                    changed = true;
                _rows![key] = row;
                presentKeys.Add(key);
                directorsInScope.Add(r.DirectorId);
            }

            // Reconcile: drop THIS day's rows for the Directors we just heard from whose repository
            // is no longer in the snapshot. Scoped to (tenant, today, Director) and to the Directors
            // present here, so another Director's rows and other days are untouched.
            if (directorsInScope.Count > 0)
            {
                var stale = _rows!.Values
                    .Where(row => row.Tenant == tenant.Value
                                  && row.Date == date
                                  && directorsInScope.Contains(row.DirectorId)
                                  && !presentKeys.Contains(Key(row)))
                    .ToList();
                foreach (var row in stale)
                    _rows!.Remove(Key(row));
                if (stale.Count > 0)
                    changed = true;
            }

            // Retention (issue 516): age out rows past the read window across ALL tenants, so the
            // file and dictionary do not grow without bound and write cost does not grow with all
            // past tenants/Directors/repositories.
            var cutoff = date.AddDays(-RetentionDays);
            var expired = _rows!.Values.Where(row => row.Date < cutoff).ToList();
            foreach (var row in expired)
                _rows!.Remove(Key(row));
            if (expired.Count > 0)
            {
                changed = true;
                FileLog.Write($"[RepoHistoryStore] pruned {expired.Count} row(s) older than {RetentionDays} days");
            }

            if (changed)
                Save();
        }
    }

    /// <summary>Weekly trends for the last <paramref name="weeks"/> weeks (oldest first).</summary>
    public IReadOnlyList<RepoWeekTrend> WeeklyTrends(TenantId tenant, int weeks = 8, DateOnly? today = null)
    {
        var end = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        List<RepoDailySnapshot> rows;
        lock (_gate)
        {
            Load();
            rows = _rows!.Values.Where(r => r.Tenant == tenant.Value).ToList();
        }

        var trends = new List<RepoWeekTrend>();
        for (int w = weeks - 1; w >= 0; w--)
        {
            var weekStart = StartOfWeek(end).AddDays(-7 * w);
            var weekEnd = weekStart.AddDays(7);
            var inWeek = rows.Where(r => r.Date >= weekStart && r.Date < weekEnd).ToList();
            if (inWeek.Count == 0)
            {
                trends.Add(new RepoWeekTrend { WeekStart = weekStart });
                continue;
            }
            // Per-day fleet totals, then the week's peak - so one busy day is not averaged away.
            var perDay = inWeek.GroupBy(r => r.Date).Select(g => new
            {
                Worktrees = g.Sum(r => r.WorktreeCount),
                Safe = g.Sum(r => r.WorktreesSafeToReap),
                Bytes = g.Sum(r => r.WorktreeBytes),
                Dirty = g.Count(r => r.DirtyDays >= DirtyThresholdDays),
            }).ToList();
            trends.Add(new RepoWeekTrend
            {
                WeekStart = weekStart,
                MaxWorktrees = perDay.Max(d => d.Worktrees),
                MaxSafeToReap = perDay.Max(d => d.Safe),
                MaxWorktreeBytes = perDay.Max(d => d.Bytes),
                ReposDirtyOverThreshold = perDay.Max(d => d.Dirty),
            });
        }
        return trends;
    }

    /// <summary>Today's repositories dirty past the threshold - the report's callout list.</summary>
    public IReadOnlyList<RepoDailySnapshot> DirtyOverThreshold(TenantId tenant, DateOnly? today = null)
    {
        var date = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        lock (_gate)
        {
            Load();
            return _rows!.Values
                .Where(r => r.Tenant == tenant.Value && r.Date == date && r.DirtyDays >= DirtyThresholdDays)
                .OrderByDescending(r => r.DirtyDays)
                .ToList();
        }
    }

    private static DateOnly StartOfWeek(DateOnly d)
        => d.AddDays(-(((int)d.DayOfWeek + 6) % 7)); // Monday

    /// <summary>
    /// The row identity: tenant, day, machine, DIRECTOR, and the repository PATH (trailing
    /// separators trimmed, then lowercased). The leaf name is display only - two repositories
    /// sharing a folder name on one machine must never overwrite each other's snapshots
    /// (inspection finding F13), and two Directors reporting the same machine and path must
    /// never overwrite each other's rows (ruling R2-9). The lowercasing stays deliberately: a
    /// case-only path collision on a case-sensitive filesystem merges two history rows - an
    /// accepted, documented inaccuracy that is never destructive (history is aggregate
    /// reporting, not an acting surface).
    /// </summary>
    private static string Key(RepoDailySnapshot r)
        => $"{r.Tenant}|{r.Date:yyyy-MM-dd}|{r.MachineName}|{r.DirectorId}|{r.Path.TrimEnd('\\', '/')}".ToLowerInvariant();

    private void Load()
    {
        if (_rows != null)
            return;
        _rows = new Dictionary<string, RepoDailySnapshot>(StringComparer.Ordinal);

        // Read the live file; if it is missing or unreadable AS A WHOLE, fall back to the
        // last-known-good backup so a torn or truncated write never erases the accumulated history
        // (issue 516). A single corrupt LINE inside a readable file is skipped, not fatal.
        if (!TryLoadFrom(_path))
            TryLoadFrom(_path + ".bak");
    }

    private bool TryLoadFrom(string path)
    {
        if (!File.Exists(path))
            return false;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepoHistoryStore] could not read {path} (will try the backup): {ex.Message}");
            return false;
        }

        int skipped = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            RepoDailySnapshot? row;
            try
            {
                row = JsonSerializer.Deserialize<RepoDailySnapshot>(line);
            }
            catch (Exception)
            {
                // A single corrupt line (a torn write) must NOT drop every good line after it -
                // skip this one and keep the rest (issue 516). Previously the first bad line ended
                // the whole load, and the next save rewrote the file from the truncated result.
                skipped++;
                continue;
            }
            // Rows without a path or without a Director id predate the current key format
            // (days old, unreleased) and cannot be keyed - ignored rather than migrated.
            if (row != null && !string.IsNullOrWhiteSpace(row.Path) && !string.IsNullOrWhiteSpace(row.DirectorId))
                _rows![Key(row)] = row;
        }
        if (skipped > 0)
            FileLog.Write($"[RepoHistoryStore] skipped {skipped} unparseable line(s) in {path}");
        return true;
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir != null)
                Directory.CreateDirectory(dir);

            // Write the whole file to a temp, flush it to disk, then swap it in atomically - so a
            // crash, a full disk, or an interrupted write can never truncate the live history
            // (issue 516). File.Replace keeps the previous file as a .bak for the load-time fallback.
            var tmp = _path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs))
            {
                foreach (var r in _rows!.Values)
                    writer.WriteLine(JsonSerializer.Serialize(r));
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
                File.Replace(tmp, _path, _path + ".bak");
            else
                File.Move(tmp, _path);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepoHistoryStore] save failed: {ex.Message}");
        }
    }
}
