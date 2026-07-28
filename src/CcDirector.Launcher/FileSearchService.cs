using System.Diagnostics;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Launcher;

/// <summary>
/// Filename search across every drive on THIS machine, so a caller on another machine can find a file
/// without knowing where it lives.
///
/// WHY THERE IS NO INDEX. An index answers faster and costs a background service that crawls the disk, keeps
/// a database, and is wrong whenever it is stale. This walks the live filesystem instead: slower on a broad
/// query, but it never reports a file that has been deleted, never misses one saved a moment ago, and needs
/// nothing running between searches. A machine-wide walk with a deadline is the honest version of the
/// feature; an index would be the fast version of a different one.
///
/// WHY A PARTIAL ANSWER IS A NORMAL RESULT. The walk is bounded twice - by a result ceiling and by a
/// deadline - because a broad query on a large disk would otherwise run for minutes and return more than any
/// caller wants. When either bound is hit the search returns what it has and says so, naming WHICH bound
/// stopped it. That distinction matters to the caller: hitting the ceiling means "narrow your query", while
/// hitting the deadline means "the same query might succeed with more time", and a single truncated flag
/// could not tell them apart.
/// </summary>
public sealed class FileSearchService
{
    /// <summary>The largest number of files any single answer may carry.</summary>
    public const int MaximumLimit = 1000;

    /// <summary>The default when a caller names no limit.</summary>
    public const int DefaultLimit = 200;

    /// <summary>The longest a search may run, however long the caller asks for.</summary>
    public const int MaximumTimeoutMilliseconds = 120_000;

    /// <summary>The default when a caller names no deadline.</summary>
    public const int DefaultTimeoutMilliseconds = 20_000;

    /// <summary>
    /// How long past the cooperative deadline the search waits for its walkers before abandoning them. It
    /// exists so a merely SLOW walker gets a chance to notice the deadline and stop itself - which counts as
    /// finishing - while a walker blocked in a system call that never returns is given up on.
    /// </summary>
    private const int JoinGraceMilliseconds = 2_000;

    /// <summary>
    /// A safety ceiling on how deep the walk descends. Reparse points are already stepped over, so this exists
    /// only for a filesystem that manages to nest pathologically without them.
    /// </summary>
    private const int MaximumDepth = 40;

    /// <summary>
    /// The directories the walk steps over, as ABSOLUTE PATHS rather than as bare names.
    ///
    /// WHY ABSOLUTE PATHS AND NOT NAMES. Matching on the name alone silently skips any directory that happens
    /// to share it - a developer's own "dev" folder in their home directory, or a project's "run" - and a
    /// filename search that quietly omits part of the disk is exactly the failure this whole class is written
    /// to avoid. The kernel trees that need skipping exist only at the filesystem root, so naming them in full
    /// costs nothing and removes the whole class of false match.
    ///
    /// WHAT IS SKIPPED, AND WHY EACH ONE:
    ///
    ///   * /proc, /sys, /dev, /run - Unix kernel and device trees. Not real files.
    ///   * /System/Volumes - the macOS firmlink to the data volume. THIS ONE IS NOT OPTIONAL. Verified on
    ///     Apple silicon: a firmlink reports ReparsePoint=false and a null LinkTarget, so it looks like an
    ///     ordinary directory to .NET and the reparse-point skip does NOT catch it. A walk from / that missed
    ///     this would re-enter the entire data volume - Users, Applications, private, all of it - and report
    ///     every file on the machine twice.
    ///   * /Volumes - externally mounted disks and network shares. Skipped for parity with Windows, where a
    ///     walk of one drive never crosses into another, and because a slow network mount can spend the whole
    ///     deadline before the walk reaches a local file.
    ///   * WinSxS - the Windows component store: hundreds of thousands of hard links to files that already
    ///     appear under their real names elsewhere. A judgement about DUPLICATES rather than relevance, and
    ///     the difference between a walk that finishes and one that always times out.
    ///   * The per-drive recycle bin and volume metadata, which hold nothing a person is searching for.
    /// </summary>
    private static HashSet<string> BuildSkippedPaths(IReadOnlyList<string> roots)
    {
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (OperatingSystem.IsWindows())
        {
            foreach (var root in roots)
            {
                skipped.Add(Path.Combine(root, "$Recycle.Bin"));
                skipped.Add(Path.Combine(root, "System Volume Information"));
            }
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windows))
                skipped.Add(Path.Combine(windows, "WinSxS"));
            return skipped;
        }

        skipped.Add("/proc");
        skipped.Add("/sys");
        skipped.Add("/dev");
        skipped.Add("/run");
        skipped.Add("/System/Volumes");
        skipped.Add("/Volumes");
        return skipped;
    }

    private readonly IReadOnlyList<string>? _rootsOverride;

    /// <summary>The production search, walking this machine's real drives.</summary>
    public FileSearchService() : this(null) { }

    /// <summary>
    /// Search explicit roots instead of the machine's drives. This exists for tests: walking every real drive
    /// would make a unit test depend on the contents and the size of whatever machine runs it.
    /// </summary>
    public FileSearchService(IReadOnlyList<string>? roots) => _rootsOverride = roots;

    /// <summary>
    /// Search every root for files whose name matches <paramref name="query"/>.
    ///
    /// A query containing a directory separator is matched against the whole path, so "Repos\devthrottle" and
    /// "*/Downloads/*.pdf" behave the way they read. Any other query is matched against the filename alone,
    /// which is what a bare "budget.xlsx" or "*.pptx" is asking for.
    /// </summary>
    public FileSearchResultDto Search(string? query, int limit, int timeoutMilliseconds, CancellationToken ct)
    {
        FileLog.Write($"[FileSearchService] Search: query={query ?? "(all)"}, limit={limit}, timeout={timeoutMilliseconds}");

        var effectiveLimit = limit <= 0 ? DefaultLimit : Math.Min(limit, MaximumLimit);
        var effectiveTimeout = timeoutMilliseconds <= 0
            ? DefaultTimeoutMilliseconds
            : Math.Min(timeoutMilliseconds, MaximumTimeoutMilliseconds);

        var pattern = SearchPattern.Parse(query);
        var matchWholePath = query is not null && (query.Contains('\\') || query.Contains('/'));
        var roots = Roots().ToList();

        var skippedPaths = BuildSkippedPaths(roots);

        var stopwatch = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(effectiveTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, ct);

        var hits = new System.Collections.Concurrent.ConcurrentQueue<FileHitDto>();
        var counters = new WalkCounters();
        var found = 0;
        var hitLimit = false;

        // One walker per root: the roots are usually separate physical devices, so walking them together is
        // most of the wall-clock saving available here. They share the counters, the deadline and the ceiling,
        // so the bounds apply to the search as a whole rather than once per drive.
        //
        // THE DEADLINE IS ENFORCED AT THE JOIN, NOT ONLY INSIDE THE WALK, AND THAT IS THE WHOLE POINT OF THIS
        // SHAPE. The walk checks the cancellation token between directories, which is enough only while the
        // walk keeps RETURNING from the filesystem. On macOS it does not always: a directory guarded by the
        // operating system's privacy consent - Documents, Desktop, Downloads and the like - does not fail to
        // open when the launcher lacks that consent. The open call BLOCKS IN THE KERNEL INDEFINITELY. A thread
        // inside that call never reaches another token check, so a cooperative deadline cannot reach it, and
        // an ordinary parallel loop would wait on that thread forever - the whole search would never answer.
        // Verified on macOS: opening one such folder hung with no timeout and no processor use, and did so
        // identically outside .NET, so it is the system call and not the runtime.
        //
        // So the walkers run on their own background threads and the search waits on a countdown with a
        // timeout. When the countdown expires, whatever has been found is returned and the stuck thread is
        // ABANDONED - it is a background thread, so it cannot hold the process open, and the alternative is an
        // answer that never comes. The abandonment is counted and reported rather than hidden.
        //
        // The unreadable-directory reporting elsewhere in this class assumes a failed open RETURNS an error.
        // That assumption is sound on Windows and on Linux; this is the one path where it is not, which is why
        // it is handled here rather than there.
        var finished = new CountdownEvent(roots.Count);
        foreach (var root in roots)
        {
            var capturedRoot = root;
            var worker = new Thread(() =>
            {
                try
                {
                    foreach (var file in WalkRoot(capturedRoot, linked.Token, counters, skippedPaths))
                    {
                        var candidate = matchWholePath ? file.FullName : file.Name;
                        if (!pattern.IsMatch(candidate)) continue;

                        if (Interlocked.Increment(ref found) > effectiveLimit)
                        {
                            Volatile.Write(ref hitLimit, true);
                            return;
                        }

                        hits.Enqueue(new FileHitDto
                        {
                            Path = file.FullName,
                            Name = file.Name,
                            SizeBytes = file.Length,
                            ModifiedUtc = file.LastWriteTimeUtc,
                        });
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[FileSearchService] walker for {capturedRoot} FAILED: {ex.Message}");
                }
                finally
                {
                    // A walker abandoned at the deadline may still finish long afterwards and signal then. The
                    // countdown is deliberately never disposed for exactly that reason: signalling a disposed
                    // countdown would throw on a thread with no handler, which on a background thread takes the
                    // whole launcher down.
                    try { finished.Signal(); }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                }
            })
            {
                IsBackground = true,
                Name = $"file-search:{capturedRoot}",
            };
            worker.Start();
        }

        // The grace is on top of the cooperative deadline so a walker that is merely SLOW gets the chance to
        // stop itself and be counted as complete; only a walker that is genuinely stuck is abandoned.
        var allWalkersFinished = finished.Wait(effectiveTimeout + JoinGraceMilliseconds);
        var abandonedRoots = allWalkersFinished ? 0 : finished.CurrentCount;

        stopwatch.Stop();

        if (!allWalkersFinished)
            FileLog.Write($"[FileSearchService] Search: {abandonedRoots} of {roots.Count} walkers did not return by " +
                          "the deadline and were abandoned - on macOS this is the privacy-consent block; the " +
                          "launcher needs Full Disk Access for those folders to be searchable.");

        // The ceiling is reported ahead of the deadline when both were reached: a caller that hit the ceiling
        // gets the same advice - narrow the query - whether or not time also ran out.
        var timedOut = deadline.IsCancellationRequested || !allWalkersFinished;
        var truncationReason = Volatile.Read(ref hitLimit) ? "limit" : timedOut ? "timeout" : null;

        var result = new FileSearchResultDto
        {
            Machine = Environment.MachineName,
            Query = query ?? "",
            Files = hits.OrderBy(hit => hit.Path, StringComparer.OrdinalIgnoreCase).Take(effectiveLimit).ToList(),
            Roots = roots,
            DirectoriesVisited = counters.DirectoriesVisited,
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            Truncated = truncationReason is not null,
            TruncationReason = truncationReason,
            UnreadableDirectories = counters.UnreadableDirectories,
            AbandonedRoots = abandonedRoots,
        };

        FileLog.Write($"[FileSearchService] Search: returned={result.Files.Count}, visited={result.DirectoriesVisited}, " +
                      $"unreadable={result.UnreadableDirectories}, elapsed={result.ElapsedMilliseconds}ms, " +
                      $"truncated={result.Truncated} ({result.TruncationReason ?? "complete"})");
        return result;
    }

    /// <summary>
    /// The roots to search. On Windows that is every fixed drive that is ready; elsewhere it is the
    /// filesystem root, with the kernel and device trees skipped during the walk.
    /// </summary>
    private IEnumerable<string> Roots()
    {
        if (_rootsOverride is not null)
        {
            foreach (var root in _rootsOverride) yield return root;
            yield break;
        }

        if (!OperatingSystem.IsWindows())
        {
            yield return "/";
            yield break;
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            // Network and removable drives are left out on purpose: a disconnected mapped drive blocks for
            // its full network timeout on the first access, which would spend the whole deadline before the
            // walk reached a single local file.
            if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                yield return drive.RootDirectory.FullName;
        }
    }

    /// <summary>
    /// The counts every walker adds to. It is a shared object rather than a pair of by-reference arguments
    /// because a by-reference argument cannot cross into an iterator method, and the walk has to be an
    /// iterator so the caller can stop it the moment the result ceiling is reached.
    /// </summary>
    private sealed class WalkCounters
    {
        private int _directoriesVisited;
        private int _unreadableDirectories;

        public int DirectoriesVisited => Volatile.Read(ref _directoriesVisited);
        public int UnreadableDirectories => Volatile.Read(ref _unreadableDirectories);

        public void CountVisited() => Interlocked.Increment(ref _directoriesVisited);
        public void CountUnreadable() => Interlocked.Increment(ref _unreadableDirectories);
    }

    /// <summary>
    /// Walk one root breadth-first, yielding every file.
    ///
    /// The per-directory try-catch is the expected path, not defensive padding: a whole-machine walk crosses
    /// thousands of directories the current user cannot read, and that is an ordinary condition. Each one is
    /// counted so the caller learns how much of the machine was unreadable instead of being told nothing.
    /// </summary>
    private static IEnumerable<FileInfo> WalkRoot(string root, CancellationToken ct, WalkCounters counters,
        HashSet<string> skippedPaths)
    {
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            if (ct.IsCancellationRequested) break;
            var (directory, depth) = queue.Dequeue();
            counters.CountVisited();

            string[] subdirectories;
            FileInfo[] files;
            try
            {
                var info = new DirectoryInfo(directory);
                subdirectories = Directory.GetDirectories(directory);
                files = info.GetFiles();
            }
            catch (Exception)
            {
                counters.CountUnreadable();
                continue;
            }

            foreach (var file in files)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return file;
            }

            if (depth >= MaximumDepth) continue;

            foreach (var subdirectory in subdirectories)
            {
                if (skippedPaths.Contains(subdirectory)) continue;
                if (IsReparsePoint(subdirectory)) continue;
                queue.Enqueue((subdirectory, depth + 1));
            }
        }
    }

    /// <summary>
    /// True for a symbolic link or junction. Stepping over these is what stops a link pointing at an ancestor
    /// from sending the walk round the same tree until the deadline expires.
    /// </summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch
        {
            // Unreadable attributes mean the walk cannot prove this is safe to descend into, so it does not.
            return true;
        }
    }
}
