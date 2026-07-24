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
/// - Newest compute wins, enforced AT THE PUBLISH (inspection round 2, ruling R2-5): every compute
///   takes a monotonically increasing start stamp, and a publish whose stamp is older than the one
///   the model already recorded for that key - including a removal - is dropped. The per-repository
///   semaphore (single-flight) and the defer-during-scan rule remain as efficiency devices; the
///   stamp rule is the correctness device. Deferred recomputes keep their requester's own token.
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
    private readonly Dictionary<string, DeferredRecompute> _deferredRecomputes = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private readonly string? _cachePath;

    /// <summary>A single-repository recompute parked while a full scan runs - it keeps the
    /// ORIGINAL requester's token, so a request whose requester gave up is skipped at drain.</summary>
    private readonly record struct DeferredRecompute(string Path, CancellationToken Token);

    /// <summary>
    /// Monotonically increasing compute-start stamps (ruling R2-5): every compute - scan or
    /// single recompute - takes a stamp when it STARTS, and the model records per key the stamp
    /// of the newest accepted publish (a removal counts as a publish of "absent"). A publish
    /// whose stamp is older than the recorded one is dropped, so an older compute can never
    /// overwrite - or resurrect - a newer result, whatever order the publishes arrive in. The
    /// per-repository semaphore remains an efficiency device (it avoids duplicate concurrent
    /// walks); THIS rule is the correctness device.
    /// </summary>
    private long _computeStampCounter;
    private readonly Dictionary<string, long> _publishStamps = new(StringComparer.OrdinalIgnoreCase);

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
                RepositoryStatus? published;
                try
                {
                    var computeStamp = NextComputeStamp();
                    var sessions = await FetchLiveSessionsAsync(ct);
                    var status = await _compute(path, sessions, ct);
                    var key = WorktreeReaperService.NormalizePath(status.Path);
                    seen.Add(key);
                    lock (_gate)
                    {
                        // A cancelled or superseded scan never publishes (ruling R2-2):
                        // cancellation can land in the narrow interval after the compute
                        // returned, and the next loop iteration's check is too late. Re-check
                        // the token AND that this scan still owns the model, under the gate,
                        // before writing anything.
                        if (ct.IsCancellationRequested || !ReferenceEquals(_cts, cts))
                            throw new OperationCanceledException(ct);
                        published = PublishIfNewestLocked(key, status, computeStamp);
                        ScanDone++;
                    }
                }
                finally
                {
                    repoLock.Release();
                }
                if (published != null)
                    Upserted?.Invoke(published);
                ProgressChanged?.Invoke();
            }

            // Reconcile: drop repositories that were in the model but not found this scan.
            List<RepositoryStatus> removed;
            lock (_gate)
            {
                // Same rule at the reconcile (ruling R2-2): only the owning, uncancelled scan
                // may remove entries - a superseded scan's roots are not the truth any more.
                if (ct.IsCancellationRequested || !ReferenceEquals(_cts, cts))
                    throw new OperationCanceledException(ct);
                removed = _byPath.Where(kv => !seen.Contains(kv.Key)).Select(kv => kv.Value).ToList();
                foreach (var r in removed)
                {
                    var removedKey = WorktreeReaperService.NormalizePath(r.Path);
                    _byPath.Remove(removedKey);
                    // A removal is a publish of "absent" (ruling R2-5): stamp it, so an older
                    // compute still in flight cannot publish late and resurrect the repository.
                    _publishStamps[removedKey] = NextComputeStampLocked();
                }
            }
            foreach (var r in removed)
                Removed?.Invoke(r);

            // Persist the verified model so the next launch warm-starts.
            SaveCache();

            // The size cache only stays meaningful for worktrees that still exist: evict entries
            // this completed scan did not see (reaped, moved, or deleted worktrees).
            RepositoryStatusService.EvictSizeCacheExcept(CurrentWorktreePaths());
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
        // Each deferred request kept its ORIGINAL requester's token (ruling R2-5).
        await DrainDeferredRecomputesAsync();

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
                // The deferred request keeps ITS OWN token (ruling R2-5): if the requester gives
                // up before the scan completes, the drain skips this request instead of running
                // it under someone else's token.
                _deferredRecomputes[WorktreeReaperService.NormalizePath(repoPath)] = new DeferredRecompute(repoPath, ct);
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
                {
                    _byPath.Remove(key);
                    // A removal is a publish of "absent" (ruling R2-5): stamp it so an older
                    // compute still in flight cannot publish late and resurrect the entry.
                    _publishStamps[key] = NextComputeStampLocked();
                }
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
        RepositoryStatus? published;
        try
        {
            var computeStamp = NextComputeStamp();
            var sessions = await FetchLiveSessionsAsync(ct);
            var status = await _compute(targetPath, sessions, ct);
            lock (_gate)
                published = PublishIfNewestLocked(targetKey, status, computeStamp);
        }
        finally
        {
            repoLock.Release();
        }
        if (published == null)
            return; // a newer compute (or a newer removal) already ruled this key - dropped
        FileLog.Write($"[RepositoryMonitor] recomputed one: {published.Name}");
        Upserted?.Invoke(published);
        SaveCache();
    }

    /// <summary>
    /// One compute at a time per repository. An EFFICIENCY device only (it avoids duplicate
    /// concurrent walks of the same working tree) - publish ordering is guaranteed by the
    /// compute-start stamps in <see cref="PublishIfNewestLocked"/>, not by this lock.
    /// </summary>
    private SemaphoreSlim GetRepoLock(string key)
    {
        lock (_gate)
        {
            if (!_repoLocks.TryGetValue(key, out var sem))
                _repoLocks[key] = sem = new SemaphoreSlim(1, 1);
            return sem;
        }
    }

    /// <summary>The next compute-start stamp. Callers must NOT hold <see cref="_gate"/>.</summary>
    private long NextComputeStamp()
    {
        lock (_gate)
            return NextComputeStampLocked();
    }

    /// <summary>The next compute-start stamp. Callers MUST hold <see cref="_gate"/>.</summary>
    private long NextComputeStampLocked() => ++_computeStampCounter;

    /// <summary>
    /// THE one guarded publish path (ruling R2-5) - every write of a computed status into the
    /// model, from the full scan and from single recomputes alike, goes through here. Newest
    /// compute start wins: when the key already carries a newer stamp (a newer compute
    /// published, or a newer scan removed the key), this publish is DROPPED and null is
    /// returned. Callers must hold <see cref="_gate"/>.
    /// </summary>
    private RepositoryStatus? PublishIfNewestLocked(string key, RepositoryStatus status, long computeStamp)
    {
        if (_publishStamps.TryGetValue(key, out var newest) && newest > computeStamp)
        {
            FileLog.Write($"[RepositoryMonitor] publish dropped for {status.Path}: a newer compute (stamp {newest}) already ruled this key (this compute started at stamp {computeStamp})");
            return null;
        }
        _publishStamps[key] = computeStamp;
        var enriched = Enrich(status, _byPath.TryGetValue(key, out var prev) ? prev : null);
        _byPath[key] = enriched;
        return enriched;
    }

    private async Task<IReadOnlyList<LiveSessionRef>?> FetchLiveSessionsAsync(CancellationToken ct)
        => LiveSessionsProvider is { } provider ? await provider(ct) : null;

    private IReadOnlyList<string> CurrentWorktreePaths()
    {
        lock (_gate)
            return _byPath.Values.SelectMany(s => s.Worktrees).Select(w => w.Path).ToList();
    }

    private async Task DrainDeferredRecomputesAsync()
    {
        List<DeferredRecompute> deferred;
        lock (_gate)
        {
            if (_deferredRecomputes.Count == 0)
                return;
            deferred = _deferredRecomputes.Values.ToList();
            _deferredRecomputes.Clear();
        }
        FileLog.Write($"[RepositoryMonitor] running {deferred.Count} recompute(s) deferred during the scan");
        foreach (var request in deferred)
        {
            // The request runs under ITS OWN requester's token (ruling R2-5) - never the
            // scan's. A requester that already gave up is skipped, not run on its behalf.
            if (request.Token.IsCancellationRequested)
            {
                FileLog.Write($"[RepositoryMonitor] deferred recompute skipped - its requester cancelled: {request.Path}");
                continue;
            }
            try
            {
                await RecomputeOneAsync(request.Path, request.Token);
            }
            catch (OperationCanceledException)
            {
                FileLog.Write($"[RepositoryMonitor] deferred recompute cancelled by its requester: {request.Path}");
            }
            catch (Exception ex)
            {
                // A deferred failure is an ERROR - the requester cannot observe it, so the log
                // is the only place it can surface. Never absorbed into a success path.
                FileLog.Write($"[RepositoryMonitor] ERROR: deferred recompute failed for {request.Path}: {ex}");
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
