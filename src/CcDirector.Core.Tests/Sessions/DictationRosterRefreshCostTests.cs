using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// Issue #1111: the roster's "receiving a dictation" refresh must cost the SAME whether one session is
/// open or fifty.
///
/// The original loop asked each session separately, and each ask re-enumerated the whole dictation-uploads
/// store and re-read every marker in it. So a one-second timer did (sessions x markers) file reads per
/// second on the UI thread - hundreds of them on a Director holding two dozen sessions, every one of them
/// re-deriving the same store-wide answer. The fix reads the store ONCE per tick and asks the resulting
/// set per session.
///
/// This is pinned as source text rather than behaviour because the defect is not a wrong answer - the old
/// code returned exactly the right one. The defect is WHERE the read happens: inside the loop instead of
/// above it. No assertion on the result can tell those apart, so the loop itself is the thing under test.
/// The DoesNotContain is the live half: restoring the per-session call reddens this test.
/// </summary>
public sealed class DictationRosterRefreshCostTests
{
    [Fact]
    public void Roster_refresh_reads_the_marker_store_once_per_tick_not_once_per_session()
    {
        var main = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CcDirector.Avalonia", "MainWindow.axaml.cs"));

        // Read once, ABOVE the loop.
        Assert.Contains("var lockedSessionIds = Session.DictationLockedIds();", main);

        // Then ask the set per session - no disk in the loop body.
        Assert.Contains(
            "foreach (var vm in _sessions) vm.Session.RefreshReceivingDictation(lockedSessionIds);",
            main);

        // The regression itself: the parameterless overload goes back to disk for ONE session, so calling it
        // per session in a loop is precisely the cost this issue removed. It stays on Session for a caller
        // refreshing a single session, but it must not reappear in the roster's tick.
        Assert.DoesNotContain("vm.Session.RefreshReceivingDictation();", main);
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
