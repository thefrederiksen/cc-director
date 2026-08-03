using CcDirector.ControlApi;
using CcDirector.Core.Security;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The DIRECTOR's half of the session credential (Remove-the-network-port mission, phase 1b).
///
/// The property worth pinning here is what this store does NOT hold. A Director that kept every live
/// session's raw key would be a single place from which any session on the machine could be impersonated -
/// the exact concentration this phase exists to remove - so the raw key is returned once, to the caller
/// that stamps it into one session's environment, and only its hash is retained.
/// </summary>
public sealed class SessionGatewayKeysTests
{
    [Fact]
    public void The_raw_key_is_returned_once_and_never_retained()
    {
        var keys = new SessionGatewayKeys();
        var session = Guid.NewGuid();

        var key = keys.Mint(session);
        var registration = keys.RegistrationFor(session);

        Assert.NotNull(registration);
        // The registration - the ONLY thing that leaves this machine - carries the hash and not the key.
        Assert.Equal(GatewaySessionKey.Hash(key), registration!.KeyHash);
        Assert.NotEqual(key, registration.KeyHash);
        Assert.Equal(session.ToString(), registration.SessionId);
    }

    [Fact]
    public void Each_session_gets_its_own_key()
    {
        var keys = new SessionGatewayKeys();
        var a = keys.Mint(Guid.NewGuid());
        var b = keys.Mint(Guid.NewGuid());

        Assert.NotEqual(a, b);
        Assert.Equal(2, keys.Count);
    }

    [Fact]
    public void A_session_that_holds_no_key_has_no_registration()
        => Assert.Null(new SessionGatewayKeys().RegistrationFor(Guid.NewGuid()));

    [Fact]
    public void Minting_again_for_one_session_replaces_its_key_rather_than_adding_a_second()
    {
        var keys = new SessionGatewayKeys();
        var session = Guid.NewGuid();

        keys.Mint(session);
        var second = keys.Mint(session);

        Assert.Equal(1, keys.Count);
        Assert.Equal(GatewaySessionKey.Hash(second), keys.RegistrationFor(session)!.KeyHash);
    }

    [Fact]
    public void Every_live_session_rides_the_reseed()
    {
        var keys = new SessionGatewayKeys();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        keys.Mint(first);
        keys.Mint(second);

        var registrations = keys.LiveRegistrations();

        Assert.Equal(2, registrations.Count);
        Assert.Contains(registrations, r => r.SessionId == first.ToString());
        Assert.Contains(registrations, r => r.SessionId == second.ToString());
    }

    [Fact]
    public void A_forgotten_session_stops_riding_the_reseed()
    {
        // This is half of the reaping pair. Without it, a reaped session would be re-registered on the next
        // reseed and its key - just revoked - would be handed back to the Gateway.
        var keys = new SessionGatewayKeys();
        var session = Guid.NewGuid();
        keys.Mint(session);

        Assert.True(keys.Forget(session));

        Assert.Empty(keys.LiveRegistrations());
        Assert.Null(keys.RegistrationFor(session));
        // Forgetting twice is not an error, and reports that there was nothing left to forget - which is
        // what stops the host sending a second revocation for a session it has already ended.
        Assert.False(keys.Forget(session));
    }

    [Fact]
    public void The_expiry_is_recomputed_on_every_registration_so_a_reseed_extends_the_key()
    {
        // A long-lived session must never lose its key to the backstop expiry while its Director is
        // connected. Re-sending a registration with the ORIGINAL expiry would let exactly that happen.
        var now = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);
        var clockNow = now;
        var keys = new SessionGatewayKeys(() => clockNow);
        var session = Guid.NewGuid();
        keys.Mint(session);

        var atMint = keys.RegistrationFor(session)!.ExpiresAtUtc;
        clockNow = now.AddHours(6);
        var atReseed = keys.RegistrationFor(session)!.ExpiresAtUtc;

        Assert.Equal(now + GatewaySessionKey.Lifetime, atMint);
        Assert.Equal(now.AddHours(6) + GatewaySessionKey.Lifetime, atReseed);
        Assert.True(atReseed > atMint);
    }

    [Fact]
    public void An_empty_session_id_is_refused_rather_than_given_a_key()
        => Assert.Throws<ArgumentException>(() => new SessionGatewayKeys().Mint(Guid.Empty));
}
