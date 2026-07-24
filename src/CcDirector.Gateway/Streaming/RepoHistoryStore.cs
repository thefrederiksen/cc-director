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

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, RepoDailySnapshot>? _rows; // key: tenant|date|machine|repo

    public RepoHistoryStore(string path)
    {
        _path = path;
    }

    /// <summary>Fold a pushed snapshot into today's rows. Idempotent per day; cheap on every push.</summary>
    public void ObserveSnapshot(TenantId tenant, IReadOnlyList<RepoStatusDto> repositories, DateOnly? today = null)
    {
        var date = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        lock (_gate)
        {
            Load();
            foreach (var r in repositories)
            {
                if (r.Provisional)
                    continue; // unverified warm-start data never becomes history
                if (string.IsNullOrWhiteSpace(r.Path))
                {
                    FileLog.Write($"[RepoHistoryStore] snapshot row without a path ignored (name={r.Name})");
                    continue; // the path IS the identity - a pathless row cannot be keyed
                }
                int dirtyDays = r.DirtySinceUtc is { } since && !r.IsClean
                    ? (int)Math.Max(0, (DateTime.UtcNow - since).TotalDays)
                    : 0;
                var row = new RepoDailySnapshot
                {
                    Tenant = tenant.Value,
                    Date = date,
                    MachineName = r.MachineName,
                    Path = r.Path,
                    Name = r.Name,
                    UncommittedCount = r.UncommittedCount,
                    DirtyDays = dirtyDays,
                    BehindMainCount = r.BehindMainCount,
                    WorktreeCount = r.WorktreeCount,
                    WorktreesSafeToReap = r.WorktreesSafeToReap,
                    WorktreeBytes = r.WorktreeBytes,
                };
                _rows![Key(row)] = row;
            }
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
    /// The row identity: tenant, day, machine, and the repository PATH (lowercased). The leaf name
    /// is display only - two repositories sharing a folder name on one machine must never
    /// overwrite each other's snapshots (inspection finding F13).
    /// </summary>
    private static string Key(RepoDailySnapshot r) => $"{r.Tenant}|{r.Date:yyyy-MM-dd}|{r.MachineName}|{r.Path}".ToLowerInvariant();

    private void Load()
    {
        if (_rows != null)
            return;
        _rows = new Dictionary<string, RepoDailySnapshot>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(_path))
                return;
            foreach (var line in File.ReadAllLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var row = JsonSerializer.Deserialize<RepoDailySnapshot>(line);
                // Rows without a path predate the path-keyed format (days old, unreleased) and
                // cannot be keyed - ignored rather than migrated.
                if (row != null && !string.IsNullOrWhiteSpace(row.Path))
                    _rows[Key(row)] = row;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepoHistoryStore] load failed (starting empty): {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (dir != null)
                Directory.CreateDirectory(dir);
            File.WriteAllLines(_path, _rows!.Values.Select(r => JsonSerializer.Serialize(r)));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepoHistoryStore] save failed: {ex.Message}");
        }
    }
}
