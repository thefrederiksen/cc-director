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
/// Consistency rules (issue devthrottle_internal#510, inspection round 1):
/// - The monitor owns the live-session source (<see cref="LiveSessionsProvider"/>) and consults it
///   on EVERY compute, so no compute path can erase the in-use-by-session classification.
/// - Computes are single-flight per repository, and a single-repository recompute requested while a
///   full scan runs is deferred until the scan completes - so an older result can never overwrite a
///   newer one.
/// - A linked-worktree path is canonicalized to its PRIMARY checkout before computing, so a
///   worktree path never becomes its own model entry.
/// </summary>
public sealed class RepositoryMonitor
{
    private readonly Func<IEnumerable<string>, IReadOnlyList<string>> _enumerate;
    private readonly Func<string, IReadOnlyList<LiveSessionRef>?, CancellationToken, Task<RepositoryStatus>> _compute;
    private readonly Func<string, CancellationToken, Task<string?>> _resolvePrimary;

    private readonly object _gate = new();
    private readonly Dictionary<string, RepositoryStatus> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SemaphoreSlim> _repoLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _deferredRecomputes = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// THE live-session source for every compute this monitor runs - full scans and single-repository
    /// recomputes alike. Wired once at startup by the host (the same source the panels use). When it
    /// is null the computes run without session data (no host wired one up yet).
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>>? LiveSessionsProvider { get; set; }

    public RepositoryMonitor(
        Func<IEnumerable<string>, IReadOnlyList<string>>? enumerate = null,
        Func<string, IReadOnlyList<LiveSessionRef>?, CancellationToken, Task<RepositoryStatus>>? compute = null,
        string? cachePath = null,
        Func<string, CancellationToken, Task<string?>>? resolvePrimary = null)
    {
        _enumerate = enumerate ?? DefaultEnumerate;
        _compute = compute ?? DefaultCompute;
        _cachePath = cachePath;
        _resolvePrimary = resolvePrimary ?? DefaultResolvePrimary;
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
                        // Cached entries are PROVISIONAL: shown dimmed as "verifying", never acted
                        // on, until the live scan re-confirms them (the warm-start trust rule).
                        _byPath[WorktreeReaperService.NormalizePath(s.Path)] = s with { Provisional = true };
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
    /// Finds the repository entry a path belongs to: the repository itself, or the repository one of
    /// whose worktrees IS that path. This is how a session sitting inside a worktree finds its repo's
    /// entry (the one-brain rule: the per-session tab renders the same model as the Repositories home).
    /// </summary>
    public RepositoryStatus? FindForPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var key = WorktreeReaperService.NormalizePath(path);
        lock (_gate)
        {
            if (_byPath.TryGetValue(key, out var direct))
                return direct;
            foreach (var s in _byPath.Values)
                foreach (var w in s.Worktrees)
                    if (string.Equals(WorktreeReaperService.NormalizePath(w.Path), key, StringComparison.OrdinalIgnoreCase))
                        return s;
        }
        return null;
    }

    /// <summary>
    /// Rescan the given roots, streaming each repository's status into the model as it is computed.
    /// A new rescan supersedes any in-flight one. Live sessions come from
    /// <see cref="LiveSessionsProvider"/> on every compute.
    /// </summary>
    public async Task RescanAsync(IEnumerable<string> roots, CancellationToken externalCt = default)
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
                var repoLock = GetRepoLock(WorktreeReaperService.NormalizePath(path));
                await repoLock.WaitAsync(ct);
                RepositoryStatus enriched;
                try
                {
                    var sessions = await FetchLiveSessionsAsync(ct);
                    var status = await _compute(path, sessions, ct);
                    var key = WorktreeReaperService.NormalizePath(status.Path);
                    seen.Add(key);
                    lock (_gate)
                    {
                        enriched = Enrich(status, _byPath.TryGetValue(key, out var prev) ? prev : null);
                        _byPath[key] = enriched;
                        ScanDone++;
                    }
                }
                finally
                {
                    repoLock.Release();
                }
                Upserted?.Invoke(enriched);
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

        // Run the single-repository recomputes that arrived while this scan held the model.
        await DrainDeferredRecomputesAsync(externalCt);

        ScanCompleted?.Invoke();
    }

    /// <summary>
    /// Recompute ONE repository and publish the result - the file watcher's path: a change under
    /// one repo never re-scans the others. Removes the entry when the directory is gone. A linked
    /// worktree path is canonicalized to its primary checkout first, so the PRIMARY entry is
    /// recomputed and a worktree path never becomes a model entry of its own. While a full scan is
    /// running the recompute is deferred and runs when the scan completes.
    /// </summary>
    public async Task RecomputeOneAsync(string repoPath, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (IsScanning)
            {
                _deferredRecomputes[WorktreeReaperService.NormalizePath(repoPath)] = repoPath;
                FileLog.Write($"[RepositoryMonitor] recompute deferred until the running scan completes: {repoPath}");
                return;
            }
        }

        // "Gone" means the folder disappeared OR it is no longer a git repository (a root watcher
        // fires for ANY subdirectory; a non-repo folder must never become a model entry).
        bool gitIsFile = File.Exists(Path.Combine(repoPath, ".git"));
        bool isRepo = Directory.Exists(repoPath)
                      && (Directory.Exists(Path.Combine(repoPath, ".git")) || gitIsFile);
        if (!isRepo)
        {
            var key = WorktreeReaperService.NormalizePath(repoPath);
            RepositoryStatus? gone = null;
            lock (_gate)
            {
                if (_byPath.TryGetValue(key, out gone))
                    _byPath.Remove(key);
            }
            if (gone != null)
            {
                FileLog.Write($"[RepositoryMonitor] recompute: {repoPath} is gone - removed");
                Removed?.Invoke(gone);
                SaveCache();
            }
            return;
        }

        // A .git FILE marks a linked worktree - canonicalize to the primary checkout and recompute
        // THAT entry. Failing to resolve is a real error, not a reason to guess.
        var targetPath = repoPath;
        if (gitIsFile)
        {
            var primary = await _resolvePrimary(repoPath, ct);
            if (string.IsNullOrWhiteSpace(primary))
                throw new InvalidOperationException(
                    $"could not resolve the primary repository for the linked worktree path: {repoPath}");
            FileLog.Write($"[RepositoryMonitor] recompute: {repoPath} is a linked worktree of {primary}");
            targetPath = primary;
        }

        var targetKey = WorktreeReaperService.NormalizePath(targetPath);
        var repoLock = GetRepoLock(targetKey);
        await repoLock.WaitAsync(ct);
        RepositoryStatus enriched;
        try
        {
            var sessions = await FetchLiveSessionsAsync(ct);
            var status = await _compute(targetPath, sessions, ct);
            lock (_gate)
            {
                enriched = Enrich(status, _byPath.TryGetValue(targetKey, out var prev) ? prev : null);
                _byPath[targetKey] = enriched;
            }
        }
        finally
        {
            repoLock.Release();
        }
        FileLog.Write($"[RepositoryMonitor] recomputed one: {enriched.Name}");
        Upserted?.Invoke(enriched);
        SaveCache();
    }

    /// <summary>One compute at a time per repository - publishes can never land out of order.</summary>
    private SemaphoreSlim GetRepoLock(string key)
    {
        lock (_gate)
        {
            if (!_repoLocks.TryGetValue(key, out var sem))
                _repoLocks[key] = sem = new SemaphoreSlim(1, 1);
            return sem;
        }
    }

    private async Task<IReadOnlyList<LiveSessionRef>?> FetchLiveSessionsAsync(CancellationToken ct)
        => LiveSessionsProvider is { } provider ? await provider(ct) : null;

    private async Task DrainDeferredRecomputesAsync(CancellationToken ct)
    {
        List<string> deferred;
        lock (_gate)
        {
            if (_deferredRecomputes.Count == 0)
                return;
            deferred = _deferredRecomputes.Values.ToList();
            _deferredRecomputes.Clear();
        }
        FileLog.Write($"[RepositoryMonitor] running {deferred.Count} recompute(s) deferred during the scan");
        foreach (var path in deferred)
        {
            try
            {
                await RecomputeOneAsync(path, ct);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[RepositoryMonitor] deferred recompute FAILED for {path}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Model-level enrichment applied on every upsert: a freshly computed status is never
    /// provisional, and dirty-since is carried forward from the previous entry (or stamped now when
    /// the tree just turned dirty), so "uncommitted work sitting for N days" survives rescans and -
    /// via the cache - restarts.
    /// </summary>
    internal static RepositoryStatus Enrich(RepositoryStatus fresh, RepositoryStatus? previous)
    {
        DateTime? dirtySince = null;
        if (!fresh.IsClean)
            dirtySince = previous is { IsClean: false, DirtySinceUtc: not null }
                ? previous.DirtySinceUtc
                : DateTime.UtcNow;

        return fresh with { Provisional = false, DirtySinceUtc = dirtySince };
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

    /// <summary>
    /// The primary checkout owning a linked worktree: git names the shared .git directory via
    /// rev-parse --git-common-dir; its parent is the primary working tree. Null when git cannot
    /// answer (not a repository, or a bare repository with no primary checkout).
    /// </summary>
    private static async Task<string?> DefaultResolvePrimary(string path, CancellationToken ct)
    {
        var result = await new GitCommandRunner().RunAsync(
            path, new[] { "rev-parse", "--path-format=absolute", "--git-common-dir" }, ct);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            return null;
        var commonDir = result.Output.Trim();
        if (!string.Equals(Path.GetFileName(commonDir.TrimEnd('/', '\\')), ".git", StringComparison.OrdinalIgnoreCase))
            return null; // bare repository - no primary working tree
        return Path.GetDirectoryName(commonDir.TrimEnd('/', '\\'));
    }
}
