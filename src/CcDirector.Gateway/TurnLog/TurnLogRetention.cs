using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.TurnLog;

/// <summary>
/// How long captured terminals stay on the Gateway.
///
/// WHY THIS EXISTS AT ALL. The corpus is written here and pulled down daily into the repository that keeps
/// it. Without a sweep, the copy on the Gateway is simply permanent - every screen, every scrollback and
/// both sides of every conversation, for every account capture was ever switched on for, sitting on a
/// shared file system behind a management endpoint. A security review named exactly that: the interesting
/// question about captured content is not only who can reach it, but how long it is there to be reached.
/// A retention window turns an unbounded exposure into a bounded one, and it costs nothing, because
/// anything worth keeping has already been pulled.
///
/// IT DELETES BY DAY, WHOLE DIRECTORIES AT A TIME, and only ones whose name is a date it can parse. A
/// sweep that guesses what a directory is, or that deletes the newest thing it finds, is how a retention
/// job becomes an outage. Anything it does not understand is left alone and named in the log rather than
/// removed - the failure mode of this class must be "kept too long", never "deleted something we needed".
///
/// THE WINDOW IS DELIBERATELY LONGER THAN THE PULL CADENCE. The pull runs daily; two weeks means a pull
/// can fail, and be noticed and fixed, without the records it was going to collect being deleted underneath
/// it. Shortening this to the pull cadence would make one missed job a permanent hole.
/// </summary>
public sealed class TurnLogRetention
{
    /// <summary>How long a day's records stay on the Gateway. Fourteen days: long enough that a failed
    /// daily pull can be noticed and fixed before anything is lost, short enough that the copy sitting on
    /// the Gateway is a working buffer rather than an archive.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(14);

    private readonly string _root;
    private readonly Func<DateTime> _nowUtc;

    public TurnLogRetention(string root, Func<DateTime>? nowUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Delete every day older than the window. Answers how many day-directories were removed.
    ///
    /// Never throws: a retention sweep that takes the Gateway down with it is worse than one that runs
    /// late. Every refusal and every failure is logged, because a sweep that silently does nothing looks
    /// exactly like a sweep that had nothing to do.
    /// </summary>
    public int Sweep()
    {
        if (!Directory.Exists(_root)) return 0;

        var cutoff = DateOnly.FromDateTime(_nowUtc().Date.Add(-KeepFor));
        var removed = 0;

        foreach (var dayDir in SafeDirectories(_root))
        {
            var name = Path.GetFileName(dayDir);

            // ONLY A DIRECTORY WHOSE NAME IS A DATE. Anything else is something this sweep does not
            // understand, and it is left where it is.
            if (!DateOnly.TryParseExact(name, "yyyy-MM-dd", out var day))
            {
                FileLog.Write($"[TurnLogRetention] leaving {name}: not a day directory, so this sweep will not judge it");
                continue;
            }

            if (day >= cutoff) continue;

            try
            {
                Directory.Delete(dayDir, recursive: true);
                removed++;
                FileLog.Write($"[TurnLogRetention] removed {name} (older than {KeepFor.TotalDays:F0} days)");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TurnLogRetention] could NOT remove {name}: {ex.Message} - it stays, and this is not fatal");
            }
        }

        return removed;
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnLogRetention] could not list {root}: {ex.Message}");
            return Array.Empty<string>();
        }
    }
}
