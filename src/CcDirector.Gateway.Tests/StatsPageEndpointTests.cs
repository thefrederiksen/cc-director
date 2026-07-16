using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// HTTP wire tests for the DevThrottle Stats private dashboard (<c>GET /stats</c> and <c>/stats/data</c>).
/// Boots ONLY <see cref="StatsPageEndpoint"/> on an ephemeral port over a real aggregator seeded with a few
/// sessions, so it proves the always-available page and its JSON serve end-to-end - including under the
/// same host-wide <see cref="AuthMiddleware"/> the real Gateway applies.
/// </summary>
public sealed class StatsPageEndpointTests : IDisposable
{
    private const string GatewayToken = "test-gateway-token-for-stats";
    private readonly string _dir;

    public StatsPageEndpointTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-stats-ep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private static SessionDto Session(string id, params (string modality, string surface, long turns, long chars)[] buckets)
    {
        var dto = new SessionDto { SessionId = id, InputStats = new InputStatsDto() };
        foreach (var b in buckets)
            dto.InputStats!.Buckets.Add(new InputStatBucketDto { Modality = b.modality, Surface = b.surface, Turns = b.turns, Characters = b.chars });
        return dto;
    }

    private async Task<(WebApplication app, HttpClient http)> StartAsync(GatewayInputStatsAggregator agg, bool authEnabled)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        if (authEnabled)
        {
            var requireToken = new AuthMiddleware.RequireToken
            {
                Token = GatewayToken,
                Devices = new DeviceRegistry(Path.Combine(_dir, "devices-" + Guid.NewGuid().ToString("N") + ".json")),
            };
            app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, requireToken, next));
        }

        StatsPageEndpoint.Map(app, agg);
        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }

    [Fact]
    public async Task StatsPage_Serves_SelfContainedHtml()
    {
        var agg = new GatewayInputStatsAggregator(Path.Combine(_dir, "s.db"));
        var (app, http) = await StartAsync(agg, authEnabled: false);
        try
        {
            var resp = await http.GetAsync("/stats");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.StartsWith("text/html", resp.Content.Headers.ContentType!.ToString());

            var html = await resp.Content.ReadAsStringAsync();
            Assert.Contains("Your Throttle", html); // private per-person view name (owner decision)
            Assert.Contains("/stats/data", html); // the page fetches its own data endpoint
            // Self-contained: no external resource references.
            Assert.DoesNotContain("http://", html);
            Assert.DoesNotContain("https://", html);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task StatsData_ReturnsAggregatedTotals_AndCaveats()
    {
        var agg = new GatewayInputStatsAggregator(Path.Combine(_dir, "s.db"));
        agg.Observe(Session("s1", ("voice", "phone", 3, 300)));
        agg.Observe(Session("s2", ("typed", "desktop", 1, 20)));

        var (app, http) = await StartAsync(agg, authEnabled: false);
        try
        {
            var resp = await http.GetAsync("/stats/data");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            var buckets = root.GetProperty("buckets");
            long voicePhoneTurns = 0, typedDesktopTurns = 0;
            foreach (var b in buckets.EnumerateArray())
            {
                var mod = b.GetProperty("modality").GetString();
                var surf = b.GetProperty("surface").GetString();
                var turns = b.GetProperty("turns").GetInt64();
                if (mod == "voice" && surf == "phone") voicePhoneTurns = turns;
                if (mod == "typed" && surf == "desktop") typedDesktopTurns = turns;
            }
            Assert.Equal(3, voicePhoneTurns);
            Assert.Equal(1, typedDesktopTurns);

            // The honesty caveats are always present so a published share is never quietly flattered.
            Assert.True(root.GetProperty("notCaptured").GetArrayLength() > 0);
            // No message text ever leaves the machine for this page - only counts.
            var raw = root.GetRawText();
            Assert.DoesNotContain("\"text\"", raw, StringComparison.OrdinalIgnoreCase);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task StatsData_RanksRepos_ByTurns()
    {
        var agg = new GatewayInputStatsAggregator(Path.Combine(_dir, "s.db"));
        agg.Observe(new SessionDto
        {
            SessionId = "s1",
            RepoPath = @"D:\ReposFred\devthrottle",
            InputStats = new InputStatsDto { Buckets = { new InputStatBucketDto { Modality = "voice", Surface = "phone", Turns = 6, Characters = 600 } } },
        });
        agg.Observe(new SessionDto
        {
            SessionId = "s2",
            RepoPath = @"C:\repos\mindzieWeb",
            InputStats = new InputStatsDto { Buckets = { new InputStatBucketDto { Modality = "typed", Surface = "desktop", Turns = 2, Characters = 40 } } },
        });

        var (app, http) = await StartAsync(agg, authEnabled: false);
        try
        {
            using var doc = JsonDocument.Parse(await (await http.GetAsync("/stats/data")).Content.ReadAsStringAsync());
            var repos = doc.RootElement.GetProperty("repos");
            Assert.Equal(2, repos.GetArrayLength());

            var first = repos[0];
            Assert.Equal("devthrottle", first.GetProperty("repoName").GetString()); // most turns ranks first
            Assert.Equal(6, first.GetProperty("turns").GetInt64());
            Assert.Equal(6, first.GetProperty("voiceTurns").GetInt64());
            Assert.Equal(1, first.GetProperty("sessions").GetInt32());
            Assert.Equal("mindzieWeb", repos[1].GetProperty("repoName").GetString());
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task StatsData_RanksAgents_ByTurns_AndCarriesTheSinceStamp()
    {
        var agg = new GatewayInputStatsAggregator(Path.Combine(_dir, "s.db"));
        agg.Observe(new SessionDto
        {
            SessionId = "s1",
            Agent = "ClaudeCode",
            InputStats = new InputStatsDto { Buckets = { new InputStatBucketDto { Modality = "voice", Surface = "phone", Turns = 6, Characters = 600 } } },
        });
        agg.Observe(new SessionDto
        {
            SessionId = "s2",
            Agent = "Codex",
            InputStats = new InputStatsDto { Buckets = { new InputStatBucketDto { Modality = "typed", Surface = "desktop", Turns = 2, Characters = 40 } } },
        });

        var (app, http) = await StartAsync(agg, authEnabled: false);
        try
        {
            using var doc = JsonDocument.Parse(await (await http.GetAsync("/stats/data")).Content.ReadAsStringAsync());
            var agents = doc.RootElement.GetProperty("agents");
            Assert.Equal(2, agents.GetArrayLength());

            var first = agents[0];
            Assert.Equal("Claude Code", first.GetProperty("agentName").GetString()); // most turns ranks first
            Assert.Equal("ClaudeCode", first.GetProperty("agent").GetString());
            Assert.Equal(6, first.GetProperty("turns").GetInt64());
            Assert.Equal(6, first.GetProperty("voiceTurns").GetInt64());
            Assert.Equal(1, first.GetProperty("sessions").GetInt32());
            Assert.Equal("Codex", agents[1].GetProperty("agentName").GetString());

            // The page states the window its numbers cover, so the stamp has to reach the client.
            Assert.NotEqual("", doc.RootElement.GetProperty("agentsSinceUtc").GetString());
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Fact]
    public async Task AuthEnabled_NoToken_Returns401_AndWithToken_Returns200()
    {
        var agg = new GatewayInputStatsAggregator(Path.Combine(_dir, "s.db"));
        var (app, http) = await StartAsync(agg, authEnabled: true);
        try
        {
            var noToken = await http.GetAsync("/stats/data");
            Assert.Equal(HttpStatusCode.Unauthorized, noToken.StatusCode);

            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GatewayToken);
            var withToken = await http.GetAsync("/stats/data");
            Assert.Equal(HttpStatusCode.OK, withToken.StatusCode);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }
}
