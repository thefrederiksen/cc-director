using System.Security.Cryptography;
using System.Text;

namespace CcDirector.Core.Security;

/// <summary>
/// The credential ONE session's agent presents to the Gateway.
///
/// The Director already mints a session-bound credential for its own Control API
/// (<see cref="DirectorScopedToken"/>), and that one needs no storage at all: the Director holds the
/// machine secret, so it can re-derive and verify the signature on every request. The Gateway cannot
/// do that. It does not hold the Director's machine secret and must never be given one - a shared
/// secret from which any session's credential can be derived would let a Gateway compromise mint
/// credentials for every session on every machine it serves.
///
/// So a Gateway session key is not derived from anything. It is 256 bits of randomness, minted by the
/// Director, handed to exactly one session, and recognised by the Gateway from a stored one-way HASH -
/// the same shape the per-device keys already use (<c>DeviceRegistry</c>). The RAW KEY NEVER LEAVES
/// THIS MACHINE except into the environment of the session it belongs to: the Director hashes it here
/// and registers only the hash, so the Gateway - hosted or self-hosted - never holds a value that
/// could be replayed as a session, and a stolen database yields nothing to present.
///
/// Both halves live here, in Core, so the minting side (the Director) and the verifying side (the
/// Gateway) hash with ONE function. Two implementations of "the hash of a key" that drift by a
/// trailing newline or an encoding do not fail loudly - they fail as an authentication that never
/// matches, which reads as a broken feature rather than a broken hash.
/// </summary>
public static class GatewaySessionKey
{
    /// <summary>
    /// How long a freshly registered session key is valid. The expiry is the BACKSTOP, not the primary
    /// end of life - a session key is revoked when its session is reaped, and re-registered (which
    /// extends the expiry) on every tunnel reseed while the session lives. It exists for the paths
    /// where no revocation is ever delivered: a Director that is killed, a machine that is unplugged,
    /// a tunnel that never comes back. Without it those keys would be accepted forever.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    /// <summary>
    /// Mint a new session key: 256 bits of cryptographic randomness, URL-safe base64, no padding. The
    /// same shape and strength as a per-device key, because it is the same kind of thing - a bearer
    /// credential recognised by its stored hash.
    /// </summary>
    public static string Mint()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// The lower-case hexadecimal SHA-256 of a session key - the ONLY form of the key that is ever
    /// sent to the Gateway, stored, or compared. Matches the device-credential hash format exactly
    /// (<c>Convert.ToHexString(SHA256(UTF8(key))).ToLowerInvariant()</c>) so both credential tables
    /// hash the same way and neither has a second private convention.
    /// </summary>
    public static string Hash(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("A session key is required to hash.", nameof(key));

        return Convert.ToHexString(HashBytes(key)).ToLowerInvariant();
    }

    /// <summary>
    /// The raw SHA-256 bytes of a session key, for a fixed-time comparison against a stored hash.
    /// Verification compares BYTES, never the hexadecimal strings: an ordinal string compare returns
    /// as soon as two characters differ, which tells anything that can time the request how much of
    /// the hash it guessed right.
    /// </summary>
    public static byte[] HashBytes(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("A session key is required to hash.", nameof(key));

        return SHA256.HashData(Encoding.UTF8.GetBytes(key));
    }
}
