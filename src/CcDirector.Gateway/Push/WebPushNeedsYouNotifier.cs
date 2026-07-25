using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using CcDirector.Core.Tenancy;
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
///   - A newly-EXPIRED snooze (Snooze Length mission): when a session RETURNS from an expired snooze
///     (<see cref="SessionDto.SnoozeExpired"/>) it is genuinely new, actionable news, so it is ANNOUNCED
///     ONCE with distinct copy that is ALLOWED to buzz even if the dot is already up. Crucially, the
///     announcement is keyed to a session ENTERING the expired set that we have not yet announced, NOT to
///     "any expired snooze is present" - a dead-Director expired snooze lingers in "needs you" forever, so
///     buzzing on its mere presence would re-buzz every poll. Once announced it is remembered
///     (<see cref="DotState.Announced"/>) and folds into the silent dot/heartbeat, never buzzing again
///     while it lingers.
/// ONE PASS PER TENANT. This engine holds no timer: <see cref="PushNeedsYouTenantSweep"/> drives
/// <see cref="RunOnceAsync"/> once per tenant, inside that tenant's own scope, so the subscription store it
/// reads holds exactly that tenant's phones and the roster it counts is exactly that tenant's fleet. That is
/// what turned notifications ON for the hosted Gateway: the notifier used to own a bare timer, which has no
/// tenant, so on hosted it would have read the tenant-scoped subscriptions store with no scope and failed
/// closed every tick - and rather than fail every tick the whole notifier was skipped when hosted. Skipped
/// means no dot ever appeared on a phone talking to the hosted Gateway, AND no falling-edge clear was ever
/// sent, so a dot raised earlier (by a desktop Gateway) could never be cleared by the hosted one. Self-host
/// is a single Local pass, unchanged.
///
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
    private readonly Func<CancellationToken, Task<NeedsYouSnapshot>> _getSnapshot;
    private readonly IWebPushSender _sender;
    private readonly Func<TenantId> _currentTenant;

    // The dot decision, PARTITIONED PER TENANT. A single flat DotState was the reason hosted push could not
    // be turned on at all: one tenant's zero-count pass would clear the dot the previous tenant's pass had
    // just decided to show, and the heartbeat counters would interleave into nonsense. Each tenant's fleet
    // has its own rising edge, its own falling edge and its own announced set, so each gets its own state.
    // Self-host has exactly one entry (Local) and behaves as it always did.
    private readonly ConcurrentDictionary<TenantId, DotState> _dots = new();

    /// <param name="store">The subscription store. Tenant-scoped: inside a tenant's scope it holds exactly
    /// that tenant's devices, which is what makes one pass push to one tenant's phones and no others.</param>
    /// <param name="getSnapshot">Reads the CURRENT tenant's needs-you snapshot. Called inside the tenant
    /// scope, so it must read the ambient tenant and never resolve a tenant of its own.</param>
    /// <param name="sender">The Web Push transport.</param>
    /// <param name="currentTenant">The tenant of the pass now running. Omitted means single-tenant
    /// (<see cref="TenantId.Local"/>) - the self-host shape and the unit-test default, so leaving it out can
    /// never accidentally produce hosted behavior.</param>
    public WebPushNeedsYouNotifier(
        PushSubscriptionStore store,
        Func<CancellationToken, Task<NeedsYouSnapshot>> getSnapshot,
        IWebPushSender sender,
        Func<TenantId>? currentTenant = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _getSnapshot = getSnapshot ?? throw new ArgumentNullException(nameof(getSnapshot));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _currentTenant = currentTenant ?? (() => TenantId.Local);
    }

    /// <summary>
    /// Force the next check to re-push the dot even if the count is unchanged. Called when a new device
    /// subscribes so it receives the current dot promptly (on the next poll, if something needs you)
    /// rather than only on the next rising edge.
    ///
    /// Resets the SUBSCRIBING tenant only (it is called from a request, so a tenant is bound): one account
    /// adding a phone must not re-push every other account's dot.
    /// </summary>
    public void ResetDedupe() => _dots.TryRemove(_currentTenant(), out _);

    /// <summary>
    /// The count of sessions that "need you" plus the ids of those that just returned from an expired
    /// snooze (a subset of the needs-you set). The notifier reads this once per poll from the same
    /// aggregated roster every client sees.
    /// </summary>
    public readonly record struct NeedsYouSnapshot(int Count, IReadOnlyCollection<string> ExpiredNeedsYouIds);

    /// <summary>
    /// Immutable decision state for the app-icon dot: whether we have told the phone a dot is present,
    /// how many consecutive zero-count polls we have seen while it is present (the falling-edge debounce
    /// counter), how many polls have passed since we last pushed while it is present (the heartbeat
    /// counter that brings a phone-cleared dot back), and the set of returned-from-snooze session ids we
    /// have ALREADY announced (so each is buzzed exactly once and a lingering expired snooze never
    /// re-buzzes).
    /// </summary>
    public readonly record struct DotState(bool DotShowing, int ZeroStreak, int PollsSincePush, ImmutableHashSet<string> Announced)
    {
        /// <summary>The starting state: no dot on the phone, no pending clear, nothing announced.</summary>
        public static readonly DotState Initial = new(false, 0, 0, ImmutableHashSet<string>.Empty);
    }

    private static readonly IReadOnlyCollection<string> NoExpired = Array.Empty<string>();

    /// <summary>
    /// The pure decision, count-only overload (no snooze-expiry signal). Kept so the dot's rising /
    /// heartbeat / falling behavior can be reasoned about and tested in isolation.
    /// </summary>
    public static (bool push, int count, DotState next) Decide(int current, DotState state)
    {
        var (push, count, _, next) = Decide(current, NoExpired, state);
        return (push, count, next);
    }

    /// <summary>
    /// The pure decision. The app-icon dot is boolean on Android (a dot is present or not - the exact
    /// count does not change it), so we push on the moments that matter and stay quiet otherwise:
    ///   - Newly-expired snooze (highest priority): one or more sessions in <paramref name="expiredNow"/>
    ///     were not in <see cref="DotState.Announced"/> - announce ONCE (push, snoozeEnded=true, buzz
    ///     permitted) even if the dot is already up, then remember them so they never re-buzz.
    ///   - Rising edge (no dot yet, work now needs you): push the current count so the dot appears.
    ///   - Heartbeat (dot showing, <see cref="HeartbeatPolls"/> polls since the last push): re-push the
    ///     current count so a dot the phone cleared on its own comes back. Silent, so it does not buzz.
    ///   - Falling edge (dot showing, count has read zero for <see cref="ClearConfirmations"/> polls in a
    ///     row): push a single zero so the dot clears, even while the app is closed.
    /// The <see cref="DotState.Announced"/> set is pruned to only sessions still expired each poll, so a
    /// session whose snooze was cleared and later re-expires is announced afresh. Returns whether to push,
    /// the count to send, whether this push is a snooze-expiry announcement, and the next state.
    /// </summary>
    public static (bool push, int count, bool snoozeEnded, DotState next) Decide(
        int current, IReadOnlyCollection<string> expiredNow, DotState state)
    {
        // Prune the announced set to sessions still expired (a cleared/re-snoozed session drops out and
        // may be announced again if it re-expires), then find the ones newly entering the expired set.
        var expiredSet = expiredNow as ImmutableHashSet<string> ?? ImmutableHashSet.CreateRange(expiredNow);
        var announcedStill = state.Announced.Intersect(expiredSet);
        var hasNewlyExpired = expiredSet.Except(announcedStill).Count > 0;

        if (current > 0)
        {
            // A newly-expired snooze is genuinely new, actionable news: announce it ONCE, and let it buzz
            // even if the dot is already up. After this it is remembered and folds into the silent dot.
            if (hasNewlyExpired)
                return (true, current, true, new DotState(true, 0, 0, expiredSet));

            // Rising edge: work now needs you and no dot is up yet -> show it immediately.
            if (!state.DotShowing)
                return (true, current, false, new DotState(true, 0, 0, announcedStill));

            // Dot already up. Re-assert on the heartbeat (recovers a phone-side clear); otherwise stay
            // quiet, only advancing the heartbeat counter.
            var polls = state.PollsSincePush + 1;
            if (polls >= HeartbeatPolls)
                return (true, current, false, new DotState(true, 0, 0, announcedStill));
            return (false, 0, false, new DotState(true, 0, polls, announcedStill));
        }

        // current == 0: nothing needs you right now (so nothing is expired either).
        if (!state.DotShowing)
            return (false, 0, false, DotState.Initial); // no dot to clear (startup, or already cleared)

        var streak = state.ZeroStreak + 1;
        if (streak >= ClearConfirmations)
            return (true, 0, false, DotState.Initial); // settled at zero -> clear the dot once, forget announcements
        // Debouncing the clear: hold the dot, keep the heartbeat counter so a re-rise resumes cleanly.
        return (false, 0, false, new DotState(true, streak, state.PollsSincePush, announcedStill));
    }

    /// <summary>The count of sessions that currently "need you" (effective-red, not parked).</summary>
    public static int CountNeedsYou(IEnumerable<SessionDto> sessions) =>
        sessions.Count(s => SessionOrdering.Classify(s) == SessionOrdering.TriageBucket.NeedsYou);

    /// <summary>
    /// The ids of sessions that "need you" AND just returned from an expired snooze
    /// (<see cref="SessionDto.SnoozeExpired"/>). A subset of the needs-you set - the ones the notifier
    /// announces once with distinct copy.
    /// </summary>
    public static IReadOnlyCollection<string> ExpiredNeedsYouIds(IEnumerable<SessionDto> sessions) =>
        sessions
            .Where(s => s.SnoozeExpired
                        && !string.IsNullOrEmpty(s.SessionId)
                        && SessionOrdering.Classify(s) == SessionOrdering.TriageBucket.NeedsYou)
            .Select(s => s.SessionId)
            .ToList();

    /// <summary>
    /// One poll: skip entirely when no one is subscribed; otherwise read the current snapshot, decide,
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

        NeedsYouSnapshot snapshot;
        try
        {
            snapshot = await _getSnapshot(cancellationToken);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WebPushNeedsYouNotifier] roster read failed: {ex.Message}");
            return;
        }

        var tenant = _currentTenant();
        var before = _dots.GetValueOrDefault(tenant, DotState.Initial);
        var (push, count, snoozeEnded, next) = Decide(snapshot.Count, snapshot.ExpiredNeedsYouIds, before);
        _dots[tenant] = next;
        if (!push) return;

        await SendToAllAsync(count, snoozeEnded, cancellationToken);
    }

    private async Task SendToAllAsync(int count, bool snoozeEnded, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new NeedsYouPayload { Count = count, SnoozeEnded = snoozeEnded });
        var subscriptions = _store.All();
        FileLog.Write($"[WebPushNeedsYouNotifier] pushing count={count} snoozeEnded={snoozeEnded} to {subscriptions.Count} subscription(s)");

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

    public void Dispose() => (_sender as IDisposable)?.Dispose();

    private sealed class NeedsYouPayload
    {
        // Lowercase on the wire so the service worker reads event.data.json().count.
        [System.Text.Json.Serialization.JsonPropertyName("count")]
        public int Count { get; set; }

        // Snooze Length mission: true only on the one push that ANNOUNCES a newly-returned-from-snooze
        // session. The service worker renders the distinct "Snooze ended" copy and lets that push buzz;
        // every other push stays the quiet dot. Always present so the worker can read it.
        [System.Text.Json.Serialization.JsonPropertyName("snoozeEnded")]
        public bool SnoozeEnded { get; set; }
    }
}
