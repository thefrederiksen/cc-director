using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// The desktop's last-known copy of the user's snooze lengths and default, fetched FROM the Gateway.
///
/// Two facts force this shape. First, the setting is Gateway-owned and per-user, so a Director must ask
/// the Gateway for it - reading <c>config.json</c> locally would be wrong on every machine that is not
/// the Gateway's, because that machine's file is a different file. Second, the session menu is built
/// on every open and owes the user feedback in under 100ms (CodingStyle.md), so it cannot wait on a
/// network call that may cross a tunnel to another machine. The menu therefore reads
/// <see cref="Current"/>, which never blocks.
///
/// The cache warms itself when the Gateway connection goes green (see <see cref="AttachTo"/>) - the
/// moment the answer first becomes gettable - and refreshes when a read finds the value older than
/// <see cref="StaleAfter"/>, so an edit made in the Cockpit reaches the desktop menu without a restart.
///
/// <see cref="Current"/> is null until the first successful fetch. That is NOT a fallback: a null means
/// the desktop genuinely does not know the user's lengths, and the menu says so by offering only the
/// plain Snooze (which still works - a hold with no length makes the Gateway apply the default). It
/// never invents lengths to show.
/// </summary>
public sealed class SnoozeOptionsCache
{
    /// <summary>
    /// How long a fetched list is trusted before a read triggers a background refresh. Five minutes is
    /// well inside "I changed it in the Cockpit and went to look at the menu", and far outside anything
    /// that would make opening a menu chatty.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    private readonly Func<IGatewayHold?> _hold;
    private readonly object _lock = new();
    private SnoozeOptionsResponse? _current;
    private DateTime _fetchedAtUtc = DateTime.MinValue;
    private Task? _inFlight;

    public SnoozeOptionsCache(Func<IGatewayHold?> hold) => _hold = hold;

    /// <summary>
    /// The last-known lengths, or null when the desktop has never successfully fetched them. Never
    /// blocks and never throws - safe to read while building a context menu. Reading a stale value kicks
    /// off a background refresh and returns the stale value for THIS open; the next open sees the fresh
    /// one.
    /// </summary>
    public SnoozeOptionsResponse? Current
    {
        get
        {
            SnoozeOptionsResponse? current;
            bool stale;
            lock (_lock)
            {
                current = _current;
                stale = DateTime.UtcNow - _fetchedAtUtc > StaleAfter;
            }

            if (stale) BeginRefresh();
            return current;
        }
    }

    /// <summary>
    /// Warm the cache whenever the Gateway connection goes green. Connecting is the first moment the
    /// answer is gettable, and it is also when a reconnect should re-read a list that may have changed
    /// while this Director was away.
    /// </summary>
    public void AttachTo(GatewayConnectionMonitor monitor)
    {
        monitor.Changed += () =>
        {
            if (monitor.Status == GatewayConnectionStatus.Connected)
            {
                FileLog.Write("[SnoozeOptionsCache] Gateway connected: refreshing the snooze lengths");
                BeginRefresh();
            }
        };
    }

    /// <summary>
    /// Start a refresh unless one is already running, and never let its failure escape - an unreachable
    /// Gateway must not crash the app or a menu open. Fire-and-forget by design: the caller keeps the
    /// value it already has.
    /// </summary>
    private void BeginRefresh()
    {
        lock (_lock)
        {
            if (_inFlight is { IsCompleted: false }) return;
            _inFlight = Task.Run(() => RefreshAsync());
        }
    }

    /// <summary>
    /// Fetch and store the lengths. Swallows failure ON PURPOSE and says so in the log: this is the one
    /// place that decides an unreachable Gateway means "keep showing what we last knew" rather than
    /// "break the menu". A failure leaves <see cref="Current"/> untouched, so it stays either null
    /// (offer only the plain Snooze) or the last real answer - it never becomes a made-up list.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var options = await (_hold() is { } hold ? hold.GetSnoozeOptionsAsync(ct) : Task.FromResult<SnoozeOptionsResponse?>(null));
            if (options is null) return;

            lock (_lock)
            {
                _current = options;
                _fetchedAtUtc = DateTime.UtcNow;
            }
            FileLog.Write($"[SnoozeOptionsCache] RefreshAsync: cached [{string.Join(", ", options.Presets)}], default={options.DefaultMinutes}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SnoozeOptionsCache] RefreshAsync FAILED (keeping the last-known lengths): {ex.Message}");
        }
    }
}
