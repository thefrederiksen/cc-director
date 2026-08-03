using System.Net;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Push;
using CcDirector.Gateway.Tests.Data;
using Lib.Net.Http.WebPush;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The needs-you notifier decides WHEN to push the app-icon dot count and prunes dead subscriptions.
/// These tests drive it with a fake sender and a scripted count, so no real push service is touched.
/// </summary>
public sealed class WebPushNeedsYouNotifierTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    // A subscription store over the isolated on-disk database (two NewStore() calls share the same DB, so a
    // second store reloads the first's rows).
    private PushSubscriptionStore NewStore()
        => new(_h.Open(), _h.LegacyPath("push-subscriptions-" + Guid.NewGuid().ToString("N") + ".json"));

    // ---- Pure decision logic -------------------------------------------------------------------

    [Fact]
    public void Decide_RisingEdge_PushesTheCountAndShowsTheDot()
    {
        var (push, count, next) = WebPushNeedsYouNotifier.Decide(3, WebPushNeedsYouNotifier.DotState.Initial);

        Assert.True(push);
        Assert.Equal(3, count);
        Assert.True(next.DotShowing);
    }

    [Fact]
    public void Decide_StartupWithNothingWaiting_DoesNotPush()
    {
        var (push, _, next) = WebPushNeedsYouNotifier.Decide(0, WebPushNeedsYouNotifier.DotState.Initial);

        Assert.False(push);
        Assert.False(next.DotShowing);
    }

    [Fact]
    public void Decide_CountWobbleWhileDotShowing_DoesNotPush()
    {
        // The dot is already up (a rise happened). A change to a different non-zero count must NOT push:
        // the Android dot looks identical, so re-pushing would only re-ping the phone.
        var afterRise = WebPushNeedsYouNotifier.Decide(2, WebPushNeedsYouNotifier.DotState.Initial).next;

        var (push, _, next) = WebPushNeedsYouNotifier.Decide(5, afterRise);

        Assert.False(push);
        Assert.True(next.DotShowing);
    }

    [Fact]
    public void Decide_FallingEdge_ClearsOnlyAfterTheConfirmationWindow()
    {
        var showing = WebPushNeedsYouNotifier.Decide(1, WebPushNeedsYouNotifier.DotState.Initial).next;

        // First zero poll: debounce, no push yet.
        var first = WebPushNeedsYouNotifier.Decide(0, showing);
        Assert.False(first.push);
        Assert.True(first.next.DotShowing);

        // Second consecutive zero poll: the count has settled -> push ONE clear.
        var second = WebPushNeedsYouNotifier.Decide(0, first.next);
        Assert.True(second.push);
        Assert.Equal(0, second.count);
        Assert.False(second.next.DotShowing);

        // Still zero afterwards: no repeated zero push.
        var third = WebPushNeedsYouNotifier.Decide(0, second.next);
        Assert.False(third.push);
    }

    [Fact]
    public void Decide_DotClearedByPhone_ReappearsOnHeartbeat()
    {
        // The Gateway cannot see the phone clear the dot (a swipe-away, or the app clearing it on
        // foreground). While sessions keep needing you, the heartbeat must re-assert the dot so it
        // comes back - quiet between heartbeats, one push when the window elapses.
        var r = WebPushNeedsYouNotifier.Decide(3, WebPushNeedsYouNotifier.DotState.Initial); // rising edge
        Assert.True(r.push);

        for (var i = 0; i < WebPushNeedsYouNotifier.HeartbeatPolls - 1; i++)
        {
            r = WebPushNeedsYouNotifier.Decide(3, r.next);
            Assert.False(r.push); // quiet between heartbeats
        }

        r = WebPushNeedsYouNotifier.Decide(3, r.next);
        Assert.True(r.push);      // heartbeat re-asserts the dot
        Assert.Equal(3, r.count);
    }

    [Fact]
    public void Decide_ZeroFlickerBeforeConfirmation_EmitsNoPushAtAll()
    {
        // A session finishes (count hits zero) a moment before another goes red. Because the clear is
        // debounced and a re-rise while the dot is up does not push, the flicker produces zero pushes.
        var showing = WebPushNeedsYouNotifier.Decide(1, WebPushNeedsYouNotifier.DotState.Initial).next;

        var dropped = WebPushNeedsYouNotifier.Decide(0, showing);   // first zero -> debounce
        Assert.False(dropped.push);

        var rose = WebPushNeedsYouNotifier.Decide(1, dropped.next); // rose again before confirmation
        Assert.False(rose.push);
        Assert.True(rose.next.DotShowing);
        Assert.Equal(0, rose.next.ZeroStreak);                      // streak reset - no pending clear
    }

    // ---- snooze-expiry announcements (Snooze Length mission) -----------------------------------

    [Fact]
    public void Decide_NewlyExpiredWhileDotUp_AnnouncesOnceWithSnoozeEnded()
    {
        // The dot is already up for other needs-you work; a session then RETURNS from an expired snooze.
        // That is genuinely new, actionable news, so it must push once with the snooze-ended marker even
        // though the count already had a dot.
        var up = WebPushNeedsYouNotifier.Decide(2, WebPushNeedsYouNotifier.DotState.Initial).next;

        var r = WebPushNeedsYouNotifier.Decide(3, new[] { "s1" }, up);

        Assert.True(r.push);
        Assert.True(r.snoozeEnded);
        Assert.Equal(3, r.count);
        Assert.Contains("s1", r.next.Announced);
    }

    [Fact]
    public void Decide_LingeringExpired_NeverBuzzesAgain()
    {
        // A dead-Director expired snooze lingers in "needs you" forever. It must be announced exactly once;
        // every later poll (including the heartbeat re-assert) must NOT be a snooze-ended announcement.
        var r = WebPushNeedsYouNotifier.Decide(1, new[] { "s1" }, WebPushNeedsYouNotifier.DotState.Initial);
        Assert.True(r.push);
        Assert.True(r.snoozeEnded); // announced once

        for (var i = 0; i < WebPushNeedsYouNotifier.HeartbeatPolls + 2; i++)
        {
            var next = WebPushNeedsYouNotifier.Decide(1, new[] { "s1" }, r.next);
            Assert.False(next.snoozeEnded); // a heartbeat may push, but NEVER as a snooze-ended buzz
            r = next;
        }
    }

    [Fact]
    public void Decide_NewlyExpiredFromScratch_AnnouncesAndShowsTheDot()
    {
        var r = WebPushNeedsYouNotifier.Decide(1, new[] { "s1" }, WebPushNeedsYouNotifier.DotState.Initial);

        Assert.True(r.push);
        Assert.True(r.snoozeEnded);
        Assert.True(r.next.DotShowing);
    }

    [Fact]
    public void Decide_ReSnoozeThenReExpire_AnnouncesAgain()
    {
        // Announce s1, then the user re-snoozes it (it leaves the expired set - pruned from Announced),
        // then it expires again -> it must announce afresh (a new dead-man's-switch event).
        var r1 = WebPushNeedsYouNotifier.Decide(1, new[] { "s1" }, WebPushNeedsYouNotifier.DotState.Initial);
        Assert.True(r1.snoozeEnded);

        var reSnoozed = WebPushNeedsYouNotifier.Decide(1, Array.Empty<string>(), r1.next); // s1 no longer expired
        Assert.DoesNotContain("s1", reSnoozed.next.Announced);

        var reExpired = WebPushNeedsYouNotifier.Decide(1, new[] { "s1" }, reSnoozed.next);
        Assert.True(reExpired.push);
        Assert.True(reExpired.snoozeEnded);
    }

    [Fact]
    public async Task RunOnce_NewlyExpiredSnooze_AnnouncesOnceThenGoesQuiet()
    {
        var store = NewStore();
        store.Add("https://push.example/aaa", "p", "a");
        var sender = new FakeSender();
        var count = 1;
        var expired = new List<string> { "s1" };
        var notifier = new WebPushNeedsYouNotifier(
            store,
            _ => Task.FromResult(new WebPushNeedsYouNotifier.NeedsYouSnapshot(count, expired.ToArray())),
            sender);

        await notifier.RunOnceAsync(CancellationToken.None); // s1 newly expired -> announce
        await notifier.RunOnceAsync(CancellationToken.None); // still lingering -> quiet (no heartbeat yet)

        Assert.Single(sender.Sent);
        Assert.Contains("\"snoozeEnded\":true", sender.Sent[0].payload);
        Assert.Contains("\"count\":1", sender.Sent[0].payload);
    }

    private static WebPushNeedsYouNotifier.NeedsYouSnapshot Snap(int count, params string[] expired) =>
        new(count, expired);

    [Fact]
    public void CountNeedsYou_CountsEffectiveRedNotParked()
    {
        // Issue #1177 (Phase 2): the fold derives the base color from the raw ActivityState, not the
        // cooked StatusColor. These inputs previously set only StatusColor; supply the raw ActivityState
        // the color implies (inverse of ColorFromActivityState). Expected count is unchanged.
        var sessions = new[]
        {
            new SessionDto { StatusColor = "red", ActivityState = "WaitingForInput" },                 // needs you
            new SessionDto { StatusColor = "red", ActivityState = "WaitingForInput" },                 // needs you
            new SessionDto { StatusColor = "red", ActivityState = "WaitingForInput", OnHold = true },  // parked -> not needs you
            new SessionDto { StatusColor = "blue", ActivityState = "Working" },                        // working -> not needs you
        };

        Assert.Equal(2, WebPushNeedsYouNotifier.CountNeedsYou(sessions));
    }

    // ---- Fan-out + pruning behavior ------------------------------------------------------------

    [Fact]
    public async Task RunOnce_NoSubscribers_DoesNotEvenReadTheRoster()
    {
        var store = NewStore();
        var reads = 0;
        var notifier = new WebPushNeedsYouNotifier(store, _ => { reads++; return Task.FromResult(Snap(5)); }, new FakeSender());

        await notifier.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task RunOnce_FirstNonZeroCount_PushesToEverySubscription()
    {
        var store = NewStore();
        store.Add("https://push.example/aaa", "p", "a");
        store.Add("https://push.example/bbb", "p", "a");
        var sender = new FakeSender();
        var notifier = new WebPushNeedsYouNotifier(store, _ => Task.FromResult(Snap(2)), sender);

        await notifier.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, sender.Sent.Count);
        Assert.All(sender.Sent, s => Assert.Contains("\"count\":2", s.payload));
    }

    [Fact]
    public async Task RunOnce_UnchangedCount_DoesNotPushTwice()
    {
        var store = NewStore();
        store.Add("https://push.example/aaa", "p", "a");
        var sender = new FakeSender();
        var notifier = new WebPushNeedsYouNotifier(store, _ => Task.FromResult(Snap(2)), sender);

        await notifier.RunOnceAsync(CancellationToken.None);
        await notifier.RunOnceAsync(CancellationToken.None);

        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task RunOnce_DropToZeroThenRise_PushesCountThenClearThenCount()
    {
        var store = NewStore();
        store.Add("https://push.example/aaa", "p", "a");
        var sender = new FakeSender();
        var count = 2;
        var notifier = new WebPushNeedsYouNotifier(store, _ => Task.FromResult(Snap(count)), sender);

        await notifier.RunOnceAsync(CancellationToken.None); // 2 -> push count:2 (rising edge)
        count = 0;
        await notifier.RunOnceAsync(CancellationToken.None); // 0 -> debounce, no push yet
        await notifier.RunOnceAsync(CancellationToken.None); // 0 -> settled, push a single clear (count:0)
        count = 2;
        await notifier.RunOnceAsync(CancellationToken.None); // 2 -> push count:2 again (rising edge)

        Assert.Equal(3, sender.Sent.Count);
        Assert.Contains("\"count\":2", sender.Sent[0].payload);
        Assert.Contains("\"count\":0", sender.Sent[1].payload);
        Assert.Contains("\"count\":2", sender.Sent[2].payload);
    }

    [Fact]
    public async Task RunOnce_AllSessionsDone_SendsExactlyOneZeroClear()
    {
        var store = NewStore();
        store.Add("https://push.example/aaa", "p", "a");
        var sender = new FakeSender();
        var count = 1;
        var notifier = new WebPushNeedsYouNotifier(store, _ => Task.FromResult(Snap(count)), sender);

        await notifier.RunOnceAsync(CancellationToken.None); // 1 -> push count:1 (rising edge)
        count = 0;
        await notifier.RunOnceAsync(CancellationToken.None); // 0 -> debounce, no push yet
        await notifier.RunOnceAsync(CancellationToken.None); // 0 -> settled, one clear (count:0)
        await notifier.RunOnceAsync(CancellationToken.None); // still 0 -> no repeat

        Assert.Equal(2, sender.Sent.Count);
        Assert.Contains("\"count\":1", sender.Sent[0].payload);
        Assert.Contains("\"count\":0", sender.Sent[1].payload);
    }

    [Fact]
    public async Task RunOnce_GoneSubscription_IsPruned()
    {
        var store = NewStore();
        store.Add("https://push.example/alive", "p", "a");
        store.Add("https://push.example/dead", "p", "a");
        var sender = new FakeSender();
        sender.GoneEndpoints.Add("https://push.example/dead");
        var notifier = new WebPushNeedsYouNotifier(store, _ => Task.FromResult(Snap(1)), sender);

        await notifier.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, store.Count);
        Assert.DoesNotContain(store.All(), s => s.Endpoint == "https://push.example/dead");
        Assert.Contains(store.All(), s => s.Endpoint == "https://push.example/alive");
    }

    [Fact]
    public async Task ResetDedupe_RePushesTheCurrentCount()
    {
        var store = NewStore();
        store.Add("https://push.example/aaa", "p", "a");
        var sender = new FakeSender();
        var notifier = new WebPushNeedsYouNotifier(store, _ => Task.FromResult(Snap(2)), sender);

        await notifier.RunOnceAsync(CancellationToken.None); // push
        notifier.ResetDedupe();                              // a new device subscribed
        await notifier.RunOnceAsync(CancellationToken.None); // re-push the current count

        Assert.Equal(2, sender.Sent.Count);
    }

    private sealed class FakeSender : IWebPushSender
    {
        public List<(string endpoint, string payload)> Sent { get; } = new();
        public HashSet<string> GoneEndpoints { get; } = new();

        public Task SendAsync(StoredPushSubscription subscription, string payloadJson, CancellationToken cancellationToken)
        {
            if (GoneEndpoints.Contains(subscription.Endpoint))
                throw new PushServiceClientException("gone", HttpStatusCode.Gone);
            Sent.Add((subscription.Endpoint, payloadJson));
            return Task.CompletedTask;
        }
    }
}
