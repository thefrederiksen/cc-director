using System.Net;
using System.Net.Http;
using System.Text;
using CcDirector.Core.Account;
using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Issue #1357: the Director-side provider resolves the signed-in user (email + nickname) from the
/// Gateway's <c>GET /account/status</c>, caches it for a TTL so the hot preamble path does not re-hit the
/// Gateway, and exposes a synchronous last-known snapshot for the (non-blocking) Pi launch path. A fake
/// HTTP handler stands in for the Gateway so no real network call is made.
/// </summary>
public sealed class SignedInUserProviderTests
{
    private const string GatewayUrl = "http://127.0.0.1:7878";

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly string _body;
        public int Calls { get; private set; }
        public CountingHandler(string body) { _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>A test clock the tests advance to cross the cache TTL boundary deterministically.</summary>
    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 07, 11, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static SignedInUserProvider Make(CountingHandler handler, TimeProvider time)
        => new(
            () => new GatewayConfig { Url = GatewayUrl },
            new GatewayAccountStatusClient(new HttpClient(handler)),
            ttl: TimeSpan.FromMinutes(5),
            timeProvider: time);

    [Fact]
    public async Task ResolveAsync_SignedInWithNickname_MapsEmailAndNickname()
    {
        var handler = new CountingHandler("{\"signedIn\":true,\"email\":\"soren@example.com\",\"provider\":\"google\",\"nickname\":\"Starlord\"}");
        var provider = Make(handler, new MutableTimeProvider());

        var user = await provider.ResolveAsync();

        Assert.NotNull(user);
        Assert.Equal("soren@example.com", user!.Email);
        Assert.Equal("Starlord", user.Nickname);
        Assert.Equal("Starlord", user.DisplayName);
    }

    [Fact]
    public async Task ResolveAsync_SignedInNoNickname_EmailIsDisplayName()
    {
        var handler = new CountingHandler("{\"signedIn\":true,\"email\":\"soren@example.com\",\"provider\":\"google\"}");
        var provider = Make(handler, new MutableTimeProvider());

        var user = await provider.ResolveAsync();

        Assert.NotNull(user);
        Assert.Null(user!.Nickname);
        Assert.Equal("soren@example.com", user.DisplayName);
    }

    [Fact]
    public async Task ResolveAsync_SignedOut_ReturnsNull()
    {
        var handler = new CountingHandler("{\"signedIn\":false}");
        var provider = Make(handler, new MutableTimeProvider());

        Assert.Null(await provider.ResolveAsync());
    }

    [Fact]
    public async Task ResolveAsync_CachesWithinTtl_ThenRefreshesAfter()
    {
        var handler = new CountingHandler("{\"signedIn\":true,\"email\":\"soren@example.com\",\"provider\":\"google\",\"nickname\":\"Starlord\"}");
        var time = new MutableTimeProvider();
        var provider = Make(handler, time);

        await provider.ResolveAsync();
        await provider.ResolveAsync();
        Assert.Equal(1, handler.Calls); // second call served from cache

        time.Advance(TimeSpan.FromMinutes(6)); // past the 5-minute TTL
        await provider.ResolveAsync();
        Assert.Equal(2, handler.Calls); // stale -> one more fetch
    }

    [Fact]
    public async Task CurrentSnapshot_IsNullBeforeResolve_ThenLastResolved()
    {
        var handler = new CountingHandler("{\"signedIn\":true,\"email\":\"soren@example.com\",\"provider\":\"google\",\"nickname\":\"Starlord\"}");
        var provider = Make(handler, new MutableTimeProvider());

        Assert.Null(provider.CurrentSnapshot); // no network before a resolve
        Assert.Equal(0, handler.Calls);

        await provider.ResolveAsync();

        Assert.NotNull(provider.CurrentSnapshot);
        Assert.Equal("Starlord", provider.CurrentSnapshot!.Nickname);
    }
}
