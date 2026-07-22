using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Api;

/// <summary>Result of a per-sender broadcast rate-limit check (issue #1229).</summary>
/// <param name="Allowed">True when the sender is under the limit and the send was recorded.</param>
/// <param name="LimitPerWindow">The configured maximum broadcasts per window.</param>
/// <param name="WindowSeconds">The window length in seconds.</param>
public readonly record struct RateLimitResult(bool Allowed, int LimitPerWindow, int WindowSeconds);

/// <summary>
/// The stateful half of the Hub's broadcast governance (issue #1229): it mints and validates the
/// human-issued broadcast grants that let a message reach beyond the sender's team, and it rate-limits
/// how often any one session may broadcast so a runaway agent cannot storm the fleet even inside its
/// own team. Kept separate from the pure <see cref="FleetBroadcastPolicy"/> so the scope rule stays
/// I/O-free; this class owns the in-memory state. Thread-safe. One instance per Gateway process.
///
/// Grants are validated (not consumed one-shot) for their short lifetime, so a human who authorizes a
/// fleet-wide announcement can send it without the grant evaporating on the first attempt; the short
/// time-to-live bounds the exposure.
///
/// Hosted Multi-Tenancy (audit-a): the Gateway process is shared by every tenant, so ALL state here is
/// keyed by the OWNING tenant resolved from the caller's authenticated device key (never from the request
/// body). The rate-limit window is keyed by (tenant, sessionId), so tenant A exhausting the window for
/// session id X can never deny tenant B's same-id broadcast; and every grant records its minting tenant
/// and validates only in that tenant, so A's grant can never authorize a broadcast in B's partition.
/// Self-host resolves every request to <see cref="TenantId.Local"/>, so its behaviour is unchanged.
/// </summary>
public sealed class BroadcastGovernor
{
    private readonly int _maxPerWindow;
    private readonly TimeSpan _window;
    private readonly TimeSpan _grantTtl;
    private readonly Func<DateTime> _now;

    // (owning tenant, senderId) -> UTC timestamps of its recent recorded broadcasts (pruned to the window
    // on each check). Keyed by the tenant so one tenant's window can never touch another's for the same id.
    private readonly ConcurrentDictionary<(TenantId Tenant, string Sender), List<DateTime>> _sends = new();

    // grantId -> (minting tenant, UTC expiry). A grant is valid only in the tenant that minted it.
    private readonly ConcurrentDictionary<string, (TenantId Owner, DateTime Expiry)> _grants = new();

    private readonly object _sendsLock = new();

    /// <param name="maxPerWindow">Maximum broadcasts one session may make per window. Default 5.</param>
    /// <param name="window">The rolling rate-limit window. Default 60 seconds.</param>
    /// <param name="grantTtl">How long a minted grant stays valid. Default 10 minutes.</param>
    /// <param name="now">Clock seam for tests. Default <see cref="DateTime.UtcNow"/>.</param>
    public BroadcastGovernor(int maxPerWindow = 5, TimeSpan? window = null, TimeSpan? grantTtl = null, Func<DateTime>? now = null)
    {
        if (maxPerWindow < 1) throw new ArgumentOutOfRangeException(nameof(maxPerWindow), "The rate limit must allow at least one broadcast per window.");
        _maxPerWindow = maxPerWindow;
        _window = window ?? TimeSpan.FromSeconds(60);
        _grantTtl = grantTtl ?? TimeSpan.FromMinutes(10);
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>The configured maximum broadcasts per window (for messages and diagnostics).</summary>
    public int MaxPerWindow => _maxPerWindow;

    /// <summary>The configured rate-limit window in whole seconds (for messages and diagnostics).</summary>
    public int WindowSeconds => (int)_window.TotalSeconds;

    /// <summary>
    /// Mint a fresh broadcast grant OWNED BY <paramref name="tenant"/>, valid for the configured
    /// time-to-live. Returns the opaque grant id the caller hands to the broadcaster. Only reachable
    /// through the Hub's auth-guarded grant endpoint - there is no Director relay for minting, so an agent
    /// cannot mint its own. The grant validates only in <paramref name="tenant"/> (see
    /// <see cref="IsGrantValid"/>), so on the shared hosted Gateway one tenant's grant can never authorize
    /// another tenant's broadcast.
    /// </summary>
    public string MintGrant(TenantId tenant)
    {
        var id = Guid.NewGuid().ToString("N");
        _grants[id] = (tenant, _now() + _grantTtl);
        return id;
    }

    /// <summary>
    /// True when <paramref name="grantId"/> names a grant that exists, has not expired, AND was minted by
    /// <paramref name="tenant"/>. A grant is bound to its minting tenant, so it is never valid in another
    /// tenant's partition. Prunes any expired grants it encounters. A null/blank id is never valid.
    /// </summary>
    public bool IsGrantValid(TenantId tenant, string? grantId)
    {
        if (string.IsNullOrWhiteSpace(grantId)) return false;
        PruneExpiredGrants();
        return _grants.TryGetValue(grantId, out var grant)
            && grant.Expiry > _now()
            && grant.Owner == tenant;
    }

    /// <summary>
    /// Record a broadcast by <paramref name="senderId"/> in <paramref name="tenant"/> against the rolling
    /// window and report whether it is under the limit. The window is keyed by (tenant, sender), so a
    /// caller only ever touches its OWN tenant's window - one tenant exhausting session id X can never deny
    /// another tenant's same-id broadcast. When the sender is over the limit nothing is recorded and
    /// <see cref="RateLimitResult.Allowed"/> is false. A null/blank sender id is exempt (it cannot be
    /// tracked); the scope policy already denies an unidentified fleet-wide sender.
    /// </summary>
    public RateLimitResult TryRecordSend(TenantId tenant, string? senderId)
    {
        if (string.IsNullOrWhiteSpace(senderId))
            return new RateLimitResult(true, _maxPerWindow, WindowSeconds);

        var now = _now();
        var cutoff = now - _window;
        // Preserve the original case-insensitive sender matching while keying the tenant exactly.
        var key = (tenant, senderId.ToLowerInvariant());

        lock (_sendsLock)
        {
            var stamps = _sends.GetOrAdd(key, _ => new List<DateTime>());
            stamps.RemoveAll(t => t < cutoff);
            if (stamps.Count >= _maxPerWindow)
                return new RateLimitResult(false, _maxPerWindow, WindowSeconds);

            stamps.Add(now);
            return new RateLimitResult(true, _maxPerWindow, WindowSeconds);
        }
    }

    private void PruneExpiredGrants()
    {
        var now = _now();
        foreach (var kvp in _grants)
            if (kvp.Value.Expiry <= now)
                _grants.TryRemove(kvp.Key, out _);
    }
}
