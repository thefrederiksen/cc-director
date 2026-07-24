using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// A long-lived, in-memory model of the repositories under the registered root directories, updated
/// by progressive background scans. It is the source of truth the Repository screen (and, later, the
/// per-session Worktrees tab and the Cockpit) render - they subscribe and display; they never scan.
///
/// A rescan streams results: each repository is published as soon as it is computed
/// (<see cref="Upserted"/>), progress is reported as it goes, and repositories no longer found are
/// removed at the end (<see cref="Removed"/>). Events fire on the scanning thread; UI subscribers
/// marshal to their dispatcher.
///
/// This is phase 1 of the background service (devthrottle_internal#507): the model + progressive
/// streaming. The warm-start cache, the file watcher, live session occupancy, and the Gateway push
/// are later phases.
/// </summary>
public sealed class RepositoryMonitor
{
    private readonly Func<IEnumerable<string>, IReadOnlyList<string>> _enumerate;
    private readonly Func<string, IReadOnlyList<LiveSessionRef>?, CancellationToken, Task<RepositoryStatus>> _compute;

    private readonly object _gate = new();
    private readonly Dictionary<string, RepositoryStatus> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private readonly string? _cachePath;

    /// <summary>True while a scan is in progress.</summary>
    public bool IsScanning { get; private set; }

    /// <summary>How many repositories have been computed in the current/last scan.</summary>
    public int ScanDone { get; private set; }

    /// <summary>How many repositories the current/last scan set out to compute.</summary>
    public int ScanTotal { get; private set; }

    /// <summary>Raised when a repository's status is added or updated in the model.</summary>
    public event Action<RepositoryStatus>? Upserted;

    /// <summary>Raised when a repository is removed from the model (no longer found on disk).</summary>
    public event Action<RepositoryStatus>? Removed;

    /// <summary>Raised when <see cref="IsScanning"/>, <see cref="ScanDone"/>, or <see cref="ScanTotal"/> changes.</summary>
    public event Action? ProgressChanged;

    /// <summary>Raised once when a scan finishes (not raised when a scan is superseded/cancelled).</summary>
    public event Action? ScanCompleted;

    public RepositoryMonitor(
        Func<IEnumerable<string>, IReadOnlyList<string>>? enumerate = null,
        Func<string, IReadOnlyList<LiveSessionRef>?, CancellationToken, Task<RepositoryStatus>>? compute = null,
        string? cachePath = null)
    {
        _enumerate = enumerate ?? DefaultEnumerate;
        _compute = compute ?? DefaultCompute;
        _cachePath = cachePath;
    }

    /// <summary>
    /// Warm start: load the last run's repositories from the JSON cache into the model, so a screen
    /// opened before the first scan finishes shows content immediately. The scan then re-verifies and
    /// reconciles. Best-effort and silent - a bad or missing cache just means an empty warm start.
    /// </summary>
    public void LoadCache()
    {
        if (string.IsNullOrEmpty(_cachePath) || !File.Exists(_cachePath))
            return;
        try
        {
            var cached = JsonSerializer.Deserialize<List<RepositoryStatus>>(File.ReadAllText(_cachePath));
            if (cached == null)
                return;
            lock (_gate)
            {
                foreach (var s in cached)
                    if (!string.IsNullOrWhiteSpace(s.Path))
                        _byPath[WorktreeReaperService.NormalizePath(s.Path)] = s;
            }
            FileLog.Write($"[RepositoryMonitor] warm-start: loaded {cached.Count} repositories from cache");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryMonitor] LoadCache failed: {ex.Message}");
        }
    }

    private void SaveCache()
    {
        if (string.IsNullOrEmpty(_cachePath))
            return;
        try
        {
            List<RepositoryStatus> snapshot;
            lock (_gate)
                snapshot = _byPath.Values.ToList();
            var dir = Path.GetDirectoryName(_cachePath);
            if (dir != null)
                Directory.CreateDirectory(dir);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryMonitor] SaveCache failed: {ex.Message}");
        }
    }

    /// <summary>A thread-safe copy of the current model.</summary>
    public IReadOnlyList<RepositoryStatus> Snapshot()
    {
        lock (_gate)
            return _byPath.Values.ToList();
    }

    /// <summary>
    /// Rescan the given roots, streaming each repository's status into the model as it is computed.
    /// A new rescan supersedes any in-flight one.
    /// </summary>
    public async Task RescanAsync(IEnumerable<string> roots, IReadOnlyList<LiveSessionRef>? sessions = null, CancellationToken externalCt = default)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            cts = _cts;
        }
        var ct = cts.Token;

        var paths = _enumerate(roots);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        lock (_gate) { IsScanning = true; ScanTotal = paths.Count; ScanDone = 0; }
        ProgressChanged?.Invoke();
        FileLog.Write($"[RepositoryMonitor] rescan started: {paths.Count} repositories");

        try
        {
            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                var status = await _compute(path, sessions, ct);
                var key = WorktreeReaperService.NormalizePath(status.Path);
                seen.Add(key);
                lock (_gate) { _byPath[key] = status; ScanDone++; }
                Upserted?.Invoke(status);
                ProgressChanged?.Invoke();
            }

            // Reconcile: drop repositories that were in the model but not found this scan.
            List<RepositoryStatus> removed;
            lock (_gate)
            {
                removed = _byPath.Where(kv => !seen.Contains(kv.Key)).Select(kv => kv.Value).ToList();
                foreach (var r in removed)
                    _byPath.Remove(WorktreeReaperService.NormalizePath(r.Path));
            }
            foreach (var r in removed)
                Removed?.Invoke(r);

            // Persist the verified model so the next launch warm-starts.
            SaveCache();
        }
        catch (OperationCanceledException)
        {
            FileLog.Write("[RepositoryMonitor] rescan superseded/cancelled");
            return; // a newer scan owns the model now
        }
        finally
        {
            lock (_gate) { IsScanning = false; }
            ProgressChanged?.Invoke();
        }

        FileLog.Write($"[RepositoryMonitor] rescan completed: {ScanDone}/{ScanTotal}");
        ScanCompleted?.Invoke();
    }

    private static IReadOnlyList<string> DefaultEnumerate(IEnumerable<string> roots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            foreach (var (_, path) in RemoteRepoProvider.ScanLocalRepos(root))
                if (seen.Add(WorktreeReaperService.NormalizePath(path)))
                    result.Add(path);
        }
        return result;
    }

    private static async Task<RepositoryStatus> DefaultCompute(string path, IReadOnlyList<LiveSessionRef>? sessions, CancellationToken ct)
        => await new RepositoryStatusService().GetStatusAsync(path, sessions, fetchPrune: false, ct);
}
