using CcDirector.Core.Account;
using CcDirector.Gateway.Account;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Proves the Gateway account-membership check (issue #1079): a presented, already-authorization-valid
/// cloud token is accepted only when its subject matches the account THIS Gateway is signed in to.
/// A valid token minted for a different account is refused, and a signed-out Gateway accepts nothing.
///
/// The credential service under test is built over an in-memory token store and a refresher that
/// THROWS if it is ever called, so these tests run cross-platform, use no HTTP handler, and
/// demonstrate that the membership check makes no network call - if it did, the throwing refresher
/// would fail the test.
/// </summary>
public sealed class GatewayAccountMembershipTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 6, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _tempDir;
    private readonly string _authEventsPath;

    public GatewayAccountMembershipTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-gw-membership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _authEventsPath = Path.Combine(_tempDir, "devthrottle-auth-events.jsonl");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// Builds the credential service over an in-memory store and a throwing refresher (no network),
    /// signed in as <paramref name="ownSubject"/> when it is provided, or signed out when it is null.
    /// </summary>
    private DevThrottleAccountService MakeAccount(string? ownSubject)
    {
        var store = new InMemoryTokenStore();
        var validator = new JwtAccessTokenValidator("membership-test-unused-secret");
        var eventLog = new AuthEventLog(_authEventsPath);
        var service = new DevThrottleAccountService(store, validator, eventLog, new ThrowingTokenRefresher());

        if (ownSubject is not null)
        {
            var ownToken = GatewayTestJwt.CreateWithSubject(Now.AddHours(1), ownSubject);
            service.StoreTokens(new DevThrottleTokens(ownToken, "own-refresh-token"));
        }

        return service;
    }

    /// <summary>A presented token that passed #1074 authorization validation, carrying its subject.</summary>
    private static AuthorizationTokenValidation ValidPresentedToken(string subject) =>
        new(IsValid: true, Subject: subject, ExpiresAtUtc: Now.AddHours(1), InvalidReason: null);

    // Acceptance criterion 1: Gateway signed in as account A, a token whose subject is A -> accept.
    [Fact]
    public void Check_TokenForSameAccount_IsMember()
    {
        var membership = new GatewayAccountMembership(MakeAccount(ownSubject: "account-A"));

        var result = membership.Check(ValidPresentedToken("account-A"));

        Assert.True(result.IsMember);
    }

    // Acceptance criterion 2: Gateway signed in as account A, a token whose subject is B -> refuse.
    [Fact]
    public void Check_TokenForDifferentAccount_IsNotMember()
    {
        var membership = new GatewayAccountMembership(MakeAccount(ownSubject: "account-A"));

        var result = membership.Check(ValidPresentedToken("account-B"));

        Assert.False(result.IsMember);
    }

    // Acceptance criterion 3: Gateway has no signed-in identity -> any token is refused.
    [Fact]
    public void Check_GatewayNotSignedIn_IsNotMember()
    {
        var membership = new GatewayAccountMembership(MakeAccount(ownSubject: null));

        var result = membership.Check(ValidPresentedToken("account-A"));

        Assert.False(result.IsMember);
    }

    // Defence in depth: a presented result that is not authorization-valid can never be a member.
    [Fact]
    public void Check_PresentedTokenNotAuthorizationValid_IsNotMember()
    {
        var membership = new GatewayAccountMembership(MakeAccount(ownSubject: "account-A"));

        var invalid = new AuthorizationTokenValidation(
            IsValid: false, Subject: null, ExpiresAtUtc: null, InvalidReason: "audience mismatch");
        var result = membership.Check(invalid);

        Assert.False(result.IsMember);
    }

    // No personally identifiable information in the accept/refuse reason: neither the account subject
    // nor an email marker appears in the reason string for any decision (accept, mismatch, signed-out).
    [Fact]
    public void Check_ReasonStrings_CarryNoPersonallyIdentifyingInformation()
    {
        const string ownSubject = "account-A-1a2b3c";
        const string otherSubject = "account-B-9x8y7z";

        var signedIn = new GatewayAccountMembership(MakeAccount(ownSubject));
        var accept = signedIn.Check(ValidPresentedToken(ownSubject));
        var mismatch = signedIn.Check(ValidPresentedToken(otherSubject));

        var signedOut = new GatewayAccountMembership(MakeAccount(ownSubject: null));
        var noAccount = signedOut.Check(ValidPresentedToken(ownSubject));

        foreach (var reason in new[] { accept.Reason, mismatch.Reason, noAccount.Reason })
        {
            Assert.DoesNotContain(ownSubject, reason, StringComparison.Ordinal);
            Assert.DoesNotContain(otherSubject, reason, StringComparison.Ordinal);
            Assert.DoesNotContain("@", reason, StringComparison.Ordinal);
        }
    }

    /// <summary>In-memory <see cref="IProtectedTokenStore"/> so the check runs without the Windows-only store.</summary>
    private sealed class InMemoryTokenStore : IProtectedTokenStore
    {
        private DevThrottleTokens? _tokens;
        public bool HasTokens => _tokens is not null;
        public void Save(DevThrottleTokens tokens) => _tokens = tokens;
        public DevThrottleTokens? Load() => _tokens;
        public void Clear() => _tokens = null;
    }

    /// <summary>
    /// A refresher that throws if invoked. The membership check reads the cached credential only, so it
    /// must never reach the network seam - if it did, this would fail the test (proves no network call).
    /// </summary>
    private sealed class ThrowingTokenRefresher : ITokenRefresher
    {
        public Task<TokenRefreshResult> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
            throw new InvalidOperationException("The membership check must not make a network call");
    }
}
