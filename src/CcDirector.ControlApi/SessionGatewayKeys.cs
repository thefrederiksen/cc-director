using System.Collections.Concurrent;
using CcDirector.Core.Security;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// The Director's record of which of its live sessions hold a Gateway session key
/// (Remove-the-network-port mission, phase 1b).
///
/// IT KEEPS HASHES, NOT KEYS. A raw key is minted here, returned ONCE to the caller that stamps it into
/// the session's environment, and then forgotten - only its hash is retained, and only so the Director
/// can re-register the session on a tunnel reseed. This is deliberate: a Director process that held
/// every live session's raw key would be a single place from which any session could be impersonated,
/// which is the concentration this whole phase exists to avoid. Once stamped, the raw key lives in one
/// place - the environment of the one session it belongs to.
///
/// WHY RE-REGISTER AT ALL. The tunnel is the delivery channel, and it drops: a Gateway restart, a
/// network blip, a Director that reconnects. The rest of the tunnel already answers this the same way -
/// every reconnect re-pushes the FULL session snapshot, which makes the new connection authoritative
/// rather than trying to replay what was missed. Session keys ride that same reseed, so a session whose
/// registration was lost with the connection is valid again the moment the connection is, and no
/// separate acknowledgement or retry queue is needed.
///
/// The expiry is recomputed on every registration (see <see cref="GatewaySessionKey.Lifetime"/>), so a
/// session that lives for days keeps a valid key while its Director is connected, and a key whose
/// Director died lapses on its own.
/// </summary>
public sealed class SessionGatewayKeys
{
    private readonly ConcurrentDictionary<Guid, string> _hashBySession = new();
    private readonly Func<DateTime> _clock;

    /// <param name="clock">UTC clock seam for tests; production omits it.</param>
    public SessionGatewayKeys(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>How many live sessions hold a key. Diagnostics and tests.</summary>
    public int Count => _hashBySession.Count;

    /// <summary>
    /// Mint this session's Gateway key and record its hash. Returns the RAW key - the only time it is ever
    /// returned - for the caller to stamp into the session's environment.
    ///
    /// Minting twice for one session replaces the recorded hash, which ends the previous key at the next
    /// registration. That is the correct behaviour for the one case it happens in (a session's environment
    /// being built again), and it is why the store is keyed by session rather than appending.
    /// </summary>
    public string Mint(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A session id is required to mint a Gateway session key.", nameof(sessionId));

        var key = GatewaySessionKey.Mint();
        _hashBySession[sessionId] = GatewaySessionKey.Hash(key);
        FileLog.Write($"[SessionGatewayKeys] Mint: session={sessionId} (key value never logged, and not retained here)");
        return key;
    }

    /// <summary>
    /// The registration message for one session, or null when it holds no key. The expiry is computed
    /// FRESH on every call, so a re-registration extends the key rather than re-sending a lapsing one.
    /// </summary>
    public SessionKeyRegistration? RegistrationFor(Guid sessionId)
        => _hashBySession.TryGetValue(sessionId, out var hash)
            ? new SessionKeyRegistration
            {
                SessionId = sessionId.ToString(),
                KeyHash = hash,
                ExpiresAtUtc = _clock() + GatewaySessionKey.Lifetime,
            }
            : null;

    /// <summary>Registrations for every session that holds a key - what a tunnel reseed re-sends.</summary>
    public List<SessionKeyRegistration> LiveRegistrations()
    {
        var expires = _clock() + GatewaySessionKey.Lifetime;
        return _hashBySession
            .Select(pair => new SessionKeyRegistration
            {
                SessionId = pair.Key.ToString(),
                KeyHash = pair.Value,
                ExpiresAtUtc = expires,
            })
            .ToList();
    }

    /// <summary>
    /// Drop a reaped session's record so the Director stops re-registering it. This is HOUSEKEEPING on this
    /// machine and is NOT the revocation - the credential is ended on the Gateway, which is the only place
    /// that can refuse it. Forgetting here without revoking there would leave a working key behind for a
    /// session that no longer exists.
    /// </summary>
    public bool Forget(Guid sessionId)
    {
        var removed = _hashBySession.TryRemove(sessionId, out _);
        if (removed)
            FileLog.Write($"[SessionGatewayKeys] Forget: session={sessionId}");
        return removed;
    }
}
