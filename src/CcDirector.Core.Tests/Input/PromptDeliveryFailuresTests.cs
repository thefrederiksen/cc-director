using CcDirector.Core.Input;
using Xunit;

namespace CcDirector.Core.Tests.Input;

/// <summary>
/// The ledger of prompts that did not go (issue internal#811). These tests pin the three things the
/// surfaces depend on: that a failure is COUNTED, that "your words are gone right now" is distinct from
/// "this went wrong earlier and recovered", and that a lucky retry never erases the history.
///
/// The ledger is a process-wide static (one Director, one ledger, read from the session mapper), so every
/// test resets it first and uses its own session id.
/// </summary>
[Collection("PromptDeliveryFailures")]
public sealed class PromptDeliveryFailuresTests
{
    public PromptDeliveryFailuresTests() => PromptDeliveryFailures.ResetForTests();

    [Fact]
    public void Tally_SessionThatNeverFailed_IsAllZeroAndSaysNothing()
    {
        var tally = PromptDeliveryFailures.Tally(Guid.NewGuid());

        Assert.Equal(0, tally.FailedDeliveries);
        Assert.Equal(0, tally.ComposerEchoMisses);
        Assert.Null(tally.LastFailureAtUtc);
        Assert.Null(tally.LastFailureReason);
        Assert.False(tally.Unresolved);
    }

    [Fact]
    public void RecordFailedDelivery_CountsItAndMarksTheSessionUnresolved()
    {
        var session = Guid.NewGuid();

        PromptDeliveryFailures.RecordFailedDelivery(
            session, "Delivery", "the composer never echoed the typed text after 2 attempts", 739);

        var tally = PromptDeliveryFailures.Tally(session);
        Assert.Equal(1, tally.FailedDeliveries);
        Assert.True(tally.Unresolved);
        Assert.NotNull(tally.LastFailureAtUtc);
        Assert.Contains("never echoed", tally.LastFailureReason);
    }

    [Fact]
    public void RecordDeliverySucceeded_ClearsTheAlarmButKeepsTheCount()
    {
        // THE DISTINCTION THE WHOLE FEATURE RESTS ON. "Unresolved" is the alarm - your words are gone right
        // now - and a later prompt landing is the only thing that answers it. The COUNT is the history, and
        // a lucky retry must not be able to erase the fact that this session ate a prompt.
        var session = Guid.NewGuid();
        PromptDeliveryFailures.RecordFailedDelivery(session, "Delivery", "the composer never echoed", 739);

        PromptDeliveryFailures.RecordDeliverySucceeded(session);

        var tally = PromptDeliveryFailures.Tally(session);
        Assert.False(tally.Unresolved);
        Assert.Equal(1, tally.FailedDeliveries);
        Assert.NotNull(tally.LastFailureAtUtc);
    }

    [Fact]
    public void RecordFailedDelivery_AfterARecovery_RaisesTheAlarmAgain()
    {
        var session = Guid.NewGuid();
        PromptDeliveryFailures.RecordFailedDelivery(session, "Delivery", "first loss", 10);
        PromptDeliveryFailures.RecordDeliverySucceeded(session);

        PromptDeliveryFailures.RecordFailedDelivery(session, "UserInput", "second loss", 20);

        var tally = PromptDeliveryFailures.Tally(session);
        Assert.True(tally.Unresolved);
        Assert.Equal(2, tally.FailedDeliveries);
        Assert.Equal("second loss", tally.LastFailureReason);
    }

    [Fact]
    public void RecordDeliverySucceeded_OnASessionThatNeverFailed_ChangesNothing()
    {
        var session = Guid.NewGuid();

        PromptDeliveryFailures.RecordDeliverySucceeded(session);

        Assert.Equal(PromptDeliveryTally.Empty, PromptDeliveryFailures.Tally(session));
    }

    [Fact]
    public void RecordComposerEchoMiss_IsCountedButRaisesNoAlarm()
    {
        // A miss that recovers on the retype costs the user nothing, so it must never light the "your words
        // are gone" alarm. It is still counted, because the misses are the leading indicator of the losses -
        // on 2026-07-15 there were six misses and two of them became losses, and nobody could see either.
        var session = Guid.NewGuid();

        PromptDeliveryFailures.RecordComposerEchoMiss(session, "ClaudeDriver", attempt: 1, textLength: 739);
        PromptDeliveryFailures.RecordComposerEchoMiss(session, "ClaudeDriver", attempt: 2, textLength: 739);

        var tally = PromptDeliveryFailures.Tally(session);
        Assert.Equal(2, tally.ComposerEchoMisses);
        Assert.Equal(0, tally.FailedDeliveries);
        Assert.False(tally.Unresolved);
    }

    [Fact]
    public void EchoMissesAndFailures_AreCountedOnSeparateLanes()
    {
        var session = Guid.NewGuid();

        PromptDeliveryFailures.RecordComposerEchoMiss(session, "ClaudeDriver", 1, 739);
        PromptDeliveryFailures.RecordFailedDelivery(session, "Delivery", "gone", 739);

        var tally = PromptDeliveryFailures.Tally(session);
        Assert.Equal(1, tally.ComposerEchoMisses);
        Assert.Equal(1, tally.FailedDeliveries);
    }

    [Fact]
    public void Ledgers_AreKeptApartPerSession()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        PromptDeliveryFailures.RecordFailedDelivery(a, "Delivery", "gone", 5);

        Assert.True(PromptDeliveryFailures.Tally(a).Unresolved);
        Assert.False(PromptDeliveryFailures.Tally(b).Unresolved);
    }

    [Fact]
    public void EmptySessionId_IsIgnoredRatherThanBecomingAPhantomLedger()
    {
        // The submit routes that carry no session (the driver and backend call sites) pass the default id.
        // Counting those together under one empty guid would invent a "session" nothing can render.
        PromptDeliveryFailures.RecordComposerEchoMiss(Guid.Empty, "CodexDriver", 1, 40);
        PromptDeliveryFailures.RecordFailedDelivery(Guid.Empty, "Agent", "gone", 40);

        Assert.Equal(PromptDeliveryTally.Empty, PromptDeliveryFailures.Tally(Guid.Empty));
        Assert.Empty(PromptDeliveryFailures.Recent());
    }

    [Fact]
    public void Recent_ReturnsTheFleetWideHistoryNewestFirst()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        PromptDeliveryFailures.RecordFailedDelivery(a, "Delivery", "first", 1);
        PromptDeliveryFailures.RecordComposerEchoMiss(b, "PiDriver", 1, 2);
        PromptDeliveryFailures.RecordFailedDelivery(b, "UserInput", "third", 3);

        var recent = PromptDeliveryFailures.Recent();

        Assert.Equal(3, recent.Count);
        Assert.Equal("third", recent[0].Reason);
        Assert.Equal("failed-delivery", recent[0].Kind);
        Assert.Equal("composer-echo-miss", recent[1].Kind);
        Assert.Equal("first", recent[2].Reason);
    }

    [Fact]
    public void Recent_IsCappedSoALoopingFailureCannotGrowWithoutBound()
    {
        var session = Guid.NewGuid();
        for (var i = 0; i < PromptDeliveryFailures.RingCapacity + 25; i++)
            PromptDeliveryFailures.RecordFailedDelivery(session, "Agent", $"loss {i}", 1);

        var recent = PromptDeliveryFailures.Recent();

        Assert.Equal(PromptDeliveryFailures.RingCapacity, recent.Count);
        // Newest survive: the last one recorded is the first one returned.
        Assert.Equal($"loss {PromptDeliveryFailures.RingCapacity + 24}", recent[0].Reason);
        // The per-session COUNT is not capped - it counted every one of them.
        Assert.Equal(PromptDeliveryFailures.RingCapacity + 25, PromptDeliveryFailures.Tally(session).FailedDeliveries);
    }

    [Fact]
    public void Reason_IsOneLineAndLengthCappedSoARowCanRenderIt()
    {
        var session = Guid.NewGuid();
        var sprawling = "line one\r\nline two " + new string('x', PromptDeliveryFailures.MaxReasonChars);

        PromptDeliveryFailures.RecordFailedDelivery(session, "Delivery", sprawling, 1);

        var reason = PromptDeliveryFailures.Tally(session).LastFailureReason!;
        Assert.DoesNotContain('\n', reason);
        Assert.DoesNotContain('\r', reason);
        Assert.True(reason.Length <= PromptDeliveryFailures.MaxReasonChars + 3, $"reason was {reason.Length} chars");
    }

    [Fact]
    public void Reason_ThatIsBlank_StillSaysSomethingRatherThanNothing()
    {
        var session = Guid.NewGuid();

        PromptDeliveryFailures.RecordFailedDelivery(session, "Delivery", "   ", 1);

        Assert.Equal("the send failed without saying why", PromptDeliveryFailures.Tally(session).LastFailureReason);
    }

    [Fact]
    public void Forget_DropsTheSessionsCountersWhenNothingCanRenderThemAnyMore()
    {
        var session = Guid.NewGuid();
        PromptDeliveryFailures.RecordFailedDelivery(session, "Delivery", "gone", 1);

        PromptDeliveryFailures.Forget(session);

        Assert.Equal(PromptDeliveryTally.Empty, PromptDeliveryFailures.Tally(session));
        // The fleet-wide history is NOT forgotten with the session - it is the record of what happened.
        Assert.Single(PromptDeliveryFailures.Recent());
    }
}

/// <summary>
/// The ledger is process-wide, so every test that writes to it runs in one collection rather than in
/// parallel with the others - two tests resetting it under each other would make both flaky.
/// </summary>
[CollectionDefinition("PromptDeliveryFailures", DisableParallelization = true)]
public sealed class PromptDeliveryFailuresCollection { }
