using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// Resolves the signed-in DevThrottle user (email + nickname) for a session's fleet preamble (issue
/// #1357), reading it from the Gateway's <c>GET /account/status</c> through
/// <see cref="GatewayAccountStatusClient"/>. The account credential lives on the Gateway (issue #651),
/// so the Director never holds a token of its own - it asks the Gateway.
///
/// What that answer MEANS differs by deployment (issue #1856), and this type is deliberately indifferent to
/// which: on self-host the Gateway reports its own signed-in account; on hosted it reports whether the
/// CALLING device is enrolled, folded from that device's own key, because a hosted Gateway holds no account
/// credential of its own. Either way this reads a verdict the Gateway owns rather than deciding one.
///
/// A short-lived in-memory cache keeps this off the hot path: the preamble endpoint is fetched at
/// session start (and re-fetched by some agents on /clear), and the Pi launch path reads the cached
/// snapshot synchronously so session creation never blocks on the network. On a cold or stale cache
/// <see cref="ResolveAsync"/> makes one Gateway call and stores the result; <see cref="CurrentSnapshot"/>
/// returns the last resolved value without any network call.
///
/// When the Gateway is not configured, is unreachable, or reports signed-out, the resolved user is null
/// and the preamble omits the identity line - the caller never fabricates an identity.
/// </summary>
public sealed class SignedInUserProvider
{
    /// <summary>How long a resolved snapshot is reused before the next resolve re-reads the Gateway.</summary>
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(5);

    private readonly Func<GatewayConfig> _configLoader;
    private readonly GatewayAccountStatusClient _client;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _timeProvider;

    private readonly object _gate = new();
    private SignedInUser? _snapshot;
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;
    private bool _haveSnapshot;

    /// <param name="configLoader">
    /// Re-reads the Gateway connection config each resolve (pass <c>GatewayConfig.Load</c>) so a
    /// settings change is picked up without a restart.
    /// </param>
    /// <param name="client">The Gateway account-status client; a default one is created when null.</param>
    /// <param name="ttl">Cache lifetime; defaults to <see cref="DefaultCacheTtl"/>.</param>
    /// <param name="timeProvider">Time source for the cache clock; defaults to the system clock (tests inject one).</param>
    public SignedInUserProvider(
        Func<GatewayConfig> configLoader,
        GatewayAccountStatusClient? client = null,
        TimeSpan? ttl = null,
        TimeProvider? timeProvider = null)
    {
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _client = client ?? new GatewayAccountStatusClient();
        _ttl = ttl ?? DefaultCacheTtl;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The last resolved signed-in user, WITHOUT a network call - null until the first successful
    /// resolve, or when the last resolve found no signed-in user. Used by the synchronous Pi launch path
    /// so session creation never blocks on the Gateway.
    /// </summary>
    public SignedInUser? CurrentSnapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    /// <summary>
    /// Returns the signed-in user, serving a fresh cached snapshot when one exists and otherwise reading
    /// the Gateway once and caching the result. Never throws for a Gateway problem - the underlying
    /// client reports an unreachable/erroring Gateway as a not-signed-in result value, which resolves to
    /// a null user (identity line omitted).
    /// </summary>
    public async Task<SignedInUser?> ResolveAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_haveSnapshot && _timeProvider.GetUtcNow() - _fetchedAt < _ttl)
                return _snapshot;
        }

        var status = await _client.GetStatusAsync(_configLoader(), ct).ConfigureAwait(false);
        var user = ToUser(status);

        lock (_gate)
        {
            _snapshot = user;
            _fetchedAt = _timeProvider.GetUtcNow();
            _haveSnapshot = true;
        }

        FileLog.Write($"[SignedInUserProvider] ResolveAsync: user={(user is null ? "<none>" : "resolved")}");
        return user;
    }

    /// <summary>Maps a Gateway status to a <see cref="SignedInUser"/>, or null when not usably signed in.</summary>
    internal static SignedInUser? ToUser(GatewayAccountStatus status)
    {
        if (status is null || !status.SignedIn || string.IsNullOrWhiteSpace(status.Email))
            return null;
        return new SignedInUser(status.Email!, status.Nickname);
    }
}
