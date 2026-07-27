using CcDirector.Core.Backends;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// The supervision facts a session keeps for itself at the activity flip (internal#625 Phase 1):
/// the completed-turn counter (one flip to WaitingForInput == one turn - the same rule
/// TurnReviewLogger writes records by), the waiting anchor, and the accumulated
/// waiting-on-the-user clock. Sessions are driven through ApplyTerminalActivityState exactly as
/// the terminal detector drives production. The clock is injected so accumulation is asserted
/// exactly, never with sleeps.
/// </summary>
public sealed class SessionSupervisionFactsTests
{
    [Fact]
    public void FreshSession_ReportsZeroTurns_NoAnchor_NoIdle()
    {
        using var session = NewSession();

        Assert.Equal(0, session.TurnCount);
        Assert.Null(session.WaitingSince);
        Assert.Equal(0, session.CumulativeIdleSeconds);
    }

    [Fact]
    public void TurnCount_IncrementsOnlyOnTheFlipToWaitingForInput()
    {
        using var session = NewSession();

        session.ApplyTerminalActivityState(ActivityState.Working);
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        Assert.Equal(1, session.TurnCount);

        session.ApplyTerminalActivityState(ActivityState.Working);
        Assert.Equal(1, session.TurnCount);

        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        Assert.Equal(2, session.TurnCount);
    }

    [Fact]
    public void TurnCount_APermissionPromptIsNotATurn()
    {
        using var session = NewSession();

        session.ApplyTerminalActivityState(ActivityState.Working);
        session.ApplyTerminalActivityState(ActivityState.WaitingForPerm);

        // Waiting on a permission answer mid-turn: nothing finished, nothing to count.
        Assert.Equal(0, session.TurnCount);
    }

    [Fact]
    public void TurnCount_ARepeatedSameStateApplicationIsNotASecondTurn()
    {
        using var session = NewSession();

        session.ApplyTerminalActivityState(ActivityState.Working);
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        // Cosmetic repaints re-apply the same state; SetActivityState dedupes, so no double count.
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        Assert.Equal(1, session.TurnCount);
    }

    [Fact]
    public void WaitingSince_AnchorsOnEnteringWaiting_AndClearsOnWorking()
    {
        using var session = NewSession();
        var now = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
        session.SupervisionClock = () => now;

        session.ApplyTerminalActivityState(ActivityState.Working);
        Assert.Null(session.WaitingSince);

        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        Assert.Equal(now, session.WaitingSince);

        now = now.AddSeconds(30);
        session.ApplyTerminalActivityState(ActivityState.Working);
        Assert.Null(session.WaitingSince);
    }

    [Fact]
    public void WaitingSince_SurvivesThePermToInputTransition_OneWaitOneAnchor()
    {
        using var session = NewSession();
        var t0 = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
        var now = t0;
        session.SupervisionClock = () => now;

        session.ApplyTerminalActivityState(ActivityState.Working);
        session.ApplyTerminalActivityState(ActivityState.WaitingForPerm);
        Assert.Equal(t0, session.WaitingSince);

        // Still the user's wait - the anchor must not move, and no idle closes yet.
        now = t0.AddSeconds(20);
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        Assert.Equal(t0, session.WaitingSince);
        Assert.Equal(0, session.CumulativeIdleSeconds);
    }

    [Fact]
    public void CumulativeIdle_SumsClosedWaits_AndExcludesTheOpenOne()
    {
        using var session = NewSession();
        var t0 = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
        var now = t0;
        session.SupervisionClock = () => now;

        // First wait: 60 seconds.
        session.ApplyTerminalActivityState(ActivityState.Working);
        now = t0.AddSeconds(10);
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        now = t0.AddSeconds(70);
        session.ApplyTerminalActivityState(ActivityState.Working);
        Assert.Equal(60, session.CumulativeIdleSeconds);

        // Second wait opens but has not closed: the total must not move yet.
        now = t0.AddSeconds(100);
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        now = t0.AddSeconds(400);
        Assert.Equal(60, session.CumulativeIdleSeconds);

        // It closes: 300 more seconds land in the total.
        session.ApplyTerminalActivityState(ActivityState.Working);
        Assert.Equal(360, session.CumulativeIdleSeconds);
    }

    [Fact]
    public void CumulativeIdle_AnExitWhileWaitingClosesTheStretch()
    {
        using var session = NewSession();
        var t0 = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
        var now = t0;
        session.SupervisionClock = () => now;

        session.ApplyTerminalActivityState(ActivityState.Working);
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        now = t0.AddSeconds(45);
        session.ApplyTerminalActivityState(ActivityState.Exited);

        Assert.Equal(45, session.CumulativeIdleSeconds);
        Assert.Null(session.WaitingSince);
    }

    private static Session NewSession()
        => new(Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null, new StubSessionBackend(), SessionBackendType.ConPty);
}
