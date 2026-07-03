using System.Net;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Lib.Net.Http.WebPush;

namespace CcDirector.Gateway.Push;

/// <summary>
/// Keeps the mobile app-icon "needs you" dot in sync while the phone app is closed. Nothing polls the
/// fleet roster on the Gateway unless a client asks for it, so when the phone is asleep no one would
/// notice a session going red - this background notifier is that watcher.
///
/// On a fixed interval (only while at least one device is subscribed) it reads the CURRENT count of
/// sessions that "need you" (the same effective-red bucket the roster shows, via
/// <see cref="SessionOrdering.Classify"/>) and, when that count is above zero and has changed since
/// the last push, sends every subscription a <c>{ "count": N }</c> message. The service worker turns
/// that into the icon dot.
///
/// It pushes the count on every rise or change, and sends ONE "zero" push on the falling edge (the
/// moment all sessions are done) so the dot CLEARS even while the phone app is closed - the service
/// worker closes its notification on a zero payload. A push that shows no notification is a "silent"
/// push that browsers budget (the userVisibleOnly contract); we spend that budget sparingly - only
/// the single falling edge, never a repeated zero - which Chrome/Android tolerates for an installed,
/// engaged app. The foreground app also clears the dot when it opens with nothing waiting, so the two
/// paths agree.
/// </summary>
public sealed class WebPushNeedsYouNotifier : IDisposable
{
    /// <summary>Interval between roster checks while at least one device is subscribed.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(8);

    /// <summary>A short settling delay before the first check, so startup finishes first.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    // The sentinel "nothing pushed yet" last-count. Chosen below any real count (which is >= 0) so the
    // first non-zero count always differs from it and pushes. Resetting to it (no subscribers, or a
    // fresh subscribe) forces the next non-zero count to re-push.
    private const int NotYetPushed = -1;

    private readonly PushSubscriptionStore _store;
    private readonly Func<CancellationToken, Task<int>> _getNeedsYouCount;
    private readonly IWebPushSender _sender;

    private System.Threading.Timer? _timer;
    private int _busy; // 0 = idle, 1 = a tick is running (reentrancy guard)
    private int _lastNotifiedCount = NotYetPushed;

    public WebPushNeedsYouNotifier(
        PushSubscriptionStore store,
        Func<CancellationToken, Task<int>> getNeedsYouCount,
        IWebPushSender sender)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _getNeedsYouCount = getNeedsYouCount ?? throw new ArgumentNullException(nameof(getNeedsYouCount));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
    }

    /// <summary>Start the background poll. Returns immediately; the first check runs after a short delay.</summary>
    public void Start()
    {
        _timer = new System.Threading.Timer(_ => Tick(), null, StartupDelay, PollInterval);
        FileLog.Write($"[WebPushNeedsYouNotifier] started: every {PollInterval.TotalSeconds:0}s (while subscribed)");
    }

    /// <summary>
    /// Force the next check to re-push the current count even if it is unchanged. Called when a new
    /// device subscribes so it receives the current dot promptly rather than only on the next change.
    /// </summary>
    public void ResetDedupe()
    {
        Interlocked.Exchange(ref _lastNotifiedCount, NotYetPushed);
    }

    /// <summary>
    /// The pure decision: given the current needs-you count and the last count pushed, should a push
    /// go out now, and what becomes the new "last pushed" value? Pushes a changed non-zero count (the
    /// dot appears/updates) AND pushes a single zero on the falling edge from a non-zero count (the dot
    /// clears, even while the app is closed). It never pushes a zero at startup or a repeated zero.
    /// </summary>
    public static (bool push, int newLast) Decide(int current, int last)
    {
        // A rise, or a change to a different non-zero count -> push the new count.
        if (current > 0 && current != last)
            return (true, current);
        // The falling edge to zero -> push ONE clear. Guard on last > 0 so this only fires after a real
        // non-zero push (never at startup where last is the sentinel, never a repeated zero).
        if (current == 0 && last > 0)
            return (true, 0);
        // Nothing worth a push. Record 0 while idle so the next rise re-pushes.
        return (false, current <= 0 ? 0 : last);
    }

    /// <summary>The count of sessions that currently "need you" (effective-red, not parked).</summary>
    public static int CountNeedsYou(IEnumerable<SessionDto> sessions) =>
        sessions.Count(s => SessionOrdering.Classify(s) == SessionOrdering.TriageBucket.NeedsYou);

    /// <summary>
    /// One poll: skip entirely when no one is subscribed; otherwise read the current count, decide,
    /// and push to every subscription when the decision says so. Public so a test can drive it directly.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (_store.Count == 0)
        {
            // No subscribers - nothing to do, and reset so a device that subscribes later re-pushes.
            Interlocked.Exchange(ref _lastNotifiedCount, NotYetPushed);
            return;
        }

        int current;
        try
        {
            current = await _getNeedsYouCount(cancellationToken);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WebPushNeedsYouNotifier] roster read failed: {ex.Message}");
            return;
        }

        var (push, newLast) = Decide(current, Volatile.Read(ref _lastNotifiedCount));
        Interlocked.Exchange(ref _lastNotifiedCount, newLast);
        if (!push) return;

        await SendToAllAsync(current, cancellationToken);
    }

    private async Task SendToAllAsync(int count, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new NeedsYouPayload { Count = count });
        var subscriptions = _store.All();
        FileLog.Write($"[WebPushNeedsYouNotifier] pushing count={count} to {subscriptions.Count} subscription(s)");

        foreach (var subscription in subscriptions)
        {
            try
            {
                await _sender.SendAsync(subscription, payload, cancellationToken);
            }
            catch (PushServiceClientException ex) when (
                ex.StatusCode == HttpStatusCode.Gone || ex.StatusCode == HttpStatusCode.NotFound)
            {
                // The push service says this subscription no longer exists (the browser dropped it).
                // Drop it so we stop paying to send to a dead endpoint.
                _store.Remove(subscription.Endpoint);
                FileLog.Write($"[WebPushNeedsYouNotifier] pruned an expired subscription (status {(int)ex.StatusCode})");
            }
            catch (Exception ex)
            {
                // A transient failure for one subscription must not stop the others.
                FileLog.Write($"[WebPushNeedsYouNotifier] push send failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The timer boundary: owns the reentrancy guard and try/catch so one slow or failing check never
    /// stacks up or crashes the timer thread.
    /// </summary>
    private void Tick()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return; // a previous tick is still running - skip this one
        _ = TickAsync();
    }

    private async Task TickAsync()
    {
        try
        {
            await RunOnceAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WebPushNeedsYouNotifier] tick FAILED: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        (_sender as IDisposable)?.Dispose();
    }

    private sealed class NeedsYouPayload
    {
        // Lowercase on the wire so the service worker reads event.data.json().count.
        [System.Text.Json.Serialization.JsonPropertyName("count")]
        public int Count { get; set; }
    }
}
