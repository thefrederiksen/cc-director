using System;
using System.Collections.Generic;
using CcDirector.Core.Home;
using CcDirector.Core.Setup;
using Xunit;

namespace CcDirector.Core.Tests.Home;

/// <summary>
/// The Sessions row exists because this page once printed "All systems go - 8 of 8 tools passing" while
/// the Director's own log already held "cc-devthrottle ... FAILED to reach ...: missing or invalid token",
/// and a red banner on the Tools tab said so on the same screen. Two verdicts about one machine, in
/// opposite terms.
///
/// These tests pin the part that made that possible: the tools rows can be entirely green and correct
/// while sessions are dead, so a green tools row must never be able to carry the page to "all ready".
/// </summary>
public class HomeStatusSessionsRowTests
{
    private static readonly IReadOnlyList<AgentCliFact> HealthyClis =
        new[] { new AgentCliFact("Claude Code", Found: true, Version: "2.1.0") };

    /// <summary>Every non-session input healthy, so only the Sessions row can move the verdict.</summary>
    private static HomeStatus BuildWith(FleetToolCheck? reachability)
        => HomeStatusBuilder.Build(
            HealthyClis, toolsBuilt: 8, toolsTotal: 8, brokenTools: Array.Empty<string>(),
            toolHealth: null, basePythonBroken: false, toolsSetupInProgress: false,
            sessionReachability: reachability);

    private static FleetToolCheck Fault(string resolved, string expected) =>
        new(FleetToolVerdict.CannotReachDirector, resolved, expected, "Error: missing or invalid token");

    private static HomeCheck? SessionsRow(HomeStatus status)
    {
        foreach (var check in status.Checks)
            if (check.Title == HomeStatusBuilder.SessionsRowTitle) return check;
        return null;
    }

    [Fact]
    public void NoVerdictYet_AddsNoRowAtAll()
    {
        // Unjudged is not a pass and not a failure. Inventing either is the bug.
        var status = BuildWith(null);

        Assert.Null(SessionsRow(status));
    }

    [Fact]
    public void ToolsAllPassingButSessionsCannotReach_ThePageIsNotAllReady()
    {
        // The exact screenshot: healthy tools, healthy agent, sessions dead.
        var status = BuildWith(Fault(
            @"C:\Users\x\AppData\Local\cc-director\bin\cc-devthrottle.CMD",
            @"C:\Users\x\AppData\Local\cc-director\instances\slot-5\bin"));

        Assert.False(status.AllReady);
        var row = SessionsRow(status);
        Assert.NotNull(row);
        Assert.Equal(HomeCheckLevel.Bad, row!.Level);
    }

    [Fact]
    public void DifferentInstall_TheDetailNamesTheRealCauseNotAGenericFailure()
    {
        var status = BuildWith(Fault(
            @"C:\Users\x\AppData\Local\cc-director\bin\cc-devthrottle.CMD",
            @"C:\Users\x\AppData\Local\cc-director\instances\slot-5\bin"));

        // A user who has just been told "DevThrottle is down" has to be able to read this row and know
        // that neither the product nor the network is the problem.
        var detail = SessionsRow(status)!.Detail;
        Assert.Contains("another install", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SameInstallStillRefused_ReportsWhatWasSeenRatherThanBlamingTheInstall()
    {
        // Resolved from our OWN bin and still refused: repointing PATH would not repair this, so the row
        // must not imply it would.
        var binDir = @"C:\Users\x\AppData\Local\cc-director\instances\slot-5\bin";
        var status = BuildWith(Fault(binDir + @"\cc-devthrottle.CMD", binDir));

        var detail = SessionsRow(status)!.Detail;
        Assert.DoesNotContain("another install", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing or invalid token", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NotOnPathAtAll_IsAFailureWithItsOwnReason()
    {
        var status = BuildWith(new FleetToolCheck(
            FleetToolVerdict.NotFound, null, @"C:\x\bin", "Nothing named cc-devthrottle is on this machine's PATH."));

        var row = SessionsRow(status);
        Assert.Equal(HomeCheckLevel.Bad, row!.Level);
        Assert.False(status.AllReady);
    }

    [Fact]
    public void Working_IsGreenAndLetsThePageBeAllReady()
    {
        var binDir = @"C:\Users\x\AppData\Local\cc-director\instances\default\bin";
        var status = BuildWith(new FleetToolCheck(
            FleetToolVerdict.Working, binDir + @"\cc-devthrottle.CMD", binDir, "reached this Director"));

        Assert.Equal(HomeCheckLevel.Ok, SessionsRow(status)!.Level);
        Assert.True(status.AllReady);
    }

    [Fact]
    public void EveryFailingVerdict_OffersARouteToTheRepair()
    {
        // A row that states a fault and offers nowhere to go reads as broken.
        foreach (var check in new[]
                 {
                     Fault(@"C:\a\bin\cc-devthrottle.CMD", @"C:\b\bin"),
                     new FleetToolCheck(FleetToolVerdict.NotFound, null, @"C:\b\bin", "not on PATH"),
                 })
        {
            var row = SessionsRow(BuildWith(check));
            Assert.Equal(HomeCheckAction.OpenTools, row!.Action);
        }
    }
}
