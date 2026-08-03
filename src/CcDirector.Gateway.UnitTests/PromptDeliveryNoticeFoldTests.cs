using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Gateway fold that turns "a prompt to this session did not go" into words (issue internal#811).
/// The client is dumb: it renders the returned string verbatim and never decides for itself what a
/// delivery failure means (CLAUDE.md rule 7). These tests pin what the fold will and will not say.
/// </summary>
public sealed class PromptDeliveryNoticeFoldTests
{
    [Fact]
    public void NoFailureEverRecorded_SaysNothing()
    {
        Assert.Null(SessionOrdering.PromptDeliveryNotice(new SessionDto()));
    }

    [Fact]
    public void UnresolvedFailure_NamesTheLossAndCarriesTheReason()
    {
        var s = new SessionDto
        {
            PromptDeliveryUnresolved = true,
            FailedPromptDeliveries = 1,
            LastPromptDeliveryFailureReason = "the composer never echoed the typed text after 2 attempts",
        };

        var notice = SessionOrdering.PromptDeliveryNotice(s);

        Assert.NotNull(notice);
        Assert.Contains("not delivered", notice);
        Assert.Contains("never echoed", notice);
    }

    [Fact]
    public void UnresolvedFailure_WithNoReason_StillSaysTheWordsWereLost()
    {
        // A Director that reports the flag but no reason must not produce a notice that trails off. The
        // headline is the part that matters and it does not depend on the detail.
        var s = new SessionDto { PromptDeliveryUnresolved = true, FailedPromptDeliveries = 1 };

        var notice = SessionOrdering.PromptDeliveryNotice(s);

        Assert.Equal("Your last prompt was not delivered - the agent never received it.", notice);
    }

    [Fact]
    public void ResolvedFailure_SaysNothingEvenThoughTheCountsRemain()
    {
        // THE POINT OF THE UNRESOLVED FLAG. Four failures earlier today, all of them retried successfully:
        // there is nothing for the user to act on, so the alarm is silent. The counts stay on the row for
        // whoever wants to know how sick this session is - they are not the alarm.
        var s = new SessionDto
        {
            PromptDeliveryUnresolved = false,
            FailedPromptDeliveries = 4,
            ComposerEchoMisses = 9,
            LastPromptDeliveryFailureReason = "the composer never echoed the typed text after 2 attempts",
            LastPromptDeliveryFailureAtUtc = DateTime.UtcNow.AddMinutes(-30),
        };

        Assert.Null(SessionOrdering.PromptDeliveryNotice(s));
    }

    [Fact]
    public void EchoMissesAlone_AreNeverAnAlarm()
    {
        // A miss that recovered on the retype cost the user nothing. Shouting about it would train the
        // reader to ignore the badge that DOES mean their words are gone.
        var s = new SessionDto { ComposerEchoMisses = 6, PromptDeliveryUnresolved = false };

        Assert.Null(SessionOrdering.PromptDeliveryNotice(s));
    }

    [Fact]
    public void TheNotice_DoesNotTouchTheColourTheLabelOrTheBucket()
    {
        // The session's colour describes what the AGENT is doing, and the agent is doing exactly what it
        // was doing - it never heard anything. Recolouring it would say something false about the agent in
        // order to say something true about the delivery, so the notice is its own channel.
        var s = new SessionDto
        {
            ActivityState = "Working",
            StatusColor = "blue",
            PromptDeliveryUnresolved = true,
            LastPromptDeliveryFailureReason = "gone",
        };

        Assert.Equal("blue", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Working", SessionOrdering.StateLabel(s));
        Assert.Equal(SessionOrdering.TriageBucket.Active, SessionOrdering.Classify(s));
        Assert.NotNull(SessionOrdering.PromptDeliveryNotice(s));
    }
}
