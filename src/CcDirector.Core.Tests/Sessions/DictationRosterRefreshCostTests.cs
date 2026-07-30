using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// Issue #1111: the roster's "receiving a dictation" refresh must cost the SAME whether one session is
/// open or fifty, and it must not cost the PAINT THREAD anything at all.
///
/// The original loop asked each session separately, and each ask re-enumerated the whole dictation-uploads
/// store and re-read every marker in it. So a one-second timer did (sessions x markers) file reads per
/// second on the dispatcher - measured at 2.3ms a tick with one session and 58ms a tick with twenty-seven
/// (see <see cref="ResponsivenessCostMeasurement"/>), every one of those reads re-deriving the same
/// store-wide answer.
///
/// These are pinned as source text rather than behaviour because the defect is not a wrong answer - the old
/// code returned exactly the right one. The defect is WHERE the work happens: inside the loop instead of
/// above it, and on the dispatcher instead of the thread pool. No assertion on the result can tell those
/// apart, so the shape itself is the thing under test.
/// </summary>
public sealed class DictationRosterRefreshCostTests
{
    [Fact]
    public void Roster_refresh_reads_the_marker_store_once_per_tick_not_once_per_session()
    {
        var tick = DictationTickBody();

        // Read ONCE. The store-wide answer is derived a single time...
        Assert.Contains("Session.DictationLockedIds();", tick);

        // ...and each session is then asked against that set, with no disk in the loop body.
        Assert.Contains(
            "foreach (var vm in _sessions) vm.Session.RefreshReceivingDictation(lockedSessionIds);",
            tick);

        // The regression itself: the parameterless overload goes back to disk for ONE session, so calling it
        // per session in a loop is precisely the cost this issue removed. It stays on Session for a caller
        // refreshing a single session, but it must not reappear in the roster's tick.
        Assert.DoesNotContain("vm.Session.RefreshReceivingDictation();", tick);
    }

    [Fact]
    public void Roster_refresh_reads_the_marker_store_off_the_paint_thread()
    {
        var tick = DictationTickBody();

        // The read is handed to the thread pool. Deduplicating it made it cheap; it is still file
        // input/output, and file input/output on the thread that paints is what "the interface is not
        // fresh" actually feels like.
        Assert.Contains("Task.Run(", tick);

        // Only the RESULT comes back to the dispatcher, because the session's change event drives the rail.
        Assert.Contains("Dispatcher.UIThread.Post(", tick);

        // And a slow disk must not build a backlog: a tick is skipped while the previous read is still out,
        // rather than queued behind it, since only the newest answer is worth having.
        Assert.Contains("Interlocked.CompareExchange(ref _dictationLockReadInFlight", tick);
    }

    /// <summary>
    /// The body of the one-second dictation tick, isolated from the rest of the window so these assertions
    /// cannot be satisfied by some unrelated <c>Task.Run</c> elsewhere in a six-thousand-line file. Pinned
    /// from the timer's declaration to its Start call.
    /// </summary>
    private static string DictationTickBody()
    {
        var main = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CcDirector.Avalonia", "MainWindow.axaml.cs"));

        const string open = "_dictationLockTimer = new";
        const string close = "_dictationLockTimer.Start();";

        var start = main.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, "Could not find the dictation lock timer in MainWindow.axaml.cs");

        var end = main.IndexOf(close, start, StringComparison.Ordinal);
        Assert.True(end >= 0, "Could not find the end of the dictation lock timer in MainWindow.axaml.cs");

        return main[start..(end + close.Length)];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from " + AppContext.BaseDirectory);
    }
}
