using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CcDirector.Gateway.Tests;

/// <summary>A compact ES256 (P-256) signer that mints Supabase-shaped tokens and exports its public JWKS.</summary>
internal sealed class TestEs256Key : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private const string KeyId = "test-key";

    public void Dispose() => _key.Dispose();

    public string PublicKeySetJson()
    {
        var p = _key.ExportParameters(includePrivateParameters: false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new { alg = "ES256", crv = "P-256", kid = KeyId, kty = "EC", use = "sig",
                      x = B64(p.Q.X!), y = B64(p.Q.Y!) },
            },
        });
    }

    public string Token(string subject, string email, string audience, string issuer, bool includeExp = true)
    {
        var header = B64(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { alg = "ES256", typ = "JWT", kid = KeyId })));
        var signingInput = header + "." + PayloadSegment(subject, email, audience, issuer, includeExp);
        var sig = _key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        return signingInput + "." + B64(sig);
    }

    /// <summary>A forged HS256 token signed with <paramref name="secret"/> - to prove an ES256-only
    /// validator refuses symmetric tokens no matter the (possibly public/placeholder) secret.</summary>
    public static string Hs256Token(string secret, string subject, string audience, string issuer)
    {
        var header = B64(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" })));
        var signingInput = header + "." + PayloadSegment(subject, "x@x.com", audience, issuer, includeExp: true);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sig = hmac.ComputeHash(Encoding.ASCII.GetBytes(signingInput));
        return signingInput + "." + B64(sig);
    }

    private static string PayloadSegment(string subject, string email, string audience, string issuer, bool includeExp)
    {
        const long nowSeconds = 1_781_000_000L; // fixed instant; token is far from expiry at test time
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["email"] = email,
            ["aud"] = audience,
            ["iss"] = issuer,
            ["iat"] = nowSeconds,
            ["nbf"] = nowSeconds,
        };
        if (includeExp)
            claims["exp"] = 4_070_000_000L; // year ~2099
        return B64(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(claims)));
    }

    private static string B64(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
