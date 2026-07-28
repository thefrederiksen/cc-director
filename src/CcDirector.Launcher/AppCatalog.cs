using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Launcher;

/// <summary>
/// The catalogue of applications installed on THIS machine, so a caller on another machine can find out what
/// is here without knowing any of its paths.
///
/// WHAT IT READS, AND WHY THAT AND NOT THE REGISTRY. On Windows the catalogue is the Start Menu - the
/// per-user tree and the machine-wide tree - and each entry is a shortcut. The Windows uninstall registry
/// keys are the other obvious source and they are deliberately not used: they list everything that ever
/// registered an uninstaller, including runtimes, redistributables and update helpers that a person cannot
/// meaningfully "start", and many of their entries carry no launchable path at all. The Start Menu is the
/// list of things a person can actually click, which is the list this feature is for.
///
/// The entry path is the SHORTCUT, not the program's own executable. See <see cref="InstalledAppDto"/> for
/// why that is the right target rather than a shortfall.
///
/// On macOS the catalogue is the application bundle directories, and on Linux the desktop entry directories.
/// The same class serves all three because the launcher itself is built for all three.
/// </summary>
public sealed class AppCatalog
{
    /// <summary>The largest number of applications any single answer may carry.</summary>
    public const int MaximumLimit = 500;

    /// <summary>The default when a caller names no limit.</summary>
    public const int DefaultLimit = 100;

    /// <summary>
    /// How deep to walk below a catalogue root. The Start Menu nests by vendor and occasionally by product
    /// beneath that; six levels covers every real layout with room to spare and stops a directory cycle from
    /// turning a fast walk into a hang.
    /// </summary>
    private const int MaximumDepth = 6;

    private readonly IReadOnlyList<(string Root, string Source)>? _rootsOverride;

    /// <summary>The production catalogue, reading this machine's real application directories.</summary>
    public AppCatalog() : this(null) { }

    /// <summary>
    /// Build a catalogue over explicit roots instead of the machine's own. This exists for tests: a test that
    /// searched the real Start Menu would assert on whatever the machine running it happens to have installed,
    /// which is a test of the machine rather than of the catalogue.
    /// </summary>
    public AppCatalog(IReadOnlyList<(string Root, string Source)>? roots) => _rootsOverride = roots;

    /// <summary>
    /// Search the catalogue. An empty query returns everything, up to the limit.
    /// </summary>
    public AppSearchResultDto Search(string? query, int limit)
    {
        FileLog.Write($"[AppCatalog] Search: query={query ?? "(all)"}, limit={limit}");

        var effectiveLimit = limit <= 0 ? DefaultLimit : Math.Min(limit, MaximumLimit);
        var pattern = SearchPattern.Parse(query);
        var skipped = new List<string>();

        var matches = Enumerate(skipped)
            .Where(app => pattern.IsMatch(app.Name))
            .OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new AppSearchResultDto
        {
            Machine = Environment.MachineName,
            TotalMatches = matches.Count,
            Truncated = matches.Count > effectiveLimit,
            Apps = matches.Take(effectiveLimit).ToList(),
            Skipped = skipped,
        };

        FileLog.Write($"[AppCatalog] Search: matched={result.TotalMatches}, returned={result.Apps.Count}, " +
                      $"truncated={result.Truncated}, skippedDirectories={skipped.Count}");
        return result;
    }

    /// <summary>How a request to launch an application BY NAME was resolved.</summary>
    public enum ResolveStatus
    {
        /// <summary>Exactly one application answers to that name.</summary>
        Found,

        /// <summary>Nothing on this machine answers to that name.</summary>
        NotFound,

        /// <summary>Several applications answer to that name, so the launcher refuses to guess between them.</summary>
        Ambiguous,
    }

    /// <summary>The outcome of <see cref="Resolve"/>.</summary>
    /// <param name="Status">Which of the three outcomes occurred.</param>
    /// <param name="App">The single match, when the status is <see cref="ResolveStatus.Found"/>.</param>
    /// <param name="Candidates">The competing names, when the status is <see cref="ResolveStatus.Ambiguous"/>.</param>
    public sealed record ResolveOutcome(ResolveStatus Status, InstalledAppDto? App = null,
        IReadOnlyList<string>? Candidates = null);

    /// <summary>
    /// Resolve an application name to something launchable.
    ///
    /// An exact name match always wins outright, so "Notepad" starts Notepad even on a machine that also has
    /// "Notepad++" installed. Only when nothing matches exactly does it fall back to a substring match, and
    /// then only if the substring picks out exactly one application. Several matches is reported as ambiguous
    /// rather than resolved to the first one: starting the wrong program on a machine the caller cannot see is
    /// a worse outcome than being asked to be more specific.
    /// </summary>
    public ResolveOutcome Resolve(string name)
    {
        FileLog.Write($"[AppCatalog] Resolve: name={name}");
        if (string.IsNullOrWhiteSpace(name))
            return new ResolveOutcome(ResolveStatus.NotFound);

        var all = Enumerate(new List<string>());

        var exact = all.Where(app => string.Equals(app.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count >= 1)
        {
            // More than one exact match is the per-user and machine-wide Start Menu trees both carrying the
            // same program, which is the normal Windows arrangement rather than two different applications.
            // Taking the first is safe for that case and is logged so the other case is still visible.
            if (exact.Count > 1)
                FileLog.Write($"[AppCatalog] Resolve: {exact.Count} exact matches for '{name}', taking the first: " +
                              string.Join(", ", exact.Select(app => app.Path)));
            FileLog.Write($"[AppCatalog] Resolve: exact match -> {exact[0].Path}");
            return new ResolveOutcome(ResolveStatus.Found, exact[0]);
        }

        var partial = all.Where(app => app.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count == 1)
        {
            FileLog.Write($"[AppCatalog] Resolve: single substring match -> {partial[0].Path}");
            return new ResolveOutcome(ResolveStatus.Found, partial[0]);
        }

        if (partial.Count > 1)
        {
            var candidates = partial.Select(app => app.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(10).ToList();
            FileLog.Write($"[AppCatalog] Resolve: AMBIGUOUS, {partial.Count} matches for '{name}'");
            return new ResolveOutcome(ResolveStatus.Ambiguous, Candidates: candidates);
        }

        FileLog.Write($"[AppCatalog] Resolve: no match for '{name}'");
        return new ResolveOutcome(ResolveStatus.NotFound);
    }

    /// <summary>
    /// Work out what a launch request actually points at, given a path, an application name, or both.
    ///
    /// This is the ONE place that decision is made. The loopback route and the Gateway command stream both
    /// call it, so the two ways of asking this launcher to start something cannot come to different answers -
    /// the same rule the lifecycle verbs already follow by sharing <see cref="DirectorSupervisor"/>.
    ///
    /// A path wins when both are given, because a path is unambiguous and a name is a lookup.
    /// </summary>
    /// <returns>The path to start, or an error naming why nothing could be started.</returns>
    public (string? Path, string? Error) ResolveLaunchPath(string? path, string? app)
    {
        if (!string.IsNullOrWhiteSpace(path))
            return (path, null);

        if (string.IsNullOrWhiteSpace(app))
            return (null, "either path or app is required");

        var outcome = Resolve(app);
        return outcome.Status switch
        {
            ResolveStatus.Found => (outcome.App!.Path, null),
            ResolveStatus.Ambiguous => (null,
                $"'{app}' matches {outcome.Candidates!.Count} applications on {Environment.MachineName} " +
                $"({string.Join(", ", outcome.Candidates!)}). Use a more specific name, or pass an exact path."),
            _ => (null, $"no application named '{app}' is installed on {Environment.MachineName}"),
        };
    }

    /// <summary>
    /// Walk every catalogue root and return what is installed. Duplicate paths are collapsed, because the
    /// per-user and machine-wide Start Menu trees routinely carry the same program.
    /// </summary>
    private List<InstalledAppDto> Enumerate(List<string> skipped)
    {
        var found = new List<InstalledAppDto>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, source) in Roots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var app in WalkRoot(root, source, skipped))
            {
                if (seenPaths.Add(app.Path))
                    found.Add(app);
            }
        }

        return found;
    }

    /// <summary>
    /// The catalogue roots for the operating system this launcher is running on, each paired with the source
    /// label reported to the caller.
    /// </summary>
    private IEnumerable<(string Root, string Source)> Roots()
    {
        if (_rootsOverride is not null)
        {
            foreach (var entry in _rootsOverride) yield return entry;
            yield break;
        }

        if (OperatingSystem.IsWindows())
        {
            yield return (Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "start-menu-user");
            yield return (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "start-menu-machine");
            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (OperatingSystem.IsMacOS())
        {
            yield return ("/Applications", "applications");
            yield return ("/System/Applications", "applications-system");
            if (!string.IsNullOrEmpty(home))
                yield return (Path.Combine(home, "Applications"), "applications-user");
            yield break;
        }

        yield return ("/usr/share/applications", "desktop-entries");
        yield return ("/usr/local/share/applications", "desktop-entries-local");
        if (!string.IsNullOrEmpty(home))
            yield return (Path.Combine(home, ".local/share/applications"), "desktop-entries-user");
    }

    /// <summary>
    /// Walk one catalogue root breadth-first, collecting entries.
    ///
    /// The per-directory try-catch here is not defensive padding: on every one of these platforms a
    /// catalogue root reliably contains directories the current user may not read, and that is an ordinary
    /// condition rather than a fault. It is recorded in <paramref name="skipped"/> and reported to the caller,
    /// so an incomplete catalogue announces itself instead of looking like a shorter one.
    /// </summary>
    private static IEnumerable<InstalledAppDto> WalkRoot(string root, string source, List<string> skipped)
    {
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception ex)
            {
                skipped.Add($"{directory}: {ex.Message}");
                continue;
            }

            foreach (var entry in entries)
            {
                if (IsAppBundle(entry))
                {
                    yield return new InstalledAppDto
                    {
                        Name = Path.GetFileNameWithoutExtension(entry),
                        Path = entry,
                        Source = source,
                    };
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    if (depth < MaximumDepth && !IsReparsePoint(entry))
                        queue.Enqueue((entry, depth + 1));
                    continue;
                }

                if (IsAppFile(entry))
                {
                    yield return new InstalledAppDto
                    {
                        Name = Path.GetFileNameWithoutExtension(entry),
                        Path = entry,
                        Source = source,
                    };
                }
            }
        }
    }

    /// <summary>True for a macOS application bundle, which is a directory that must be treated as one item.</summary>
    private static bool IsAppBundle(string path) =>
        OperatingSystem.IsMacOS()
        && path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
        && Directory.Exists(path);

    /// <summary>True for a file that represents a startable application on this operating system.</summary>
    private static bool IsAppFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsMacOS())
            return false;
        return path.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for a symbolic link or junction. These are stepped over so a link pointing at an ancestor cannot
    /// send the walk round the same tree forever.
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
