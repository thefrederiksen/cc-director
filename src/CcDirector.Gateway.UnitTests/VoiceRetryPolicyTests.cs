using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway's own narration retry schedule: a fixed number of automatic tries per turn, a fixed number
/// of minutes apart, after which it stops and the Voice screen offers the Generate button. The owner set
/// the shape on 1 September 2026 from a phone reading "Voice did not arrive after 19m" with nothing to
/// press. These pin the boundaries - when a turn is DUE, and when its schedule is SPENT - with the clock
/// injected so neither is waited for.
/// </summary>
public sealed class VoiceRetryPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 19, 0, 0, DateTimeKind.Utc);

    /// <summary>The turn these attempts are about. Attempts are counted against a TURN, never against a
    /// session, so a new turn is never held back by the previous one's spent schedule.</summary>
    private const string Turn = "turn-abc";

    private static VoiceAttempts After(int count, TimeSpan ago, string turn = Turn) => new(count, Now - ago, turn);

    [Fact]
    public void NothingTriedYet_IsDue()
    {
        // The pre-build path must be unchanged: a turn that has not failed is tried at once.
        Assert.True(VoiceRetryPolicy.IsDue(null, Now));
        Assert.True(VoiceRetryPolicy.IsDue(After(0, TimeSpan.Zero), Now));
    }

    [Fact]
    public void JustAfterAFailedAttempt_IsNotDue()
    {
        // This is the change from the old sweep, which would have tried again on its next 45-second pass.
        Assert.False(VoiceRetryPolicy.IsDue(After(1, TimeSpan.FromSeconds(45)), Now));
        Assert.False(VoiceRetryPolicy.IsDue(After(1, VoiceRetryPolicy.RetryEvery - TimeSpan.FromSeconds(1)), Now));
    }

    [Fact]
    public void OnceTheSpacingHasPassed_IsDueAgain()
    {
        Assert.True(VoiceRetryPolicy.IsDue(After(1, VoiceRetryPolicy.RetryEvery), Now));
        Assert.True(VoiceRetryPolicy.IsDue(After(4, TimeSpan.FromMinutes(10)), Now));
    }

    [Fact]
    public void ASpentSchedule_StopsTheRetrying_ForAsLongAsItIsWorthStopping()
    {
        // The whole point of the maximum: there is a moment after which the Gateway has honestly stopped,
        // and from then on the button is the way forward. The spacing that governs ordinary retries does not
        // re-arm it - only the much longer revalidation below does, and that is a look rather than a try.
        var spent = After(VoiceRetryPolicy.MaxAutomaticAttempts, VoiceRetryPolicy.RetryEvery);
        Assert.True(VoiceRetryPolicy.IsExhausted(spent));
        Assert.False(VoiceRetryPolicy.IsDue(spent, Now));
        Assert.False(VoiceRetryPolicy.IsDue(After(VoiceRetryPolicy.MaxAutomaticAttempts,
            VoiceRetryPolicy.RevalidateSpentAfter - TimeSpan.FromSeconds(1)), Now));
    }

    [Fact]
    public void ASpentSchedule_IsLookedAtAgainEventually_SoANewTurnIsNeverStranded()
    {
        // The stranding this prevents: the only ungated attempt is the one the turn-end edge fires, that edge
        // is sampled and can be missed - or coalesced away while the previous turn's last attempt is still
        // running - and if the sweep were held back for ever, nothing left in the system could discover that
        // the reply had changed. The session would sit silent, offering a button to re-narrate a turn that is
        // no longer the current one.
        //
        // The UNNAMED ask - the sweep, before it has read anything - is the one that is let through.
        Assert.True(VoiceRetryPolicy.IsDue(
            After(VoiceRetryPolicy.MaxAutomaticAttempts, VoiceRetryPolicy.RevalidateSpentAfter), Now));
        Assert.True(VoiceRetryPolicy.RevalidateSpentAfter > VoiceRetryPolicy.RetryEvery,
            "a spent schedule must be looked at far less often than an ordinary retry, or stopping means nothing");
    }

    [Fact]
    public void TheLookIsNotATry_ASpentTurnIsRefusedForeverOnceItIsNamed()
    {
        // FOUND IN REVIEW, and the reason the policy asks two different questions. The revalidation interval
        // used to apply to the named ask as well, so ten minutes after the fifth failure BOTH calls said
        // "due": the sweep looked, the caller read the conversation, found the same turn, asked again by name,
        // was told yes, and called the model. That recorded a sixth attempt, then a seventh, every ten minutes
        // for the life of the session - while the Voice screen said the Gateway had stopped trying and offered
        // a button. A comment in the caller claimed the pass was "a LOOK, not another try"; this is the
        // assertion that makes the claim true rather than aspirational.
        var spent = After(VoiceRetryPolicy.MaxAutomaticAttempts, VoiceRetryPolicy.RevalidateSpentAfter);

        Assert.True(VoiceRetryPolicy.IsDue(spent, Now));                 // unnamed: go and look
        Assert.False(VoiceRetryPolicy.IsDue(spent, Now, Turn));          // named, same turn: do not try again
        Assert.True(VoiceRetryPolicy.IsDue(spent, Now, "another-turn")); // named, different turn: start clean

        // ...and it does not come back an hour later, or a day later, either.
        var ancient = After(VoiceRetryPolicy.MaxAutomaticAttempts, TimeSpan.FromDays(1));
        Assert.False(VoiceRetryPolicy.IsDue(ancient, Now, Turn));
    }

    [Fact]
    public void ADifferentTurn_IsAlwaysDue_HoweverSpentThePreviousTurnsScheduleWas()
    {
        // THE FINDING THIS EXISTS FOR. Resetting the count on an observed Working transition means a quick
        // turn that slips between two samples inherits the previous turn's spent schedule: its narration is
        // never attempted, and the screen offers a button for a reply nothing has tried even once. A
        // different reply is a different turn, whatever the Gateway did or did not observe in between - the
        // same fix, for the same reason, as the bare has-audio guard of issue #1322.
        var spent = After(VoiceRetryPolicy.MaxAutomaticAttempts, TimeSpan.Zero);

        Assert.False(VoiceRetryPolicy.IsDue(spent, Now, Turn));               // the same turn is finished
        Assert.True(VoiceRetryPolicy.IsDue(spent, Now, "a-different-turn"));  // a new turn starts clean
    }

    [Fact]
    public void TheSweep_IsHeldBackByASpentSchedule_BecauseThatIsTheWholePoint()
    {
        // The sweep asks without naming a turn - it has not read anything yet - and a spent schedule MUST
        // stop it there. If an unnamed ask were always due, the Gateway would never stop trying on its own
        // and the screen's "it has stopped, press Generate" would be a lie.
        //
        // What rescues a NEW turn is not this call: it is the turn-end path, which narrates without
        // consulting the schedule at all, and whose attempt is recorded against the new turn and so starts
        // the count again (see WingmanVoiceAutomaticAttemptsTests). The schedule bounds the RETRYING of a
        // turn that has already failed; it never stands between a fresh turn and its first attempt.
        Assert.False(VoiceRetryPolicy.IsDue(After(VoiceRetryPolicy.MaxAutomaticAttempts, TimeSpan.Zero), Now, turnKey: null));
    }

    [Fact]
    public void ExhaustionIsTheMaximum_NotOneShort()
    {
        Assert.False(VoiceRetryPolicy.IsExhausted(VoiceRetryPolicy.MaxAutomaticAttempts - 1));
        Assert.True(VoiceRetryPolicy.IsExhausted(VoiceRetryPolicy.MaxAutomaticAttempts));
        Assert.False(VoiceRetryPolicy.IsExhausted((VoiceAttempts?)null));
    }

    [Fact]
    public void TheScheduleIsTheOwnersShape_AFewTries_MinutesApart()
    {
        // "Three to five attempts, three to five minutes in between" - the owner's words, and a RANGE, so
        // this asserts the range and nothing tighter. Be clear about what that does and does not buy: moving
        // the count from five to four would not fail here, because the owner sanctioned four. Moving it to
        // one, or to fifty, or making the spacing seconds, would - and those are the changes that would stop
        // the screen's sentence from describing the schedule being run.
        //
        // What this test does NOT do (said plainly, because a reviewer read the old wording as a promise that
        // it did): it does not pin the exact shipped numbers, so a change within the sanctioned range passes.
        Assert.InRange(VoiceRetryPolicy.MaxAutomaticAttempts, 3, 5);
        Assert.InRange(VoiceRetryPolicy.RetryEvery, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(5));
    }
}
