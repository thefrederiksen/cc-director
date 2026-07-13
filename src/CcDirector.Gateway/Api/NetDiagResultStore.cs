using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Durable store of recent network speed-test results (Network Diagnostics mission, Phase 1). Replaces
/// the in-memory NetDiagResultLog ring so results submitted from the app/Cockpit AND the server-side
/// monitor survive a Gateway restart and can feed the per-device baseline. Newest-first, bounded, and
/// persisted to <c>diagnostics-results.json</c> with the same atomic write-through + corrupt-file
/// quarantine contract as <see cref="CronRunHistoryStore"/>, so a crash mid-write never half-truncates
/// the file and an unreadable file is preserved rather than silently overwritten.
/// </summary>
public sealed class NetDiagResultStore
{
    /// <summary>Max results retained; older results are pruned (keeps the file bounded). Architect Decision 2a: 50 -> 200.</summary>
    public const int MaxRecords = 200;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<NetDiagResultDto> _items = new(); // newest first

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <param name="path">The JSON file the store persists to. REQUIRED (no silent default).</param>
    /// <exception cref="ArgumentException">The path is null/empty/whitespace.</exception>
    public NetDiagResultStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("store path is required", nameof(path));
        _path = path;
        Load();
    }

    /// <summary>Record one result (newest first), pruning to <see cref="MaxRecords"/>, and persist.</summary>
    /// <exception cref="ArgumentNullException">The result is null.</exception>
    public void Add(NetDiagResultDto result)
    {
        if (result is null)
            throw new ArgumentNullException(nameof(result));

        lock (_gate)
        {
            _items.Insert(0, result);
            if (_items.Count > MaxRecords)
                _items.RemoveRange(MaxRecords, _items.Count - MaxRecords);
            Save();
            FileLog.Write($"[NetDiagResultStore] Add: surface={result.Surface}, route={result.Route}, clientPath={result.ClientPath}, latencyMedian={result.LatencyMedianMs}ms, count={_items.Count}");
        }
    }

    /// <summary>The most recent results, newest first (at most <paramref name="count"/>).</summary>
    public IReadOnlyList<NetDiagResultDto> Recent(int count = MaxRecords)
    {
        lock (_gate)
            return _items.Take(Math.Max(0, count)).ToList();
    }

    // ---- persistence (CronRunHistoryStore precedent: atomic write-through + corrupt-file quarantine) ----

    private sealed class StoreFile
    {
        public List<NetDiagResultDto> Results { get; set; } = new();
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            FileLog.Write($"[NetDiagResultStore] Load: no store file at {_path}; starting empty");
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
            Quarantine("file deserialized to null (no store document)");
            return;
        }

        _items.AddRange(parsed.Results.Where(r => r is not null));
        if (_items.Count > MaxRecords)
            _items.RemoveRange(MaxRecords, _items.Count - MaxRecords);
        FileLog.Write($"[NetDiagResultStore] Load: restored {_items.Count} result(s) from {_path}");
    }

    private void Quarantine(string reason)
    {
        var quarantinePath = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(_path, quarantinePath);
        FileLog.Write($"[NetDiagResultStore] Load FAILED: store file at {_path} is corrupt ({reason}); quarantined to {quarantinePath}; starting empty.");
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var file = new StoreFile { Results = _items };
            var json = JsonSerializer.Serialize(file, FileJsonOptions);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[NetDiagResultStore] Save FAILED: path={_path}: {ex.Message}");
            throw;
        }
    }
}
