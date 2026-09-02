using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Phase 3b of the turn-push mission: waiting for the answer to a SPOKEN question.
///
/// This used to poll the transcript down the tunnel every 750 milliseconds for up to three minutes - one
/// command per poll, each making the owning Director open and parse the whole transcript file on the user's
/// disk, all so the Gateway could notice a reply it is now simply handed. It watches the stored conversation
/// instead.
///
/// The giving-up signal changed with it, which is the part worth pinning. The old loop counted forty
/// consecutive FAILED READS to guess the Director had gone - a proxy for something it could not see, built
/// on a failure mode that no longer exists. It now asks the question it actually means: can a reply still
/// arrive at all? Every test below is written to FAIL if that logic regresses, not merely to pass.
/// </summary>
public sealed class WaitForSpokenReplyTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static List<TurnWidgetDto> Convo(params (string Kind, string Content)[] widgets) =>
        widgets.Select(w => new TurnWidgetDto { Kind = w.Kind, Content = w.Content }).ToList();

    private static Task<string?> Wait(Func<IReadOnlyList<TurnWidgetDto>?> read, Func<Task<bool>> reachable, int? before = 1,
        TimeSpan? timeout = null)
        => GatewayWingmanVoiceEndpoint.WaitForReplyAsync(read, reachable, "sid-1", before, CancellationToken.None,
            timeout: timeout ?? Timeout, pollEvery: Poll, settleFor: Settle);

    [Fact]
    public async Task AReplyThatArrivesAndSettles_IsTheAnswer()
    {
        var conversation = Convo(("UserMessage", "the question"));
        var reply = await Wait(() =>
        {
            if (conversation.Count == 1) conversation.Add(new TurnWidgetDto { Kind = "Text", Content = "the answer" });
            return conversation;
        }, () => Task.FromResult(true));

        Assert.Equal("the answer", reply);
    }

    [Fact]
    public async Task AConversationStillGrowing_IsNotAnsweredUntilItStops()
    {
        // The agent is still writing. Answering the first thing that appears would read half a turn aloud.
        var conversation = Convo(("UserMessage", "the question"));
        var grows = 0;
        var reply = await Wait(() =>
        {
            if (grows++ < 5) conversation.Add(new TurnWidgetDto { Kind = "Text", Content = "part " + grows });
            return conversation;
        }, () => Task.FromResult(true));

        Assert.Equal("part 5", reply);   // the last one, after it stopped growing - never "part 1"
    }

    [Fact]
    public async Task NothingStoredForAWhile_IsNotAFailure_HoweverLongItLasts()
    {
        // The old loop gave up after forty consecutive unreadable polls. A store read cannot fail - it just
        // has nothing yet - so there is nothing to count, and a session whose push is slow must not be
        // abandoned. Deliberately more than forty polls, so the old heuristic would have failed this test.
        var polls = 0;
        var reachabilityChecks = 0;
        var reply = await Wait(() =>
        {
            polls++;
            return polls <= 60 ? null : Convo(("UserMessage", "the question"), ("Text", "arrived very late"));
        }, () => { reachabilityChecks++; return Task.FromResult(true); });

        Assert.True(polls > 60, $"the wait must have kept polling past the old forty-failure limit, saw {polls}");
        Assert.True(reachabilityChecks > 0, "and it should have been asking whether a reply could still arrive");
        Assert.Equal("arrived very late", reply);
    }

    [Fact]
    public async Task WhenTheOwningComputerGoesAway_TheWaitEndsEarly_RatherThanSittingOutTheTimeout()
    {
        // No reply is coming, and ten seconds of silence tells the person nothing. Asserted on the number of
        // checks rather than only on elapsed time, so this cannot pass by being slow.
        var checks = 0;
        var reply = await Wait(() => Convo(("UserMessage", "the question")), () => { checks++; return Task.FromResult(false); });

        Assert.Null(reply);
        Assert.Equal(2, checks);   // one negative is a blip; the second consecutive one ends it
    }

    [Fact]
    public async Task OneBlipOfUnreachability_DoesNotEndTheWait()
    {
        // A reconnect blip must not abandon a session that is still working on the answer.
        var checks = 0;
        var polls = 0;
        var reply = await Wait(() =>
        {
            polls++;
            return polls < 40 ? Convo(("UserMessage", "the question"))
                              : Convo(("UserMessage", "the question"), ("Text", "worth waiting for"));
        }, () => { checks++; return Task.FromResult(checks != 1); });   // the first check says unreachable

        Assert.Equal("worth waiting for", reply);
    }

    [Fact]
    public async Task AReplyStoredAsTheComputerGoesAway_IsStillAnswered()
    {
        // The finished turn can land in the moment between the read and the reachability answer. Discarding
        // it would lose an answer the person is waiting to hear, for a computer whose job is already done.
        var stored = false;
        var reply = await Wait(() => stored
                ? Convo(("UserMessage", "the question"), ("Text", "landed at the last moment"))
                : Convo(("UserMessage", "the question")),
            () => { stored = true; return Task.FromResult(false); });

        Assert.Equal("landed at the last moment", reply);
    }

    [Fact]
    public async Task AReplyAlreadyInHand_NeverSpendsAReachabilityCheck()
    {
        // While an answer is settling, whether the machine is reachable is beside the point - the words are
        // already here. Settling deliberately outlasts the ten-poll check interval, so a regression that
        // checked anyway would be caught.
        var checks = 0;
        var conversation = Convo(("UserMessage", "the question"), ("Text", "the answer"));

        var reply = await GatewayWingmanVoiceEndpoint.WaitForReplyAsync(
            () => conversation, () => { checks++; return Task.FromResult(false); },
            "sid-1", 1, CancellationToken.None,
            timeout: Timeout, pollEvery: Poll, settleFor: TimeSpan.FromMilliseconds(200));

        Assert.Equal("the answer", reply);
        Assert.Equal(0, checks);
    }

    [Fact]
    public async Task AFailingReachabilityCheck_IsNotEvidenceTheComputerIsGone()
    {
        // The check throwing is the absence of evidence, not evidence of absence - and it runs inside a
        // request, so an escaping exception would answer a spoken question with a server error.
        var polls = 0;
        var reply = await Wait(() =>
        {
            polls++;
            return polls < 40 ? Convo(("UserMessage", "the question"))
                              : Convo(("UserMessage", "the question"), ("Text", "still got there"));
        }, () => throw new InvalidOperationException("the reachability lookup blew up"));

        Assert.Equal("still got there", reply);
    }

    [Fact]
    public async Task OnlyRepliesAFTERTheQuestion_Count()
    {
        // The snapshot taken before sending is what "new" means. Without it the wait would answer instantly
        // with whatever the agent said last time, and the person would hear a reply to their previous
        // question (issue #366). The old reply sits exactly ON the boundary, so an off-by-one would return it.
        var conversation = Convo(("Text", "an answer from before"));
        var polls = 0;
        var reply = await Wait(() =>
        {
            if (polls++ == 3) conversation.Add(new TurnWidgetDto { Kind = "Text", Content = "the new answer" });
            return conversation;
        }, () => Task.FromResult(true), before: 1);

        Assert.Equal("the new answer", reply);
    }

    [Fact]
    public async Task WithNoBaseline_TheFirstConversationSeenBecomesIt_SoAnOldReplyIsNeverReadAloud()
    {
        // Nothing was stored when the question was sent, and the whole history - including an OLD agent
        // reply - arrives a moment later. Treating "nothing stored" as a baseline of zero would hand that old
        // reply back as the answer to the question just asked: a confident answer to a question the person
        // did not ask, which is issue #366 returning by a new route.
        var arrived = false;
        var answered = false;
        var reply = await Wait(() =>
        {
            if (!arrived) { arrived = true; return null; }
            if (!answered)
            {
                answered = true;
                return Convo(("Text", "an answer from LAST time"), ("UserMessage", "the new question"));
            }
            return Convo(("Text", "an answer from LAST time"), ("UserMessage", "the new question"), ("Text", "the genuinely new answer"));
        }, () => Task.FromResult(true), before: null);

        Assert.Equal("the genuinely new answer", reply);
    }

    [Fact]
    public async Task WithNoBaseline_AndNothingNewEverArriving_NothingIsInvented()
    {
        // The other half of the same rule: if only the old conversation ever shows up, the honest answer is
        // no answer. Hearing nothing is recoverable; hearing the wrong answer confidently is not.
        var reply = await Wait(() => Convo(("Text", "an answer from LAST time")),
            () => Task.FromResult(true), before: null, timeout: TimeSpan.FromMilliseconds(300));

        Assert.Null(reply);
    }
}
