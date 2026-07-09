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
/// <see cref="SessionOrdering.Classify"/>) and pushes every subscription a <c>{ "count": N }</c>
/// message ONLY when the app-icon dot must flip. The service worker turns that into the icon dot.
///
/// The dot is boolean on the phone: on Android the launcher draws a dot while a notification is
/// present, and it does not matter whether two or five sessions need you - the dot looks the same. So
/// this notifier pushes on these moments, and stays quiet otherwise:
///   - The RISING edge (nothing was waiting, now something needs you) pushes the count so the dot
///     appears immediately.
///   - A HEARTBEAT while the dot is up (every <see cref="HeartbeatPolls"/> polls) re-asserts the count.
///     This is what makes the dot COME BACK after the phone cleared it in a way the Gateway cannot see -
///     the user swiping the notification away, or opening the app (which clears the dot on foreground)
///     and then leaving while sessions still wait. Without it the Gateway would believe the dot is
///     still up and never re-send it. The re-assert shows the same silent, tagged notification, so it
///     does not buzz.
///   - The FALLING edge (the count has read zero for <see cref="ClearConfirmations"/> polls in a row)
///     pushes a single zero so the dot CLEARS even while the phone app is closed - the service worker
///     closes its notification on a zero payload.
/// A change from one non-zero count to another (2 -> 3 -> 4) is NOT pushed between heartbeats: the dot
/// is already there. Keeping the volume this low is what stops the constant pinging, and in particular
/// keeps the silent zero-clear push (a push that shows no notification, which browsers budget under the
/// userVisibleOnly contract - the real source of the earlier buzzing was one such generic notification
/// per falling edge) rare enough to be delivered. The short confirmation window on the falling edge
/// also swallows a brief flicker - one session finishing a moment before another goes red - so it never
/// emits a clear-then-reappear pair.
/// </summary>
public sealed class WebPushNeedsYouNotifier : IDisposable
{
    /// <summary>Interval between roster checks while at least one device is subscribed.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(8);

    /// <summary>A short settling delay before the first check, so startup finishes first.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Number of consecutive zero-count polls required before the "all clear" push is sent. The dot
    /// appears the instant work needs you, but only clears after the count has settled at zero for this
    /// many polls - so a brief flicker (one session finishing a moment before another goes red) does
    /// not emit a needless clear-then-reappear pair of pushes. At the 8-second poll interval this is a
    /// settle window of roughly 8 to 16 seconds before the dot clears.
    /// </summary>
    public const int ClearConfirmations = 2;

    /// <summary>
    /// Number of polls between heartbeat re-assertions while the dot is up. The Gateway cannot observe
    /// the phone clearing the dot on its own (a swipe-away, or the app clearing it on foreground), so
    /// while sessions still need you it re-sends the current count this often to bring the dot back. At
    /// the 8-second poll interval this is roughly once a minute - responsive enough that a dismissed dot
    /// returns quickly, infrequent enough that the silent re-assert is never felt as pinging.
    /// </summary>
    public const int HeartbeatPolls = 8;

    private readonly PushSubscriptionStore _store;
    private readonly Func<CancellationToken, Task<int>> _getNeedsYouCount;
    private readonly IWebPushSender _sender;

    private System.Threading.Timer? _timer;
    private int _busy; // 0 = idle, 1 = a tick is running (reentrancy guard)
    private readonly object _dotLock = new();
    private DotState _dot = DotState.Initial;

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
    /// Force the next check to re-push the dot even if the count is unchanged. Called when a new device
    /// subscribes so it receives the current dot promptly (on the next poll, if something needs you)
    /// rather than only on the next rising edge.
    /// </summary>
    public void ResetDedupe()
    {
        lock (_dotLock)
            _dot = DotState.Initial;
    }

    /// <summary>
    /// Immutable decision state for the app-icon dot: whether we have told the phone a dot is present,
    /// how many consecutive zero-count polls we have seen while it is present (the falling-edge debounce
    /// counter), and how many polls have passed since we last pushed while it is present (the heartbeat
    /// counter that brings a phone-cleared dot back).
    /// </summary>
    public readonly record struct DotState(bool DotShowing, int ZeroStreak, int PollsSincePush)
    {
        /// <summary>The starting state: no dot on the phone, no pending clear.</summary>
        public static readonly DotState Initial = new(false, 0, 0);
    }

    /// <summary>
    /// The pure decision. The app-icon dot is boolean on Android (a dot is present or not - the exact
    /// count does not change it), so we push on the moments that matter and stay quiet otherwise:
    ///   - Rising edge (no dot yet, work now needs you): push the current count so the dot appears.
    ///   - Heartbeat (dot showing, <see cref="HeartbeatPolls"/> polls since the last push): re-push the
    ///     current count so a dot the phone cleared on its own (a swipe-away, or the app clearing it on
    ///     foreground) comes back while sessions still wait. The re-assert is silent, so it does not buzz.
    ///   - Falling edge (dot showing, count has read zero for <see cref="ClearConfirmations"/> polls in
    ///     a row): push a single zero so the dot clears, even while the app is closed.
    /// A change from one non-zero count to another (2 -> 3 -> 4) does NOT push between heartbeats: the
    /// dot is already there. Returns whether to push, the count to send, and the next state to carry
    /// forward.
    /// </summary>
    public static (bool push, int count, DotState next) Decide(int current, DotState state)
    {
        if (current > 0)
        {
            // Rising edge: work now needs you and no dot is up yet -> show it immediately.
            if (!state.DotShowing)
                return (true, current, new DotState(DotShowing: true, ZeroStreak: 0, PollsSincePush: 0));

            // Dot already up. Re-assert on the heartbeat (recovers a phone-side clear); otherwise stay
            // quiet, only advancing the heartbeat counter.
            var polls = state.PollsSincePush + 1;
            if (polls >= HeartbeatPolls)
                return (true, current, new DotState(DotShowing: true, ZeroStreak: 0, PollsSincePush: 0));
            return (false, 0, new DotState(DotShowing: true, ZeroStreak: 0, PollsSincePush: polls));
        }

        // current == 0: nothing needs you right now.
        if (!state.DotShowing)
            return (false, 0, DotState.Initial); // no dot to clear (startup, or already cleared)

        var streak = state.ZeroStreak + 1;
        if (streak >= ClearConfirmations)
            return (true, 0, DotState.Initial); // settled at zero -> clear the dot once
        // Debouncing the clear: hold the dot, keep the heartbeat counter so a re-rise resumes cleanly.
        return (false, 0, new DotState(DotShowing: true, ZeroStreak: streak, PollsSincePush: state.PollsSincePush));
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
            ResetDedupe();
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

        bool push;
        int count;
        lock (_dotLock)
        {
            (push, count, _dot) = Decide(current, _dot);
        }
        if (!push) return;

        await SendToAllAsync(count, cancellationToken);
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
