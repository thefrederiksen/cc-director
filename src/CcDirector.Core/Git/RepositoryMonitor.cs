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
    /// recomputes alike. The host MUST wire it before the first scan (ruling R2-8): scanning without
    /// a session source would silently publish session-blind safety classifications, so
    /// <see cref="RescanAsync"/> and <see cref="RecomputeOneAsync"/> throw while it is null - a
    /// programming error fails loudly instead of degrading.
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>>? LiveSessionsProvider { get; set; }

    /// <summary>Fails loudly when no live-session source is wired (ruling R2-8).</summary>
    private void RequireLiveSessionsProvider(string operation)
    {
        if (LiveSessionsProvider is null)
            throw new InvalidOperationException(
                $"RepositoryMonitor.{operation} requires a LiveSessionsProvider - scanning without a " +
                "session source would publish session-blind safety classifications. Wire the provider " +
                "before triggering any scan or recompute.");
    }

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
        RequireLiveSessionsProvider(nameof(RescanAsync));
        CancellationTokenSource cts;
        long scanStartStamp;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
            cts = _cts;
            // The scan's removals publish with the SCAN'S OWN START stamp (ruling R3-5),
            // captured here when the scan begins - never a fresh stamp at reconciliation time.
            // A compute that starts after this moment carries a newer stamp, so its publish
            // outranks this scan's removals: a repository created and published mid-scan
            // survives the scan's reconciliation instead of being removed by it.
            scanStartStamp = NextComputeStampLocked();
        }
        var ct = cts.Token;

        var paths = _enumerate(roots);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        lock (_gate) { IsScanning = true; ScanTotal = paths.Count; ScanDone = 0; }
        ProgressChanged?.Invoke();
        FileLog.Write($"[RepositoryMonitor] rescan started: {paths.Count} repositories");

        bool completed = false;
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
                removed = new List<RepositoryStatus>();
                foreach (var kv in _byPath.Where(kv => !seen.Contains(kv.Key)).ToList())
                {
                    // A removal is a publish of "absent" (ruling R2-5) stamped with the scan's
                    // START stamp (ruling R3-5). A key whose recorded publish is NEWER than the
                    // scan start was legitimately published by a compute that began after this
                    // scan enumerated - that publish outranks the removal, so the entry stays.
                    // (A repository genuinely gone is removed by its own gone-path recompute or
                    // by the next scan.)
                    if (_publishStamps.TryGetValue(kv.Key, out var newest) && newest > scanStartStamp)
                        continue;
                    _byPath.Remove(kv.Key);
                    _publishStamps[kv.Key] = scanStartStamp;
                    removed.Add(kv.Value);
                }
            }
            foreach (var r in removed)
                Removed?.Invoke(r);

            // Persist the verified model so the next launch warm-starts.
            SaveCache();

            // The size cache only stays meaningful for worktrees that still exist: evict entries
            // this completed scan did not see (reaped, moved, or deleted worktrees).
            RepositoryStatusService.EvictSizeCacheExcept(CurrentWorktreePaths());

            // Per-repository compute state is evicted alongside it (ruling R2-11): semaphores
            // and stale publish stamps for keys no longer in the model would otherwise
            // accumulate for the process lifetime.
            var removedThisScan = new HashSet<string>(
                removed.Select(r => WorktreeReaperService.NormalizePath(r.Path)),
                StringComparer.OrdinalIgnoreCase);
            EvictStaleComputeState(removedThisScan);
            completed = true;
        }
        catch (OperationCanceledException)
        {
            FileLog.Write("[RepositoryMonitor] rescan superseded/cancelled");
        }
        finally
        {
            // Scan lifecycle state belongs to the CURRENT scan only (ruling R3-6): ownership is
            // checked under the gate against the monitor's current cancellation source. A
            // superseded scan exits without touching IsScanning or progress - its replacement is
            // still scanning and owns both.
            bool owner;
            lock (_gate)
            {
                owner = ReferenceEquals(_cts, cts);
                if (owner)
                    IsScanning = false;
            }
            if (owner)
            {
                ProgressChanged?.Invoke();
                // Deferred requests are drained on EVERY exit path of the OWNING scan (ruling
                // R3-6) - completed, externally cancelled, or faulted - so a cancelled scan with
                // no successor never strands them. Each deferred request kept its ORIGINAL
                // requester's token (ruling R2-5). When a newer scan superseded this one, that
                // newer scan owns the drain: every one of ITS exit paths runs this same block.
                await DrainDeferredRecomputesAsync();
            }
        }

        if (!completed)
            return; // superseded or cancelled - never reports completion

        FileLog.Write($"[RepositoryMonitor] rescan completed: {ScanDone}/{ScanTotal}");
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
        RequireLiveSessionsProvider(nameof(RecomputeOneAsync));
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
                    _byPath.Remove(key);
                // Absence is ALWAYS stamped (ruling R3-4a) - whether or not a model row exists.
                // A FIRST-TIME compute can be in flight for this key with no row published yet;
                // without a tombstone its later publish would create the very repository this
                // newer check just saw vanish. A removal is a publish of "absent" (ruling R2-5).
                _publishStamps[key] = NextComputeStampLocked();
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

    /// <summary>
    /// Evicts per-repository compute state after a completed scan (ruling R2-11): a semaphore is
    /// removed when its key is no longer in the model AND it is currently un-held (a held one has
    /// a compute in flight and is kept). Accepted and documented crossover: a compute that still
    /// holds a REFERENCE to an evicted semaphore while the same key is re-created briefly gives
    /// that key two semaphores - single-flight degrades to double-flight for one compute, and the
    /// publish-stamp rule (R2-5) still guarantees the newest compute start wins. Evicted
    /// semaphores are not disposed: a stale reference may still be awaited, and an un-disposed
    /// SemaphoreSlim without a wait handle holds no operating system resources.
    ///
    /// Publish stamps are evicted for keys neither in the model nor removed by THIS scan: a
    /// removal stamp must survive until the next completed scan so it can still drop a late
    /// publish from a compute that started before the removal. A stamp whose key's semaphore is
    /// currently HELD is never evicted (ruling R3-4b) - the held semaphore proves a compute is
    /// still in flight, and its tombstone must outlive that compute whatever the scan count.
    /// </summary>
    private void EvictStaleComputeState(IReadOnlySet<string> removedThisScan)
    {
        lock (_gate)
        {
            int evictedLocks = 0, evictedStamps = 0;
            foreach (var key in _repoLocks.Keys.ToList())
            {
                if (_byPath.ContainsKey(key))
                    continue;
                if (_repoLocks[key].CurrentCount == 0)
                    continue; // held - a compute is in flight for this key right now
                _repoLocks.Remove(key);
                evictedLocks++;
            }
            foreach (var key in _publishStamps.Keys.ToList())
            {
                if (_byPath.ContainsKey(key) || removedThisScan.Contains(key))
                    continue;
                // A held semaphore is proof a compute is still in flight for this key (ruling
                // R3-4b): its stamp - a removal tombstone in particular - must survive until
                // that compute has published and been dropped, however many scans complete in
                // between. Eviction never removes stamp state out from under a live compute.
                if (_repoLocks.TryGetValue(key, out var heldCheck) && heldCheck.CurrentCount == 0)
                    continue;
                _publishStamps.Remove(key);
                evictedStamps++;
            }
            if (evictedLocks > 0 || evictedStamps > 0)
                FileLog.Write($"[RepositoryMonitor] evicted compute state for departed repositories: {evictedLocks} semaphore(s), {evictedStamps} stamp(s)");
        }
    }

    /// <summary>Test probe: whether a per-repository semaphore exists for this path.</summary>
    internal bool RepoLockExistsFor(string path)
    {
        lock (_gate)
            return _repoLocks.ContainsKey(WorktreeReaperService.NormalizePath(path));
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
    {
        // The entry points already refused to start unwired (ruling R2-8); this guard keeps the
        // failure loud even if the provider were unset mid-flight.
        var provider = LiveSessionsProvider
            ?? throw new InvalidOperationException("RepositoryMonitor lost its LiveSessionsProvider mid-compute");
        return await provider(ct);
    }

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
