using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Proves the work-item status endpoint is wired live on a real <see cref="GatewayHost"/> (issue
/// #970): the browser can resolve a work-list item's title + status through a same-origin Gateway
/// call, so the React Cockpit never holds the GitHub token. The deterministic cases use a non-github
/// source (no network, no credentials). A separate env-gated case resolves a real GitHub issue
/// through the live endpoint to capture the human proof transcript.
/// </summary>
public sealed class ItemStatusEndpointTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-itemstatus-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    [Fact]
    public async Task NonGithubSource_ResolvesQueued_SameOrigin_NoToken()
    {
        var resp = await _http.GetAsync("gateway/lists/item-status?source=devops&id=123");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("queued", root.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("title").ValueKind);
        Assert.Contains("devops", root.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData("gateway/lists/item-status?source=github")]
    [InlineData("gateway/lists/item-status?id=970")]
    [InlineData("gateway/lists/item-status")]
    public async Task MissingParameters_Return400(string url)
    {
        var resp = await _http.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    /// <summary>
    /// LIVE proof (issue #970): resolves a real GitHub issue through the running Gateway endpoint,
    /// with the token read on the Gateway from the shared credentials file. Runs only when
    /// DEVTHROTTLE_LIVE_GITHUB_PROOF is set to the output transcript path (kept offline/CI-safe by
    /// default). Set DEVTHROTTLE_GITHUB_OWNER / _REPO / a target id to point at the real repo.
    /// </summary>
    [Fact]
    public async Task Live_ResolvesRealGithubIssue_ThroughGateway()
    {
        var outPath = Environment.GetEnvironmentVariable("DEVTHROTTLE_LIVE_GITHUB_PROOF");
        if (string.IsNullOrWhiteSpace(outPath))
            return; // gated off by default

        var id = Environment.GetEnvironmentVariable("DEVTHROTTLE_LIVE_GITHUB_ID") ?? "967";
        var resp = await _http.GetAsync($"gateway/lists/item-status?source=github&id={id}");
        var body = await resp.Content.ReadAsStringAsync();

        var transcript =
            $"REQUEST: GET http://127.0.0.1:{_gateway.Port}/gateway/lists/item-status?source=github&id={id}\r\n" +
            $"(browser sends only the same-origin cc-gateway-token; the GitHub token stays on the Gateway)\r\n" +
            $"RESPONSE STATUS: {(int)resp.StatusCode}\r\n" +
            $"RESPONSE BODY: {body}\r\n";
        File.WriteAllText(outPath, transcript);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(body);
        // The response carries only title/status/detail - never the token.
        Assert.DoesNotContain("ghp_", body);
        Assert.DoesNotContain("github_pat_", body);
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("title").GetString()));
    }

}
