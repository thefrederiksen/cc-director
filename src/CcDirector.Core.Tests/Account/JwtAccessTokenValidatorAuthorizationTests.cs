using System.Diagnostics.CodeAnalysis;
using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Proves the authorization-mode validation added for epic #1069 (issue #1074): on top of the
/// signature and <c>exp</c> checks that <see cref="JwtAccessTokenValidator.Validate"/> performs,
/// <see cref="JwtAccessTokenValidator.ValidateForAuthorization"/> also enforces the audience,
/// issuer, and not-before claims and exposes the subject. Every check runs entirely locally against
/// a freshly-generated ES256 signing key - there is no HTTP handler configured and the validator has
/// no network dependency, so these tests also demonstrate that validation makes no cloud call.
/// </summary>
public sealed class JwtAccessTokenValidatorAuthorizationTests : IDisposable
{
    private const string ExpectedAudience = "authenticated";
    private const string ExpectedIssuer = "https://devthrottle.example.supabase.co/auth/v1";

    private static readonly DateTime Now = new(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc);

    private readonly Es256TestKey _key = new();

    public void Dispose() => _key.Dispose();

    private JwtAccessTokenValidator MakeValidator() =>
        new(TestJwt.SigningSecret, new FakeTimeProvider(Now), _key.PublicKeySetJson(),
            expectedAudience: ExpectedAudience, expectedIssuer: ExpectedIssuer);

    private JwtAccessTokenValidator MakeEs256OnlyValidator() =>
        new(TestJwt.SigningSecret, new FakeTimeProvider(Now), _key.PublicKeySetJson(),
            expectedAudience: ExpectedAudience, expectedIssuer: ExpectedIssuer, allowSymmetricHs256: false);

    [Fact]
    public void ValidateForAuthorization_Hs256_WhenEs256Only_IsNotValid()
    {
        // Algorithm-confusion defense: an HS256 token signed with the (possibly public placeholder) secret
        // must be REFUSED by an ES256-only validator, so a forged symmetric token cannot authorize as any
        // subject. This is the validator-level fix every hosted account-token authorization path inherits.
        var forged = TestJwt.CreateAuthorization(Now.AddHours(1), ExpectedAudience, ExpectedIssuer, "attacker",
            TestJwt.SigningSecret);

        var result = MakeEs256OnlyValidator().ValidateForAuthorization(forged);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateForAuthorization_Hs256_WhenSymmetricAllowed_IsValid()
    {
        // Self-host / legacy is UNCHANGED: with HS256 allowed (the default), a properly-signed HS256 token
        // still validates. Only the hosted authorization validators opt into ES256-only.
        var token = TestJwt.CreateAuthorization(Now.AddHours(1), ExpectedAudience, ExpectedIssuer, "account-42",
            TestJwt.SigningSecret);

        var result = MakeValidator().ValidateForAuthorization(token);

        Assert.True(result.IsValid);
        Assert.Equal("account-42", result.Subject);
    }

    [Fact]
    public void ValidateForAuthorization_FullyValidToken_IsValidAndExposesSubject()
    {
        var validator = MakeValidator();
        var token = _key.CreateAuthorizationToken(
            expiresAtUtc: Now.AddHours(1),
            audience: ExpectedAudience,
            issuer: ExpectedIssuer,
            subject: "account-42",
            notBeforeUtc: Now.AddMinutes(-5));

        var result = validator.ValidateForAuthorization(token);

        Assert.True(result.IsValid);
        Assert.Equal("account-42", result.Subject);
        Assert.Null(result.InvalidReason);
    }

    [Fact]
    public void ValidateForAuthorization_WrongAudience_IsNotValid()
    {
        var validator = MakeValidator();
        var token = _key.CreateAuthorizationToken(
            expiresAtUtc: Now.AddHours(1),
            audience: "some-other-audience",
            issuer: ExpectedIssuer);

        var result = validator.ValidateForAuthorization(token);

        Assert.False(result.IsValid);
        Assert.Null(result.Subject);
    }

    [Fact]
    public void ValidateForAuthorization_WrongIssuer_IsNotValid()
    {
        var validator = MakeValidator();
        var token = _key.CreateAuthorizationToken(
            expiresAtUtc: Now.AddHours(1),
            audience: ExpectedAudience,
            issuer: "https://attacker.example.com/auth/v1");

        var result = validator.ValidateForAuthorization(token);

        Assert.False(result.IsValid);
        Assert.Null(result.Subject);
    }

    [Fact]
    public void ValidateForAuthorization_Expired_IsNotValid()
    {
        var validator = MakeValidator();
        var token = _key.CreateAuthorizationToken(
            expiresAtUtc: Now.AddHours(-1),
            audience: ExpectedAudience,
            issuer: ExpectedIssuer);

        var result = validator.ValidateForAuthorization(token);

        Assert.False(result.IsValid);
        Assert.Null(result.Subject);
    }

    [Fact]
    public void ValidateForAuthorization_NotYetValid_IsNotValid()
    {
        var validator = MakeValidator();
        var token = _key.CreateAuthorizationToken(
            expiresAtUtc: Now.AddHours(1),
            audience: ExpectedAudience,
            issuer: ExpectedIssuer,
            notBeforeUtc: Now.AddMinutes(30));

        var result = validator.ValidateForAuthorization(token);

        Assert.False(result.IsValid);
        Assert.Null(result.Subject);
    }

    [Fact]
    public void ValidateForAuthorization_ValidTokenWithAudienceArrayContainingExpected_IsValid()
    {
        // Supabase can mint the audience as an array; the expected value appearing in it is accepted.
        var validator = MakeValidator();
        var token = _key.CreateAuthorizationToken(
            expiresAtUtc: Now.AddHours(1),
            audience: null,
            issuer: ExpectedIssuer,
            audienceValues: new[] { "another-service", ExpectedAudience });

        var result = validator.ValidateForAuthorization(token);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateForAuthorization_TamperedSignature_IsNotValid()
    {
        var validator = MakeValidator();
        var token = _key.CreateAuthorizationToken(
            expiresAtUtc: Now.AddHours(1),
            audience: ExpectedAudience,
            issuer: ExpectedIssuer);
        var parts = token.Split('.');
        var signature = parts[2];
        var tamperedSignature = (signature[0] == 'A' ? 'B' : 'A') + signature[1..];
        var tampered = $"{parts[0]}.{parts[1]}.{tamperedSignature}";

        var result = validator.ValidateForAuthorization(tampered);

        Assert.False(result.IsValid);
        Assert.Null(result.Subject);
    }

    [Fact]
    public void ValidateForAuthorization_WithoutExpectedAudienceOrIssuerConfigured_Throws()
    {
        // Authorization-mode validation fails loud when the expected values are not configured,
        // rather than silently skipping the audience/issuer checks (no fallback programming).
        var validator = new JwtAccessTokenValidator(
            TestJwt.SigningSecret, new FakeTimeProvider(Now), _key.PublicKeySetJson());
        var token = _key.CreateAuthorizationToken(Now.AddHours(1), ExpectedAudience, ExpectedIssuer);

        Assert.Throws<InvalidOperationException>(() => validator.ValidateForAuthorization(token));
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by tests via MakeValidator.")]
    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTime nowUtc) => _now = new DateTimeOffset(nowUtc, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
