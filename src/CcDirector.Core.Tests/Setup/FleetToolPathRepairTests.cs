using System;
using System.IO;
using CcDirector.Core.Setup;
using Xunit;

namespace CcDirector.Core.Tests.Setup;

/// <summary>
/// Covers the pure PATH rewriting. The registry write itself is not exercised here - a test that
/// edited the developer's real user PATH would be a worse bug than the one being fixed - so these
/// pin the ordering rules that decide what would be written.
/// </summary>
public class FleetToolPathRepairTests
{
    private static string P(params string[] entries) => string.Join(Path.PathSeparator, entries);

    [Fact]
    public void MoveToFront_EntryNotPresent_IsPrepended()
    {
        var result = FleetToolPathRepair.MoveToFront(P(@"C:\windows", @"C:\tools"), @"C:\mine\bin");

        Assert.Equal(P(@"C:\mine\bin", @"C:\windows", @"C:\tools"), result);
    }

    [Fact]
    public void MoveToFront_EntryBehindAStaleInstall_OvertakesIt()
    {
        // The machine this was written for: the old install's bin wins because it comes first.
        var before = P(@"C:\cc-director\bin", @"C:\windows", @"C:\cc-director\instances\default\bin");

        var result = FleetToolPathRepair.MoveToFront(before, @"C:\cc-director\instances\default\bin");

        Assert.Equal(
            P(@"C:\cc-director\instances\default\bin", @"C:\cc-director\bin", @"C:\windows"),
            result);
    }

    [Fact]
    public void MoveToFront_RunTwice_DoesNotAccumulateDuplicates()
    {
        var once = FleetToolPathRepair.MoveToFront(P(@"C:\windows"), @"C:\mine\bin");
        var twice = FleetToolPathRepair.MoveToFront(once, @"C:\mine\bin");

        Assert.Equal(once, twice);
    }

    [Fact]
    public void MoveToFront_LeavesEveryOtherEntryInItsOriginalOrder()
    {
        // The repair is a reordering of one entry, not a rewrite of the user's PATH. Anything else
        // moving is collateral damage on shared machine state.
        var result = FleetToolPathRepair.MoveToFront(P(@"C:\a", @"C:\b", @"C:\c"), @"C:\mine");

        Assert.Equal(P(@"C:\mine", @"C:\a", @"C:\b", @"C:\c"), result);
    }

    [Fact]
    public void MoveToFront_UnexpandedVariablesAreCarriedThroughUntouched()
    {
        // %USERPROFILE% must still be %USERPROFILE% afterwards. Expanding it here would be how a
        // repair silently destroys a user's PATH.
        var result = FleetToolPathRepair.MoveToFront(P(@"%USERPROFILE%\bin", @"C:\windows"), @"C:\mine");

        Assert.Contains(@"%USERPROFILE%\bin", result, StringComparison.Ordinal);
    }

    [Fact]
    public void MoveToFront_IgnoresTrailingSeparatorAndCaseWhenMatching()
    {
        var before = P(@"C:\Mine\Bin\", @"C:\windows");

        var result = FleetToolPathRepair.MoveToFront(before, @"C:\mine\bin");

        Assert.Equal(P(@"C:\mine\bin", @"C:\windows"), result);
    }

    [Fact]
    public void MoveToFront_DropsEmptySegments()
    {
        var result = FleetToolPathRepair.MoveToFront(P(@"C:\a", "", @"C:\b"), @"C:\mine");

        Assert.Equal(P(@"C:\mine", @"C:\a", @"C:\b"), result);
    }

    [Fact]
    public void PutFirstOnPath_DirectoryThatDoesNotExist_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => FleetToolPathRepair.PutFirstOnPath(Path.Combine(Path.GetTempPath(), "no-such-dir-x9")));
    }

    [Fact]
    public void PutFirstOnPath_EmptyDirectory_Throws()
    {
        Assert.Throws<ArgumentException>(() => FleetToolPathRepair.PutFirstOnPath("  "));
    }
}
