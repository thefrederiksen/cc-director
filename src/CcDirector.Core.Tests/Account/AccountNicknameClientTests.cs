using System.Net;
using System.Net.Http;
using System.Text;
using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Issue #1357: the account nickname client reads <c>GET /api/v1/account/nickname</c> with the account
/// JWT and parses the nickname out of the response. It accepts both the <c>{ data: { nickname } }</c>
/// envelope the account API uses for its other reads and a bare top-level <c>{ nickname }</c> shape, and
/// returns null when the account has no nickname set (the caller falls back to the email). These tests
/// prove the parse and the HTTP call without a network.
/// </summary>
public sealed class AccountNicknameClientTests
{
    [Fact]
    public void Parse_DataEnvelope_ReadsNickname()
        => Assert.Equal("Starlord", AccountNicknameClient.Parse("{\"data\":{\"nickname\":\"Starlord\"}}"));

    [Fact]
    public void Parse_TopLevel_ReadsNickname()
        => Assert.Equal("Starlord", AccountNicknameClient.Parse("{\"nickname\":\"Starlord\"}"));

    [Fact]
    public void Parse_TrimsWhitespace()
        => Assert.Equal("Ace", AccountNicknameClient.Parse("{\"data\":{\"nickname\":\"  Ace  \"}}"));

    [Theory]
    [InlineData("{}")]                                   // no nickname anywhere
    [InlineData("{\"data\":{}}")]                        // envelope present, no nickname
    [InlineData("{\"data\":{\"nickname\":null}}")]       // explicit null
    [InlineData("{\"nickname\":\"\"}")]                  // empty string
    [InlineData("{\"nickname\":\"   \"}")]               // whitespace only
    public void Parse_Unset_ReturnsNull(string body)
        => Assert.Null(AccountNicknameClient.Parse(body));

    [Theory]
    [InlineData("[]")]                                   // not an object
    [InlineData("\"just-a-string\"")]                    // not an object
    public void Parse_NotAnObject_Throws(string body)
        => Assert.Throws<InvalidOperationException>(() => AccountNicknameClient.Parse(body));

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public string? SeenAuth { get; private set; }
        public string? SeenUrl { get; private set; }
        public StubHandler(HttpStatusCode status, string body) { _status = status; _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            SeenAuth = request.Headers.Authorization?.ToString();
            SeenUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task GetNicknameAsync_Success_ReturnsNickname_AndBearsTheJwt()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"data\":{\"nickname\":\"Starlord\"}}");
        var client = new AccountNicknameClient(new HttpClient(handler), baseUrl: "https://example.test");

        var nickname = await client.GetNicknameAsync("the.jwt");

        Assert.Equal("Starlord", nickname);
        Assert.Equal("Bearer the.jwt", handler.SeenAuth);
        Assert.Equal("https://example.test/api/v1/account/nickname", handler.SeenUrl);
    }

    [Fact]
    public async Task GetNicknameAsync_Unset_ReturnsNull()
    {
        var client = new AccountNicknameClient(new HttpClient(new StubHandler(HttpStatusCode.OK, "{\"data\":{}}")), baseUrl: "https://example.test");
        Assert.Null(await client.GetNicknameAsync("jwt"));
    }

    [Fact]
    public async Task GetNicknameAsync_Non2xx_Throws()
    {
        var client = new AccountNicknameClient(new HttpClient(new StubHandler(HttpStatusCode.Unauthorized, "{}")), baseUrl: "https://example.test");
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetNicknameAsync("jwt"));
    }

    [Fact]
    public async Task GetNicknameAsync_NoToken_Throws()
    {
        var client = new AccountNicknameClient(new HttpClient(new StubHandler(HttpStatusCode.OK, "{}")), baseUrl: "https://example.test");
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetNicknameAsync(""));
    }
}
