using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Watches the registered root directories and each known repository's git-state signals, and asks
/// the <see cref="RepositoryMonitor"/> to recompute ONLY the affected repository - so after the
/// first scan the model stays current by reacting to change instead of re-scanning everything
/// (issue devthrottle_internal#510, phase A).
///
/// Deliberately narrow watch set:
/// - each ROOT, non-recursive, directory events only: a repo folder appearing or vanishing;
/// - each repo's .git, recursive, but FILTERED to HEAD, packed-refs, refs/, logs/HEAD, and
///   worktrees/ - the signals that change repo/worktree state.
/// It deliberately IGNORES .git/index and .git/objects: our own status scans touch the index, so
/// watching it would feed the watcher its own echo, and object writes are covered by the reflog.
/// Working-tree files are never watched (builds and node_modules would flood us).
///
/// Events are debounced per repository (a burst collapses to one recompute).
/// </summary>
public sealed class RepositoryWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    private readonly RepositoryMonitor _monitor;
    private readonly object _gate = new();
    private readonly Dictionary<string, FileSystemWatcher> _rootWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _repoWatchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>Raised after a debounced change triggered a recompute (test observability).</summary>
    public event Action<string>? Recomputed;

    public RepositoryWatcher(RepositoryMonitor monitor)
    {
        _monitor = monitor;
    }

    /// <summary>
    /// Bring the watch set in line with the current roots and known repositories. Idempotent - call
    /// after every completed scan; new repos gain a watcher, vanished ones lose theirs.
    /// </summary>
    public void SyncWatches(IEnumerable<string> roots, IEnumerable<string> repoPaths)
    {
        if (_disposed) return;
        lock (_gate)
        {
            var wantedRoots = roots.Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var wantedRepos = repoPaths.Where(Directory.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var stale in _rootWatchers.Keys.Where(k => !wantedRoots.Contains(k)).ToList())
            {
                _rootWatchers[stale].Dispose();
                _rootWatchers.Remove(stale);
            }
            foreach (var stale in _repoWatchers.Keys.Where(k => !wantedRepos.Contains(k)).ToList())
            {
                _repoWatchers[stale].Dispose();
                _repoWatchers.Remove(stale);
            }

            foreach (var root in wantedRoots.Where(r => !_rootWatchers.ContainsKey(r)))
            {
                try { _rootWatchers[root] = CreateRootWatcher(root); }
                catch (Exception ex) { FileLog.Write($"[RepositoryWatcher] root watch failed for {root}: {ex.Message}"); }
            }
            foreach (var repo in wantedRepos.Where(r => !_repoWatchers.ContainsKey(r)))
            {
                var gitDir = Path.Combine(repo, ".git");
                if (!Directory.Exists(gitDir))
                    continue; // a worktree-style .git FILE has its real state under the primary's .git
                try { _repoWatchers[repo] = CreateGitWatcher(repo, gitDir); }
                catch (Exception ex) { FileLog.Write($"[RepositoryWatcher] git watch failed for {repo}: {ex.Message}"); }
            }

            FileLog.Write($"[RepositoryWatcher] watching {_rootWatchers.Count} roots, {_repoWatchers.Count} repositories");
        }
    }

    private FileSystemWatcher CreateRootWatcher(string root)
    {
        var w = new FileSystemWatcher(root)
        {
            NotifyFilter = NotifyFilters.DirectoryName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        void OnDir(object _, FileSystemEventArgs e) => Schedule(Path.Combine(root, Path.GetFileName(e.Name ?? "")));
        w.Created += OnDir;
        w.Deleted += OnDir;
        w.Renamed += (_, e) =>
        {
            Schedule(Path.Combine(root, Path.GetFileName(e.OldName ?? "")));
            Schedule(Path.Combine(root, Path.GetFileName(e.Name ?? "")));
        };
        return w;
    }

    private FileSystemWatcher CreateGitWatcher(string repoPath, string gitDir)
    {
        var w = new FileSystemWatcher(gitDir)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
        };
        void OnGit(object _, FileSystemEventArgs e)
        {
            if (IsSignal(e.Name))
                Schedule(repoPath);
        }
        w.Created += OnGit;
        w.Deleted += OnGit;
        w.Changed += OnGit;
        w.Renamed += (_, e) => { if (IsSignal(e.Name) || IsSignal(e.OldName)) Schedule(repoPath); };
        return w;
    }

    /// <summary>True when a path relative to .git is a state signal we react to. Pure and testable.</summary>
    internal static bool IsSignal(string? relative)
    {
        if (string.IsNullOrEmpty(relative))
            return false;
        var p = relative.Replace('/', '\\');
        return p.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
            || p.Equals("packed-refs", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("refs\\", StringComparison.OrdinalIgnoreCase)
            || p.Equals("logs\\HEAD", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("worktrees\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Debounced per-repo: a burst of events collapses into one recompute.</summary>
    private void Schedule(string repoPath)
    {
        if (_disposed || string.IsNullOrWhiteSpace(repoPath))
            return;
        var key = WorktreeReaperService.NormalizePath(repoPath);
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_pending.TryGetValue(key, out var old))
                old.Cancel();
            cts = new CancellationTokenSource();
            _pending[key] = cts;
        }
        _ = DebouncedRecomputeAsync(repoPath, key, cts);
    }

    private async Task DebouncedRecomputeAsync(string repoPath, string key, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(Debounce, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer event for the same repo
        }

        lock (_gate)
        {
            if (_pending.TryGetValue(key, out var current) && current == cts)
                _pending.Remove(key);
        }

        try
        {
            FileLog.Write($"[RepositoryWatcher] change settled - recomputing {repoPath}");
            await _monitor.RecomputeOneAsync(repoPath);
            Recomputed?.Invoke(repoPath);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[RepositoryWatcher] recompute failed for {repoPath}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var w in _rootWatchers.Values) w.Dispose();
            foreach (var w in _repoWatchers.Values) w.Dispose();
            _rootWatchers.Clear();
            _repoWatchers.Clear();
            foreach (var c in _pending.Values) c.Cancel();
            _pending.Clear();
        }
    }
}
