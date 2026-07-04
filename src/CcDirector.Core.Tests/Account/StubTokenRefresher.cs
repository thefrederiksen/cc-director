using CcDirector.Core.Account;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Stub <see cref="ITokenRefresher"/> for unit tests. Returns a configured token pair to simulate a
/// successful backend exchange, null to simulate the exchange being unavailable (offline / backend
/// error), a definitive rejection (revoked session, issue #876) via <see cref="Rejecting"/>, or a
/// persistent apikey misconfiguration (issue #911) via <see cref="Misconfigured"/>. Records whether it
/// was called and the refresh token it received.
/// </summary>
internal sealed class StubTokenRefresher : ITokenRefresher
{
    private readonly TokenRefreshResult _result;

    public StubTokenRefresher(DevThrottleTokens? result)
    {
        _result = result is null ? TokenRefreshResult.Unavailable : TokenRefreshResult.Success(result);
    }

    private StubTokenRefresher(TokenRefreshResult result)
    {
        _result = result;
    }

    /// <summary>A stub whose exchange definitively rejects the refresh token (revoked session).</summary>
    public static StubTokenRefresher Rejecting() => new(TokenRefreshResult.Rejected);

    /// <summary>A stub whose exchange is persistently misconfigured (missing/invalid apikey, issue #911).</summary>
    public static StubTokenRefresher Misconfigured() => new(TokenRefreshResult.Misconfigured);

    public bool WasCalled { get; private set; }
    public string? ReceivedRefreshToken { get; private set; }

    public Task<TokenRefreshResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        WasCalled = true;
        ReceivedRefreshToken = refreshToken;
        return Task.FromResult(_result);
    }
}
