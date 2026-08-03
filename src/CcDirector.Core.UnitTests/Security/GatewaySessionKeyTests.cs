using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Security;
using Xunit;

namespace CcDirector.Core.Tests.Security;

/// <summary>
/// The session key primitive (Remove-the-network-port mission, phase 1b).
///
/// It lives in Core because BOTH sides use it - the Director mints and hashes, the Gateway hashes to
/// verify - and two implementations of "the hash of a key" that drift by an encoding or a trailing
/// newline do not fail loudly. They fail as an authentication that never matches, which reads as a broken
/// feature rather than a broken hash. So the format is PINNED here, in the words of the algorithm rather
/// than by calling the code under test back on itself.
/// </summary>
public sealed class GatewaySessionKeyTests
{
    [Fact]
    public void A_minted_key_carries_256_bits_of_randomness()
    {
        var key = GatewaySessionKey.Mint();

        // URL-safe base64 of 32 bytes, padding stripped: 43 characters, and nothing that needs escaping in
        // an environment variable, an HTTP header, or a shell.
        Assert.Equal(43, key.Length);
        Assert.DoesNotContain('+', key);
        Assert.DoesNotContain('/', key);
        Assert.DoesNotContain('=', key);
    }

    [Fact]
    public void Two_minted_keys_are_never_the_same()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 500; i++)
            Assert.True(keys.Add(GatewaySessionKey.Mint()), "Mint produced a duplicate key");
    }

    [Fact]
    public void The_hash_is_the_lower_case_hexadecimal_SHA256_of_the_UTF8_key()
    {
        // Spelled out independently of the implementation. If the format ever changes, every already-stamped
        // session's key stops authenticating at once - so this is a contract, not an internal detail.
        var key = "a-known-session-key";
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

        Assert.Equal(expected, GatewaySessionKey.Hash(key));
        Assert.Equal(64, GatewaySessionKey.Hash(key).Length);
    }

    [Fact]
    public void The_hash_is_the_same_on_both_sides_of_the_wire()
    {
        // The Director hashes to register; the Gateway hashes to verify. Same input, same answer, every time.
        var key = GatewaySessionKey.Mint();
        Assert.Equal(GatewaySessionKey.Hash(key), GatewaySessionKey.Hash(key));
        Assert.NotEqual(GatewaySessionKey.Hash(key), GatewaySessionKey.Hash(GatewaySessionKey.Mint()));
    }

    [Fact]
    public void The_hash_bytes_and_the_hexadecimal_hash_are_the_same_value()
    {
        // Verification compares BYTES in fixed time; registration sends the hexadecimal string. If the two
        // ever described different values, every session key would fail to authenticate.
        var key = GatewaySessionKey.Mint();
        Assert.Equal(GatewaySessionKey.Hash(key), Convert.ToHexString(GatewaySessionKey.HashBytes(key)).ToLowerInvariant());
    }

    [Fact]
    public void Hashing_nothing_is_refused_rather_than_answered()
    {
        // A hash of "" is a perfectly good hash of nothing, and registering it would make the empty string a
        // working credential. Fail loud instead.
        Assert.Throws<ArgumentException>(() => GatewaySessionKey.Hash(""));
        Assert.Throws<ArgumentException>(() => GatewaySessionKey.Hash(null!));
        Assert.Throws<ArgumentException>(() => GatewaySessionKey.HashBytes(""));
    }

    [Fact]
    public void The_lifetime_is_a_backstop_measured_in_hours_not_days()
    {
        // The expiry exists for the path where no revocation is ever delivered - a Director that was killed,
        // a machine unplugged. A lifetime measured in days would leave those keys live for days.
        Assert.True(GatewaySessionKey.Lifetime > TimeSpan.FromHours(1));
        Assert.True(GatewaySessionKey.Lifetime <= TimeSpan.FromHours(24));
    }
}
