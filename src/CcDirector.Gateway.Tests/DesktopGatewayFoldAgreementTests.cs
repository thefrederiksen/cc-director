using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The regression that started this work: the desktop rail and the phone showed DIFFERENT states for the
/// same session at the same instant - six of thirteen sessions disagreed when measured on the live fleet
/// on 14 July 2026. The desktop hand-rolled three private folds, and two of them had never heard of the
/// hold flag, so a snoozed session rendered a grey strip next to a red "Your Turn" and an hours-long
/// nagging clock, while the phone correctly showed "Snoozed".
///
/// The rows below are the ACTUAL diverging sessions from that measurement. Both screens now call the SAME
/// fold (<see cref="SessionOrdering"/>), so the only honest assertion is that one function yields one
/// answer per session. Anything that re-introduces a second reading fails here.
///
/// Design: docs/architecture/session-state-machine-2026-07-14.html
/// </summary>
public sealed class DesktopGatewayFoldAgreementTests
{
    /// <summary>A snoozed session as the Director reports it: the wingman's raw colour still says "red"
    /// (the session genuinely IS at a turn end), which is exactly what the old rail read.</summary>
    private static SessionDto Snoozed(string activityState) => new()
    {
        SessionId = "s",
        ActivityState = activityState,
        OnHold = true,
        StatusColor = "red",
    };

    [Theory]
    [InlineData("WaitingForInput")] // "Enrichment Facade Eval - Architect", "cc-consult - Perry", ...
    [InlineData("WaitingForPerm")]
    [InlineData("Idle")]
    public void ASnoozedSession_ReadsSnoozed_AndIsNotRed_OnEveryScreen(string activityState)
    {
        var s = Snoozed(activityState);

        // The raw fact the old rail folded on its own, and got wrong:
        Assert.Equal("red", s.StatusColor);

        // The one fold every screen now calls - one answer.
        Assert.Equal("Snoozed", SessionOrdering.StateLabel(s));
        Assert.Equal("grey", SessionOrdering.EffectiveColor(s));
        Assert.Equal(SessionOrdering.TriageBucket.OnHold, SessionOrdering.Classify(s));

        // ...and because the "waiting 10h" nag is gated on the FOLD being red rather than the raw colour,
        // a snoozed session no longer nags. This is the assertion that pins the rail's third fold.
        Assert.NotEqual("red", SessionOrdering.EffectiveColor(s));
    }

    [Fact]
    public void ALiveWorker_IsRecessive_NotRed_OnEveryScreen()
    {
        // The "Tunnel-Only Review - Reviewer 2 (Codex)" row: a Worker parked at a turn end. The Gateway
        // keeps Workers quiet so they surface to their manager rather than the human; the rail painted it
        // red because it cannot see the role. Phase 2b pushes the role down so the rail reaches this same
        // answer - this test pins what that answer must be.
        var codex = new SessionDto
        {
            SessionId = "codex",
            ActivityState = "WaitingForInput",
            StatusColor = "red",
            IsControlled = true,
            ControllerSessionId = "manager",
            SessionRole = SessionRoles.Worker,
        };

        Assert.Equal("supporting", SessionOrdering.EffectiveColor(codex));
        Assert.Equal("Sub-agent", SessionOrdering.StateLabel(codex));
    }

    [Fact]
    public void AWorkingSession_ReadsWorking_OnEveryScreen()
    {
        var s = new SessionDto { SessionId = "s", ActivityState = "Working", StatusColor = "blue" };

        Assert.Equal("Working", SessionOrdering.StateLabel(s));
        Assert.Equal("blue", SessionOrdering.EffectiveColor(s));
    }

    [Fact]
    public void AControlledWorkingSession_IsBlueAndReadsWorking_OnEveryScreen()
    {
        // The "Stable Release - Manager" row (session 107): driven by its Architect and 23 minutes into a
        // real turn, yet the rail painted it slate and labelled it "Sub-agent" - indistinguishable from
        // on-hold or exited, so the owner read a busy session as parked. Owner's ruling, 2026-07-14: if a
        // session is working it is BLUE, no matter what. Nothing outranks working. Who DRIVES a session
        // travels on the rail's role badge; colour says what it is DOING and never who owns it.
        var manager = new SessionDto
        {
            SessionId = "manager-107",
            ActivityState = "Working",
            StatusColor = "blue",
            IsControlled = true,
            ControllerSessionId = "architect",
            SessionRole = SessionRoles.Worker,
        };

        Assert.Equal("blue", SessionOrdering.EffectiveColor(manager));
        Assert.Equal("Working", SessionOrdering.StateLabel(manager));
        Assert.NotEqual(SessionOrdering.TriageBucket.OnHold, SessionOrdering.Classify(manager));
    }

    [Fact]
    public void AHeldSession_CanNeverAlsoReadWorking()
    {
        // The invariant the hold state machine makes unreachable on the Director, asserted here at the
        // presentation layer too: there is no input for which a screen shows a parked session working.
        // (A session the user snoozed WHILE working is DeferredHold - not held - and reads as working,
        // which is correct: it is still working.)
        var held = Snoozed("Working"); // a state the machine cannot produce - belt and braces
        Assert.NotEqual("Working", SessionOrdering.StateLabel(held));
    }
}
