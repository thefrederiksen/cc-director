using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Supervision;
using Xunit;

namespace CcDirector.Gateway.Tests;

// ============================================================================
// Issue #915: the recovery state machine and the model-fallback verdict reader, both pure - so the whole
// escalation ladder (a two-hour shape in production) is asserted exactly, in milliseconds.
// ============================================================================
public sealed class SupervisorPlannerTests
{
    private static readonly SupervisorSettings Defaults = SupervisorSettings.Defaults;

    [Fact]
    public void ATransientFault_WaitsTheShortDelayOnce_ThenTheLongCadence()
    {
        var first = SupervisorPlanner.Next(SessionFaultClass.TransientTransport, attempt: 1, Defaults);
        var second = SupervisorPlanner.Next(SessionFaultClass.TransientTransport, attempt: 2, Defaults);
        var ninth = SupervisorPlanner.Next(SessionFaultClass.TransientTransport, attempt: 9, Defaults);

        Assert.Equal(SupervisorActionKind.WaitThenContinue, first.Kind);
        Assert.Equal(TimeSpan.FromSeconds(45), first.Delay);
        Assert.Equal(TimeSpan.FromMinutes(15), second.Delay);
        Assert.Equal(TimeSpan.FromMinutes(15), ninth.Delay);
        Assert.Equal(ActivityCauses.TransientTransport, first.Cause);
    }

    [Fact]
    public void OnePastTheCeiling_Escalates()
    {
        // Eight long retries after the short one is nine sends; the tenth attempt is an outage, not a blip.
        var beyond = SupervisorPlanner.Next(SessionFaultClass.TransientTransport, attempt: 10, Defaults);

        Assert.Equal(SupervisorActionKind.Escalate, beyond.Kind);
        Assert.Equal(ActivityCauses.RetryCeiling, beyond.Cause);
    }

    [Fact]
    public void AZeroCeiling_AllowsTheShortRetryAndNothingMore()
    {
        var settings = Defaults with { MaxLongRetries = 0 };

        Assert.Equal(SupervisorActionKind.WaitThenContinue,
            SupervisorPlanner.Next(SessionFaultClass.TransientTransport, 1, settings).Kind);
        Assert.Equal(SupervisorActionKind.Escalate,
            SupervisorPlanner.Next(SessionFaultClass.TransientTransport, 2, settings).Kind);
    }

    [Fact]
    public void RateLimiting_StartsAtTheLongCadence_AndBacksOff_Capped()
    {
        // A throttled provider is not a blip: asking again 45 seconds later earns another refusal.
        Assert.Equal(TimeSpan.FromMinutes(15), SupervisorPlanner.Next(SessionFaultClass.RateLimited, 1, Defaults).Delay);
        Assert.Equal(TimeSpan.FromMinutes(30), SupervisorPlanner.Next(SessionFaultClass.RateLimited, 2, Defaults).Delay);
        Assert.Equal(TimeSpan.FromMinutes(60), SupervisorPlanner.Next(SessionFaultClass.RateLimited, 3, Defaults).Delay);
        // Capped, not unbounded.
        Assert.Equal(SupervisorPlanner.MaxRateLimitedDelay,
            SupervisorPlanner.Next(SessionFaultClass.RateLimited, 9, Defaults).Delay);
    }

    [Theory]
    [InlineData(SessionFaultClass.NonRecoverable, ActivityCauses.NonRecoverable)]
    [InlineData(SessionFaultClass.ContextFull, ActivityCauses.ContextFull)]
    [InlineData(SessionFaultClass.Unclassified, ActivityCauses.UnclassifiedFault)]
    public void TheClassesThatMustReachAHuman_NeverWaitAndNeverSend(SessionFaultClass fault, string expectedCause)
    {
        var action = SupervisorPlanner.Next(fault, attempt: 1, Defaults);

        Assert.Equal(SupervisorActionKind.Escalate, action.Kind);
        Assert.Equal(TimeSpan.Zero, action.Delay);
        Assert.Equal(expectedCause, action.Cause);
    }

    [Fact]
    public void ACleanTurnEnd_IsDoNothing()
        => Assert.Equal(SupervisorActionKind.DoNothing,
            SupervisorPlanner.Next(SessionFaultClass.None, attempt: 1, Defaults).Kind);

    [Fact]
    public void AttemptsAreOneBased_AndAZerothAttemptIsAProgrammingError()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => SupervisorPlanner.Next(SessionFaultClass.TransientTransport, attempt: 0, Defaults));

    [Theory]
    [InlineData(45, "45 seconds")]
    [InlineData(900, "15 minutes")]
    public void DelaysReadAsPlainEnglish(int seconds, string expected)
        => Assert.Equal(expected, SupervisorPlanner.Describe(TimeSpan.FromSeconds(seconds)));

    // ---- the model fallback's verdict reader ---------------------------------------------------------------

    [Theory]
    [InlineData("transient_recoverable", SessionFaultClass.TransientTransport)]
    [InlineData("needs_human", SessionFaultClass.NonRecoverable)]
    [InlineData("healthy_done", SessionFaultClass.None)]
    [InlineData("context_full", SessionFaultClass.ContextFull)]
    public void EachVerdict_MapsToOneClass(string verdict, SessionFaultClass expected)
        => Assert.Equal(expected, SupervisorVerdict.Map(verdict));

    [Theory]
    [InlineData("transient_recoverable")]
    [InlineData("  transient_recoverable ")]
    [InlineData("`transient_recoverable`")]
    [InlineData("Verdict: transient_recoverable.")]
    public void TheVerdictSurvivesTheWrappingAChatModelAdds(string reply)
        => Assert.Equal(SupervisorVerdict.TransientRecoverable, SupervisorVerdict.Parse(reply));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I am not sure what happened here")]
    [InlineData("either transient_recoverable or needs_human")]
    public void AnAbsentUndecidedOrUnreadableAnswer_IsNoVerdict(string? reply)
    {
        Assert.Null(SupervisorVerdict.Parse(reply));
        // ...and no verdict maps to unclassified, which ESCALATES. A model that mumbles never becomes
        // permission to type into somebody's session.
        Assert.Equal(SessionFaultClass.Unclassified, SupervisorVerdict.Map(SupervisorVerdict.Parse(reply)));
    }

    [Fact]
    public void TheQuestionStatesTheClosedAnswerSet_AndCarriesTheScreenTail()
    {
        var prompt = SupervisorVerdict.BuildPrompt(new[] { "line one", "", "API Error: something odd" });

        foreach (var verdict in SupervisorVerdict.All)
            Assert.Contains(verdict, prompt);
        Assert.Contains("API Error: something odd", prompt);
        Assert.Contains("one word only", prompt);
    }

    // ---- the settings bounds ------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(45, true)]
    [InlineData(600, true)]
    [InlineData(601, false)]
    public void TheFirstWaitIsBounded_SoAnOverrideCannotHammerASession(int seconds, bool valid)
        => Assert.Equal(valid, SupervisorSettings.IsValidFirstRetrySeconds(seconds));

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public void TheCadenceIsBounded(int minutes, bool valid)
        => Assert.Equal(valid, SupervisorSettings.IsValidRetryCadenceMinutes(minutes));

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(48, true)]
    [InlineData(49, false)]
    public void TheCeilingIsBounded_SoThereIsNoWayToAskForAnEndlessLoop(int retries, bool valid)
        => Assert.Equal(valid, SupervisorSettings.IsValidMaxLongRetries(retries));
}
