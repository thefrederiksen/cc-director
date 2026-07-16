using System.Net;
using System.Text;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// The Director-side store decides WHOSE text reaches the agent, reading a local cache of the
/// GATEWAY-OWNED value. These tests are about that decision and the one rule that matters most: when the
/// user has declined our text, no failure path may quietly put it back.
///
/// The cache is a temp file, never the real one, so a test never depends on how the developer's machine
/// is configured. RefreshAsync is driven against a stub Gateway so the download-and-cache path is
/// exercised without a network.
/// </summary>
public sealed class InjectedTextStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _cachePath;

    public InjectedTextStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-injected-text-tests", Guid.NewGuid().ToString("N"));
        _cachePath = Path.Combine(_dir, "injected-text-cache.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private InjectedTextStore NewStore() => new(_cachePath);

    private void SeedCache(bool useYours, string? yours)
        => new InjectedTextStore(_cachePath).WriteCache(
            new InjectedTextCacheEntry(useYours, yours, DateTime.UtcNow));

    // BuildForSession collapses whitespace-only text to nothing, which is correct for a user who cleared
    // their version - but it means a shipped default that ever rendered to whitespace would silently
    // inject NOTHING into every agent, everywhere, with no error. That cannot happen today and this test
    // is why it stays that way: the assumption is load-bearing, so it is asserted rather than trusted.
    [Fact]
    public void OurDefault_NeverRendersToNothing()
    {
        var text = FleetPreamble.BuildForSession(
            "a3dfb85e-49dd-442a-9e36-40fc44838783", "devthrottle", "MACHINE_A", @"C:\repos\devthrottle",
            user: null, store: InjectedTextStore.AlwaysOurs(_dir));

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("cc-devthrottle", text);
    }

    // A Director that has never reached the Gateway (no cache) injects our text - the same thing it did
    // before this setting existed. That is the documented default, not a fallback over a user's choice.
    [Fact]
    public void NoCacheYet_RunsOurText()
    {
        var store = NewStore();

        Assert.Equal(InjectedTextSource.Ours, store.ActiveSource());
        Assert.Equal(FleetPreambleTemplate.Default, store.ActiveTemplate());
    }

    [Fact]
    public void CacheSaysOurs_RunsOurText_EvenWhenItCarriesTheUsersText()
    {
        // use_yours=false, but the user's text is still cached (kept for when they switch back). Ours is
        // live, so ours is what launches.
        SeedCache(useYours: false, yours: "my own text");
        var store = NewStore();

        Assert.Equal(InjectedTextSource.Ours, store.ActiveSource());
        Assert.Equal(FleetPreambleTemplate.Default, store.ActiveTemplate());
    }

    [Fact]
    public void CacheSaysYours_RunsTheirText()
    {
        SeedCache(useYours: true, yours: "just my words, [SESSION_ID]");
        var store = NewStore();

        Assert.Equal(InjectedTextSource.Yours, store.ActiveSource());
        Assert.Equal("just my words, [SESSION_ID]", store.ActiveTemplate());
    }

    // The user's right to inject nothing at all: empty custom text is honoured, not treated as an error.
    [Fact]
    public void CacheSaysYoursButEmpty_InjectsNothing()
    {
        SeedCache(useYours: true, yours: "");
        var store = NewStore();

        Assert.Equal(InjectedTextSource.Yours, store.ActiveSource());
        Assert.Equal("", store.ActiveTemplate());
    }

    // THE RULE THIS FEATURE EXISTS FOR. The cache says the user's text is live but carries none at all
    // (a broken/partial cache). We must NOT hand the agent ours instead - that would silently inject the
    // policy they declined. It fails loudly. Empty is honoured (above); absent fails here.
    [Fact]
    public void CacheSaysYoursButTextIsAbsent_FailsLoudly_AndNeverSubstitutesOurs()
    {
        SeedCache(useYours: true, yours: null);
        var store = NewStore();

        Assert.Equal(InjectedTextSource.Yours, store.ActiveSource());
        var ex = Assert.Throws<InjectedTextUnavailableException>(() => store.ActiveTemplate());

        Assert.DoesNotContain("NEVER SIGN IT", ex.Message);
        Assert.Contains("you turned that off", ex.Message);
    }

    [Fact]
    public async Task RefreshAsync_DownloadsAndCachesTheGatewayValue()
    {
        const string body =
            "{\"use_yours\":true,\"yours\":\"downloaded [SESSION_ID]\",\"ours\":\"ignored\"}";
        var store = new InjectedTextStore(
            _cachePath, new HttpClient(new StubHandler(HttpStatusCode.OK, body)), gatewayUrl: "http://gw.test");

        await store.RefreshAsync();

        Assert.Equal(InjectedTextSource.Yours, store.ActiveSource());
        Assert.Equal("downloaded [SESSION_ID]", store.ActiveTemplate());
    }

    [Fact]
    public async Task RefreshAsync_NoGatewayConfigured_KeepsTheLastKnownCache()
    {
        SeedCache(useYours: true, yours: "last known");
        // Empty gateway url override => no-op refresh, cache untouched.
        var store = new InjectedTextStore(_cachePath, gatewayUrl: "");

        await store.RefreshAsync();

        Assert.Equal("last known", store.ActiveTemplate());
    }

    // The refresh must carry the fleet token: the Gateway auth gate rejects an unauthenticated
    // /gateway/* call, so without this the cache would never warm on a secured Gateway.
    [Fact]
    public async Task RefreshAsync_AttachesTheFleetToken()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"use_yours\":false,\"yours\":null,\"ours\":\"x\"}");
        var store = new InjectedTextStore(
            _cachePath, new HttpClient(handler), gatewayUrl: "http://gw.test", token: "secret-token");

        await store.RefreshAsync();

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization?.Scheme);
        Assert.Equal("secret-token", handler.LastRequest.Headers.Authorization?.Parameter);
    }

    /// <summary>A one-response HTTP handler so RefreshAsync can be driven without a real Gateway. Captures
    /// the request so a test can assert what the refresh actually sent (e.g. the auth header).</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
