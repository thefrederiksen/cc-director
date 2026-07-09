using System.Net;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Push;
using Lib.Net.Http.WebPush;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The needs-you notifier decides WHEN to push the app-icon dot count and prunes dead subscriptions.
/// These tests drive it with a fake sender and a scripted count, so no real push service is touched.
/// </summary>
public sealed class WebPushNeedsYouNotifierTests : IDisposable
{
    private readonly string _storePath =
        Path.Combine(Path.GetTempPath(), $"pushsub-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_storePath)) File.Delete(_storePath);
    }

    // ---- Pure decision logic -------------------------------------------------------------------

    [Theory]
    [InlineData(2, -1, true, 2)]   // first non-zero count -> push
    [InlineData(2, 2, false, 2)]   // unchanged, still 2 -> no push
    [InlineData(3, 2, true, 3)]    // changed to 3 -> push
    [InlineData(0, 2, true, 0)]    // dropped to zero after a real count -> push ONE clear
    [InlineData(0, 0, false, 0)]   // already cleared -> no repeated zero push
    [InlineData(0, -1, false, 0)]  // startup with nothing waiting -> no push
    [InlineData(2, 0, true, 2)]    // risen from zero again -> push
    public void Decide_PushesChangesAndTheFallingEdgeClear(int current, int last, bool expectPush, int expectNewLast)
    {
        var (push, newLast) = WebPushNeedsYouNotifier.Decide(current, last);
        Assert.Equal(expectPush, push);
        Assert.Equal(expectNewLast, newLast);
    }

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
        var store = new PushSubscriptionStore(_storePath);
        var reads = 0;
        var notifier = new WebPushNeedsYouNotifier(store, _ => { reads++; return Task.FromResult(5); }, new FakeSender());

        await notifier.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task RunOnce_FirstNonZeroCount_PushesToEverySubscription()
    {
        var store = new PushSubscriptionStore(_storePath);
        store.Add("https://push.example/aaa", "p", "a");
        store.Add("https://push.example/bbb", "p", "a");
        var sender = new FakeSender();
        var notifier = new WebPushNeedsYouNotifier(store, _ => Task.FromResult(2), sender);

        await notifier.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, sender.Sent.Count);
        Assert.All(sender.Sent, s => Assert.Contains("\"count\":2", s.payload));
    }

    [Fact]
    public async Task RunOnce_UnchangedCount_DoesNotPushTwice()
    {
        var store = new PushSubscriptionStore(_storePath);
        store.Add("https://push.example/aaa", "p", "a");
        var sender = new FakeSender();
        var notifier = new WebPushNeedsYouNotifier(store, _ => Task.FromResult(2), sender);

        await notifier.RunOnceAsync(CancellationToken.None);
        await notifier.RunOnceAsync(CancellationToken.None);

        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task RunOnce_DropToZeroThenRise_PushesCountThenClearThenCount()
    {
        var store = new PushSubscriptionStore(_storePath);
        store.Add("https://push.example/aaa", "p", "a");
        var sender = new FakeSender();
        var count = 2;
        var notifier = new WebPushNeedsYouNotifier(store, _ => Task.FromResult(count), sender);

        await notifier.RunOnceAsync(CancellationToken.None); // 2 -> push count:2
        count = 0;
        await notifier.RunOnceAsync(CancellationToken.None); // 0 -> push a single clear (count:0)
        count = 2;
        await notifier.RunOnceAsync(CancellationToken.None); // 2 -> push count:2 again

        Assert.Equal(3, sender.Sent.Count);
        Assert.Contains("\"count\":2", sender.Sent[0].payload);
        Assert.Contains("\"count\":0", sender.Sent[1].payload);
        Assert.Contains("\"count\":2", sender.Sent[2].payload);
    }

    [Fact]
    public async Task RunOnce_AllSessionsDone_SendsExactlyOneZeroClear()
    {
        var store = new PushSubscriptionStore(_storePath);
        store.Add("https://push.example/aaa", "p", "a");
        var sender = new FakeSender();
        var count = 1;
        var notifier = new WebPushNeedsYouNotifier(store, _ => Task.FromResult(count), sender);

        await notifier.RunOnceAsync(CancellationToken.None); // 1 -> push count:1
        count = 0;
        await notifier.RunOnceAsync(CancellationToken.None); // 0 -> one clear (count:0)
        await notifier.RunOnceAsync(CancellationToken.None); // still 0 -> no repeat
        await notifier.RunOnceAsync(CancellationToken.None); // still 0 -> no repeat

        Assert.Equal(2, sender.Sent.Count);
        Assert.Contains("\"count\":1", sender.Sent[0].payload);
        Assert.Contains("\"count\":0", sender.Sent[1].payload);
    }

    [Fact]
    public async Task RunOnce_GoneSubscription_IsPruned()
    {
        var store = new PushSubscriptionStore(_storePath);
        store.Add("https://push.example/alive", "p", "a");
        store.Add("https://push.example/dead", "p", "a");
        var sender = new FakeSender();
        sender.GoneEndpoints.Add("https://push.example/dead");
        var notifier = new WebPushNeedsYouNotifier(store, _ => Task.FromResult(1), sender);

        await notifier.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, store.Count);
        Assert.DoesNotContain(store.All(), s => s.Endpoint == "https://push.example/dead");
        Assert.Contains(store.All(), s => s.Endpoint == "https://push.example/alive");
    }

    [Fact]
    public async Task ResetDedupe_RePushesTheCurrentCount()
    {
        var store = new PushSubscriptionStore(_storePath);
        store.Add("https://push.example/aaa", "p", "a");
        var sender = new FakeSender();
        var notifier = new WebPushNeedsYouNotifier(store, _ => Task.FromResult(2), sender);

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
