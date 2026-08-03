using System.Net;
using System.Text;
using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit coverage for the Gateway-side work-item resolver (issue #970). Proves the ported GitHub fetch
/// + flow-label mapping behaves exactly like the Cockpit's GitHubItemStatusClient did, using a stub
/// HTTP handler so no network or credentials file is touched. The token is supplied through the
/// injected provider - never read from a browser - which is the whole point of moving it here.
/// </summary>
public sealed class GitHubItemStatusResolverTests
{
    private static readonly Func<(string?, string?)> GoodToken = () => ("test-token", null);
    private static readonly Func<(string?, string?)> NoToken = () => (null, "GITHUB_TOKEN not configured.");

    // ---- flow:* label mapping (mirrors the Cockpit GitHubItemStatusClientTests, issue #275) ----

    [Fact]
    public void MapStatus_NoLabels_ReturnsQueued()
        => Assert.Equal(GatewayWorkItemStatus.Queued, GitHubItemStatusResolver.MapStatus(Array.Empty<string>()));

    [Theory]
    [InlineData("flow:ready-dev")]
    [InlineData("flow:in-progress")]
    [InlineData("flow:rejected")]
    public void MapStatus_NotYetDrained_ReturnsQueued(string label)
        => Assert.Equal(GatewayWorkItemStatus.Queued, GitHubItemStatusResolver.MapStatus(new[] { label }));

    [Theory]
    [InlineData("flow:ready-qa")]
    [InlineData("flow:qa-failed")]
    public void MapStatus_InLoop_ReturnsRunning(string label)
        => Assert.Equal(GatewayWorkItemStatus.Running, GitHubItemStatusResolver.MapStatus(new[] { label }));

    [Fact]
    public void MapStatus_Done_ReturnsDone()
        => Assert.Equal(GatewayWorkItemStatus.Done, GitHubItemStatusResolver.MapStatus(new[] { "flow:done" }));

    [Fact]
    public void MapStatus_NeedsHuman_ReturnsNeedsHuman()
        => Assert.Equal(GatewayWorkItemStatus.NeedsHuman, GitHubItemStatusResolver.MapStatus(new[] { "flow:needs-human" }));

    [Fact]
    public void MapStatus_Failed_ReturnsFailed()
        => Assert.Equal(GatewayWorkItemStatus.Failed, GitHubItemStatusResolver.MapStatus(new[] { "flow:failed" }));

    [Fact]
    public void MapStatus_DoneWinsOverRunning_ReturnsDone()
        => Assert.Equal(GatewayWorkItemStatus.Done, GitHubItemStatusResolver.MapStatus(new[] { "flow:ready-qa", "flow:done" }));

    [Fact]
    public void MapStatus_NeedsHumanWinsOverRunning_ReturnsNeedsHuman()
        => Assert.Equal(GatewayWorkItemStatus.NeedsHuman, GitHubItemStatusResolver.MapStatus(new[] { "flow:qa-failed", "flow:needs-human" }));

    [Fact]
    public void MapStatus_IgnoresUnrelatedLabels_ReturnsQueued()
        => Assert.Equal(GatewayWorkItemStatus.Queued, GitHubItemStatusResolver.MapStatus(new[] { "enhancement", "cockpit" }));

    [Fact]
    public void MapStatus_IsCaseInsensitive_ReturnsDone()
        => Assert.Equal(GatewayWorkItemStatus.Done, GitHubItemStatusResolver.MapStatus(new[] { "FLOW:DONE" }));

    // ---- ResolveAsync end to end over a stub GitHub ----

    [Fact]
    public async Task NonGithubSource_ResolvesQueued_WithoutAnyCall()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not call GitHub for a non-github source"));
        var resolver = NewResolver(handler, GoodToken);

        var info = await resolver.ResolveAsync("devops", "42");

        Assert.Equal(GatewayWorkItemStatus.Queued, info.Status);
        Assert.Null(info.Title);
        Assert.Contains("devops", info.Detail);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task MissingToken_ResolvesUnknown_WithoutAnyCall()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("must not call GitHub without a token"));
        var resolver = NewResolver(handler, NoToken);

        var info = await resolver.ResolveAsync("github", "970");

        Assert.Equal(GatewayWorkItemStatus.Unknown, info.Status);
        Assert.Equal("GITHUB_TOKEN not configured.", info.Detail);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GithubItem_WithFlowDone_ResolvesTitleAndDone()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{ "title": "Rebuild the Cockpit", "labels": [ { "name": "cockpit" }, { "name": "flow:done" } ] }"""));
        var resolver = NewResolver(handler, GoodToken);

        var info = await resolver.ResolveAsync("github", "967");

        Assert.Equal("Rebuild the Cockpit", info.Title);
        Assert.Equal(GatewayWorkItemStatus.Done, info.Status);
        Assert.Null(info.Detail);
    }

    [Fact]
    public async Task GithubItem_NoFlowLabel_ResolvesTitleAndQueued()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{ "title": "Some open issue", "labels": [ { "name": "enhancement" } ] }"""));
        var resolver = NewResolver(handler, GoodToken);

        var info = await resolver.ResolveAsync("github", "1");

        Assert.Equal("Some open issue", info.Title);
        Assert.Equal(GatewayWorkItemStatus.Queued, info.Status);
    }

    [Fact]
    public async Task GithubItem_SendsBearerToken_AndCanonicalRepoPath()
    {
        HttpRequestMessage? seen = null;
        var handler = new StubHandler(req => { seen = req; return Json(HttpStatusCode.OK, """{ "title": "x", "labels": [] }"""); });
        var resolver = NewResolver(handler, GoodToken, owner: "thefrederiksen", repo: "devthrottle");

        await resolver.ResolveAsync("github", "970");

        Assert.NotNull(seen);
        Assert.Equal("Bearer", seen!.Headers.Authorization?.Scheme);
        Assert.Equal("test-token", seen.Headers.Authorization?.Parameter);
        Assert.EndsWith("repos/thefrederiksen/devthrottle/issues/970", seen.RequestUri!.ToString());
    }

    [Fact]
    public async Task GithubItem_NotFound_ResolvesUnknownWithDetail()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var resolver = NewResolver(handler, GoodToken);

        var info = await resolver.ResolveAsync("github", "999999");

        Assert.Equal(GatewayWorkItemStatus.Unknown, info.Status);
        Assert.Null(info.Title);
        Assert.Contains("not found", info.Detail);
    }

    [Fact]
    public async Task GithubItem_ServerError_ResolvesUnknownWithDetail()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Forbidden, """{ "message": "rate limited" }"""));
        var resolver = NewResolver(handler, GoodToken);

        var info = await resolver.ResolveAsync("github", "970");

        Assert.Equal(GatewayWorkItemStatus.Unknown, info.Status);
        Assert.Contains("403", info.Detail);
    }

    [Fact]
    public async Task GithubUnreachable_ResolvesUnknown_NotThrow()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var resolver = NewResolver(handler, GoodToken);

        var info = await resolver.ResolveAsync("github", "970");

        Assert.Equal(GatewayWorkItemStatus.Unknown, info.Status);
        Assert.Contains("GitHub unreachable", info.Detail);
    }

    private static GitHubItemStatusResolver NewResolver(
        StubHandler handler, Func<(string?, string?)> token, string owner = "devthrottle", string repo = "devthrottle")
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return new GitHubItemStatusResolver(http, token, owner, repo);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public int CallCount { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_respond(request));
        }
    }
}
