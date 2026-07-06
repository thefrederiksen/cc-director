using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The outcome of validating a cached access token locally. <see cref="IsValid"/> is true only
/// for a correctly-signed, unexpired token; <see cref="IsExpiredButWellFormed"/> distinguishes a
/// token that is genuinely ours but past its expiry (renewable with the refresh token) from one
/// that is malformed or carries a wrong signature (never logged in).
/// </summary>
/// <param name="IsValid">True when the signature verifies and the token has not expired.</param>
/// <param name="IsExpiredButWellFormed">True when the signature verifies but the token is past its expiry.</param>
/// <param name="ExpiresAtUtc">The token's expiry instant, when present.</param>
public sealed record AccessTokenValidation(bool IsValid, bool IsExpiredButWellFormed, DateTime? ExpiresAtUtc);

/// <summary>
/// The outcome of validating a cached access token for AUTHORIZATION use - the stricter,
/// inbound-credential check (epic #1069). A token is <see cref="IsValid"/> only when its signature
/// verifies AND its audience, issuer, not-before, and expiry claims all pass AND it carries a
/// non-empty subject. On success <see cref="Subject"/> carries the token's stable account/user id
/// (the <c>sub</c> claim) so a later membership check can confirm the token belongs to this
/// Gateway's account. On failure <see cref="InvalidReason"/> records why, for diagnostics.
/// </summary>
/// <param name="IsValid">True only when signature, audience, issuer, not-before, expiry, and subject all pass.</param>
/// <param name="Subject">The token's <c>sub</c> (account/user id) claim on the valid case; null otherwise.</param>
/// <param name="ExpiresAtUtc">The token's expiry instant, when present.</param>
/// <param name="InvalidReason">A short reason for a not-valid result; null on the valid case.</param>
public sealed record AuthorizationTokenValidation(bool IsValid, string? Subject, DateTime? ExpiresAtUtc, string? InvalidReason);

/// <summary>
/// Validates a cached access token entirely locally - signature and expiry only - with no network
/// call. DevThrottle access tokens are Supabase JSON Web Tokens, and two signing schemes are
/// supported: ES256 (the current Supabase signing keys - an elliptic-curve P-256 signature verified
/// against the backend's published PUBLIC key set, see <see cref="DevThrottleSigningKeys"/>) and
/// HS256 (the legacy shared-secret scheme, kept for installs configured with the signing secret and
/// for the test seam). A token with a wrong or tampered signature, an unsupported algorithm, or a
/// malformed structure is reported as not valid (and not well-formed), so the logged-in check
/// treats it as not logged in.
/// </summary>
public sealed class JwtAccessTokenValidator
{
    private readonly byte[] _signingSecret;
    private readonly IReadOnlyList<VerificationKey> _publicKeys;
    private readonly TimeProvider _timeProvider;
    private readonly string? _expectedAudience;
    private readonly string? _expectedIssuer;

    /// <summary>One elliptic-curve P-256 public verification key from the configured key set.</summary>
    private sealed record VerificationKey(string? KeyId, ECParameters PublicKey);

    /// <summary>A token whose structure and signature verified, carrying its decoded payload JSON.</summary>
    private sealed record VerifiedToken(string? Algorithm, string PayloadJson);

    /// <summary>
    /// Creates the validator with the verification material for both supported signing schemes.
    /// </summary>
    /// <param name="signingSecret">The shared signing secret used to verify an HS256 token's signature.</param>
    /// <param name="timeProvider">Time source for expiry checks; defaults to the system clock. Injected so tests control "now".</param>
    /// <param name="publicKeySetJson">
    /// The JSON Web Key Set document holding the elliptic-curve P-256 PUBLIC keys used to verify an
    /// ES256 token's signature (see <see cref="DevThrottleSigningKeys.ResolvePublicKeySet"/>). Null
    /// or empty means no ES256 keys are configured, so every ES256 token is reported not valid. A
    /// malformed document throws - a broken key configuration must fail loud, not validate nothing.
    /// </param>
    /// <param name="expectedAudience">
    /// The audience (<c>aud</c>) value a token must carry to pass authorization-mode validation
    /// (see <see cref="ValidateForAuthorization"/>). Supplied as configuration, not hard-coded at the
    /// call site. Null when this validator is only used for the outbound-call check (<see cref="Validate"/>);
    /// authorization-mode validation then fails loud rather than silently skipping the audience check.
    /// </param>
    /// <param name="expectedIssuer">
    /// The issuer (<c>iss</c>) value a token must carry to pass authorization-mode validation. Supplied
    /// as configuration, not hard-coded at the call site. Null when only <see cref="Validate"/> is used.
    /// </param>
    public JwtAccessTokenValidator(
        string signingSecret,
        TimeProvider? timeProvider = null,
        string? publicKeySetJson = null,
        string? expectedAudience = null,
        string? expectedIssuer = null)
    {
        if (string.IsNullOrEmpty(signingSecret))
            throw new ArgumentException("Signing secret is required", nameof(signingSecret));

        _signingSecret = Encoding.UTF8.GetBytes(signingSecret);
        _publicKeys = ParsePublicKeySet(publicKeySetJson);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _expectedAudience = expectedAudience;
        _expectedIssuer = expectedIssuer;
    }

    /// <summary>
    /// Validates the access token's signature and expiry locally. Makes no network call. Returns a
    /// not-valid, not-well-formed result for a malformed token, an unsupported algorithm, or a wrong
    /// signature; a valid signature past expiry is reported well-formed-but-expired so the caller can
    /// renew it with the refresh token.
    /// </summary>
    public AccessTokenValidation Validate(string accessToken)
    {
        var verified = VerifySignature(accessToken);
        if (verified is null)
            return new AccessTokenValidation(IsValid: false, IsExpiredButWellFormed: false, ExpiresAtUtc: null);

        using var doc = JsonDocument.Parse(verified.PayloadJson);
        var expiresAtUtc = ReadUnixTimeClaim(doc.RootElement, "exp");
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var expired = expiresAtUtc is not null && expiresAtUtc.Value <= nowUtc;

        FileLog.Write($"[JwtAccessTokenValidator] Validate: signature OK ({verified.Algorithm}), expiresAtUtc={expiresAtUtc:o}, expired={expired}");
        return new AccessTokenValidation(IsValid: !expired, IsExpiredButWellFormed: expired, ExpiresAtUtc: expiresAtUtc);
    }

    /// <summary>
    /// Validates the access token for AUTHORIZATION use (epic #1069) entirely locally - no network
    /// call. This is the stricter, inbound-credential check: on top of the signature and <c>exp</c>
    /// expiry that <see cref="Validate"/> checks, it also honours the <c>nbf</c> not-before instant
    /// and requires the token's <c>aud</c> (audience) and <c>iss</c> (issuer) claims to match the
    /// expected values supplied at construction, so a correctly-signed token minted for a different
    /// audience or issuer is rejected. On success it returns the token's <c>sub</c> (account/user id)
    /// so a later membership check can confirm the token belongs to this Gateway's account.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The expected audience or issuer was not supplied at construction. Authorization-mode validation
    /// fails loud on a missing expected value rather than silently skipping the check.
    /// </exception>
    public AuthorizationTokenValidation ValidateForAuthorization(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(_expectedAudience) || string.IsNullOrWhiteSpace(_expectedIssuer))
            throw new InvalidOperationException(
                "ValidateForAuthorization requires the expected audience and issuer to be configured at construction");

        AuthorizationTokenValidation Reject(string reason)
        {
            FileLog.Write($"[JwtAccessTokenValidator] ValidateForAuthorization: not valid - {reason}");
            return new AuthorizationTokenValidation(IsValid: false, Subject: null, ExpiresAtUtc: null, InvalidReason: reason);
        }

        var verified = VerifySignature(accessToken);
        if (verified is null)
            return Reject("signature did not verify or the token is malformed");

        using var doc = JsonDocument.Parse(verified.PayloadJson);
        var root = doc.RootElement;
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        var expiresAtUtc = ReadUnixTimeClaim(root, "exp");
        if (expiresAtUtc is not null && expiresAtUtc.Value <= nowUtc)
            return Reject("token has expired (exp is in the past)");

        var notBeforeUtc = ReadUnixTimeClaim(root, "nbf");
        if (notBeforeUtc is not null && notBeforeUtc.Value > nowUtc)
            return Reject("token is not yet valid (nbf is in the future)");

        if (!AudienceMatches(root, _expectedAudience))
            return Reject("audience (aud) claim does not match the expected audience");

        var issuer = ReadStringClaim(root, "iss");
        if (!string.Equals(issuer, _expectedIssuer, StringComparison.Ordinal))
            return Reject("issuer (iss) claim does not match the expected issuer");

        var subject = ReadStringClaim(root, "sub");
        if (string.IsNullOrEmpty(subject))
            return Reject("subject (sub) claim is missing");

        FileLog.Write($"[JwtAccessTokenValidator] ValidateForAuthorization: token accepted ({verified.Algorithm}), expiresAtUtc={expiresAtUtc:o}");
        return new AuthorizationTokenValidation(IsValid: true, Subject: subject, ExpiresAtUtc: expiresAtUtc, InvalidReason: null);
    }

    /// <summary>
    /// Verifies the token's structure and signature and returns its decoded payload JSON on success,
    /// or null when the token is not a three-part JSON Web Token, its header is malformed, its
    /// algorithm is unsupported, or its signature does not verify. Both <see cref="Validate"/> and
    /// <see cref="ValidateForAuthorization"/> go through this single, fully-local signature path.
    /// </summary>
    private VerifiedToken? VerifySignature(string accessToken)
    {
        var parts = accessToken?.Split('.') ?? Array.Empty<string>();
        if (parts.Length != 3)
        {
            FileLog.Write("[JwtAccessTokenValidator] VerifySignature: not a three-part JSON Web Token");
            return null;
        }

        var header = ReadHeader(parts[0]);
        if (header is null)
        {
            FileLog.Write("[JwtAccessTokenValidator] VerifySignature: token header is malformed");
            return null;
        }

        var signatureVerifies = header.Value.Algorithm switch
        {
            "HS256" => HmacSignatureVerifies(parts[0], parts[1], parts[2]),
            "ES256" => EcdsaSignatureVerifies(header.Value.KeyId, parts[0], parts[1], parts[2]),
            _ => UnsupportedAlgorithm(header.Value.Algorithm),
        };

        if (!signatureVerifies)
            return null;

        var payloadJson = DecodeSegment(parts[1]);
        if (payloadJson is null)
        {
            FileLog.Write("[JwtAccessTokenValidator] VerifySignature: payload segment could not be decoded");
            return null;
        }

        return new VerifiedToken(header.Value.Algorithm, payloadJson);
    }

    /// <summary>
    /// Reads the token header's signing algorithm and optional key id. Returns null when the header
    /// segment is not valid base64url or not a JSON object - a malformed token, not an error.
    /// </summary>
    private static (string? Algorithm, string? KeyId)? ReadHeader(string encodedHeader)
    {
        var headerJson = DecodeSegment(encodedHeader);
        if (headerJson is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(headerJson);
            var algorithm = doc.RootElement.TryGetProperty("alg", out var alg) ? alg.GetString() : null;
            var keyId = doc.RootElement.TryGetProperty("kid", out var kid) ? kid.GetString() : null;
            return (algorithm, keyId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool UnsupportedAlgorithm(string? algorithm)
    {
        FileLog.Write($"[JwtAccessTokenValidator] Validate: unsupported signing algorithm '{algorithm}' (only ES256 and HS256 are supported)");
        return false;
    }

    private bool HmacSignatureVerifies(string encodedHeader, string encodedPayload, string encodedSignature)
    {
        var signingInput = Encoding.ASCII.GetBytes($"{encodedHeader}.{encodedPayload}");
        using var hmac = new HMACSHA256(_signingSecret);
        var expected = hmac.ComputeHash(signingInput);

        var actual = DecodeSegmentBytes(encodedSignature);
        if (actual is null)
            return false;

        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            FileLog.Write("[JwtAccessTokenValidator] Validate: HS256 signature does not verify (tampered or wrong secret)");
            return false;
        }

        return true;
    }

    private bool EcdsaSignatureVerifies(string? keyId, string encodedHeader, string encodedPayload, string encodedSignature)
    {
        if (_publicKeys.Count == 0)
        {
            FileLog.Write("[JwtAccessTokenValidator] Validate: ES256 token but no public key set is configured -> cannot verify");
            return false;
        }

        // An ES256 signature is the two 32-byte P-256 curve values concatenated (the JSON Web
        // Signature "R || S" form) - anything else cannot be a P-256 signature.
        var signature = DecodeSegmentBytes(encodedSignature);
        if (signature is null || signature.Length != 64)
        {
            FileLog.Write("[JwtAccessTokenValidator] Validate: ES256 signature is not a 64-byte P-256 signature");
            return false;
        }

        // A token that names its signing key is verified against that key only; a token without a
        // key id is verified against every configured key (verification either passes or it does not).
        var candidates = keyId is null
            ? _publicKeys
            : _publicKeys.Where(k => string.Equals(k.KeyId, keyId, StringComparison.Ordinal)).ToArray();
        if (candidates.Count == 0)
        {
            FileLog.Write($"[JwtAccessTokenValidator] Validate: the token's key id matches no configured public key (keyId={keyId})");
            return false;
        }

        var signingInput = Encoding.ASCII.GetBytes($"{encodedHeader}.{encodedPayload}");
        foreach (var candidate in candidates)
        {
            using var ecdsa = ECDsa.Create(candidate.PublicKey);
            if (ecdsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256))
                return true;
        }

        FileLog.Write("[JwtAccessTokenValidator] Validate: ES256 signature does not verify (tampered or wrong key)");
        return false;
    }

    /// <summary>
    /// Parses the JSON Web Key Set document into the elliptic-curve P-256 verification keys. Keys of
    /// any other type or curve are skipped (a published key set may carry keys for other purposes);
    /// a document that is not a key set, or an elliptic-curve key missing its coordinates, throws -
    /// a broken key configuration must fail loud at construction, not silently verify nothing.
    /// </summary>
    private static IReadOnlyList<VerificationKey> ParsePublicKeySet(string? publicKeySetJson)
    {
        if (string.IsNullOrWhiteSpace(publicKeySetJson))
            return Array.Empty<VerificationKey>();

        using var doc = JsonDocument.Parse(publicKeySetJson);
        if (!doc.RootElement.TryGetProperty("keys", out var keysElement) || keysElement.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("The public key set is not a JSON Web Key Set document (no \"keys\" array)", nameof(publicKeySetJson));

        var keys = new List<VerificationKey>();
        foreach (var key in keysElement.EnumerateArray())
        {
            var keyType = key.TryGetProperty("kty", out var kty) ? kty.GetString() : null;
            var curve = key.TryGetProperty("crv", out var crv) ? crv.GetString() : null;
            if (!string.Equals(keyType, "EC", StringComparison.Ordinal) || !string.Equals(curve, "P-256", StringComparison.Ordinal))
            {
                FileLog.Write($"[JwtAccessTokenValidator] ParsePublicKeySet: skipping a non-P-256 key (kty={keyType}, crv={curve})");
                continue;
            }

            var x = key.TryGetProperty("x", out var xElement) ? DecodeSegmentBytes(xElement.GetString() ?? string.Empty) : null;
            var y = key.TryGetProperty("y", out var yElement) ? DecodeSegmentBytes(yElement.GetString() ?? string.Empty) : null;
            if (x is null || y is null)
                throw new ArgumentException("An elliptic-curve key in the public key set is missing its x or y coordinate", nameof(publicKeySetJson));

            var keyId = key.TryGetProperty("kid", out var kid) ? kid.GetString() : null;
            keys.Add(new VerificationKey(keyId, new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = x, Y = y },
            }));
        }

        FileLog.Write($"[JwtAccessTokenValidator] ParsePublicKeySet: {keys.Count} P-256 verification key(s) configured");
        return keys;
    }

    /// <summary>
    /// Reads a Unix-time (seconds-since-epoch) claim such as <c>exp</c> or <c>nbf</c> from the payload,
    /// or null when it is absent or not a number.
    /// </summary>
    private static DateTime? ReadUnixTimeClaim(JsonElement payload, string claimName)
    {
        if (!payload.TryGetProperty(claimName, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;

        return DateTimeOffset.FromUnixTimeSeconds(value.GetInt64()).UtcDateTime;
    }

    /// <summary>Reads a string claim from the payload, or null when it is absent or not a string.</summary>
    private static string? ReadStringClaim(JsonElement payload, string claimName)
    {
        if (payload.TryGetProperty(claimName, out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString();

        return null;
    }

    /// <summary>
    /// Returns true when the payload's <c>aud</c> claim contains <paramref name="expectedAudience"/>.
    /// Per RFC 7519 the audience is either a single string or an array of strings; both shapes are
    /// accepted, matching case-sensitively (an ordinal comparison, as claim values are exact tokens).
    /// </summary>
    private static bool AudienceMatches(JsonElement payload, string expectedAudience)
    {
        if (!payload.TryGetProperty("aud", out var aud))
            return false;

        if (aud.ValueKind == JsonValueKind.String)
            return string.Equals(aud.GetString(), expectedAudience, StringComparison.Ordinal);

        if (aud.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in aud.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String
                    && string.Equals(entry.GetString(), expectedAudience, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    private static string? DecodeSegment(string segment)
    {
        var bytes = DecodeSegmentBytes(segment);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    private static byte[]? DecodeSegmentBytes(string segment)
    {
        // JSON Web Tokens use base64url without padding; restore standard base64 before decoding.
        var normalized = segment.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2: normalized += "=="; break;
            case 3: normalized += "="; break;
        }

        return Convert.TryFromBase64String(normalized, new byte[normalized.Length], out var written)
            ? Convert.FromBase64String(normalized)
            : null;
    }
}
