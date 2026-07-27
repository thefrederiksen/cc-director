using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;
using SessionHistoryStore = CcDirector.Gateway.History.SessionHistoryStore;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The session-origin block of <c>GET /stats/data</c> (devthrottle_internal issue #982): how the fleet's
/// sessions CAME TO EXIST, served beside the turn counts that were already there.
///
/// The distinction the block exists to make: the agent-driven numbers already in this feed count TURNS -
/// who does the talking once a session is running. These count BIRTHS - who decides a session should
/// exist at all, which is the step that turns one person into a supervisor of twenty-two.
///
/// Boots only the stats endpoint over a real work-history store on a throwaway SQLite file.
/// </summary>
public sealed class StatsSessionOriginFeedTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static SessionDto Born(string id, DateTime startedAt, string kind, string surface, string? parent = null) => new()
    {
        SessionId = id,
        RepoPath = @"D:\repos\devthrottle",
        Agent = "ClaudeCode",
        MachineName = "SOREN_NORTH",
        CreatedAt = startedAt,
        ActivityState = "Working",
        Status = "Running",
        OriginKind = kind,
        OriginSurface = surface,
        ParentSessionId = parent,
    };

    private static async Task<(WebApplication app, HttpClient http)> StartAsync(SessionHistoryStore? history)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        StatsPageEndpoint.Map(app, new GatewayInputStatsAggregator(), sessionHistory: history);
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    [Fact]
    public async Task The_feed_reports_how_sessions_came_to_exist()
    {
        var store = new SessionHistoryStore(_harness.Open());
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Born("a", now.AddHours(-2), SessionOriginKinds.Agent, SessionOriginSurfaces.Cli, Guid.NewGuid().ToString()), now);
        store.UpsertLive("dir-1", Born("b", now.AddHours(-2), SessionOriginKinds.Agent, SessionOriginSurfaces.Cli, Guid.NewGuid().ToString()), now);
        store.UpsertLive("dir-1", Born("c", now.AddHours(-2), SessionOriginKinds.Human, SessionOriginSurfaces.Desktop), now);
        store.UpsertLive("dir-1", Born("d", now.AddHours(-2), SessionOriginKinds.Schedule, SessionOriginSurfaces.Cron), now);

        var (app, http) = await StartAsync(store);
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync("/stats/data"));
            var origins = doc.RootElement.GetProperty("sessionOrigins");
            var week = origins.GetProperty("last7Days");

            Assert.Equal(4, week.GetProperty("sessions").GetInt32());
            Assert.Equal(2, week.GetProperty("byKind").GetProperty(SessionOriginKinds.Agent).GetInt32());
            Assert.Equal(1, week.GetProperty("byKind").GetProperty(SessionOriginKinds.Human).GetInt32());
            Assert.Equal(1, week.GetProperty("byKind").GetProperty(SessionOriginKinds.Schedule).GetInt32());
            Assert.Equal(2, week.GetProperty("withParentSession").GetInt32());
            Assert.Equal(2, week.GetProperty("bySurface").GetProperty(SessionOriginSurfaces.Cli).GetInt32());

            // Both windows are served, and the all-time block reports where the RECORD begins - the
            // oldest birth actually stored - rather than the floor of the query. Retention prunes from
            // the front and the fields only began being written the day they shipped, so a share quoted
            // over "all time" without that date is quoting a denominator the Gateway has not got.
            var allTime = origins.GetProperty("allTime");
            Assert.Equal(4, allTime.GetProperty("sessions").GetInt32());
            var recordBegins = allTime.GetProperty("recordBeginsUtc").GetDateTime();
            Assert.True(recordBegins > DateTime.UtcNow.AddHours(-3), "the record begins at the oldest stored birth, not at an epoch");
            Assert.False(allTime.TryGetProperty("sinceUtc", out _));

            // No share is computed. What to do with the unaccounted buckets is the reader's call, and
            // baking one answer in here would fix that choice for everyone and hide it from all of them.
            Assert.False(week.TryGetProperty("agentShare", out _));
        }
        finally
        {
            http.Dispose();
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Sessions_recorded_before_the_fields_existed_are_kept_out_of_the_real_buckets()
    {
        var store = new SessionHistoryStore(_harness.Open());
        var now = DateTime.UtcNow;
        store.UpsertLive("dir-1", Born("a", now.AddHours(-2), SessionOriginKinds.Agent, SessionOriginSurfaces.Cli, Guid.NewGuid().ToString()), now);
        // No origin on the wire at all - an older Director, or a row written before this shipped.
        store.UpsertLive("dir-1", new SessionDto
        {
            SessionId = "legacy",
            RepoPath = @"D:\repos\devthrottle",
            Agent = "ClaudeCode",
            CreatedAt = now.AddHours(-2),
            ActivityState = "Working",
            Status = "Running",
        }, now);

        var (app, http) = await StartAsync(store);
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync("/stats/data"));
            var week = doc.RootElement.GetProperty("sessionOrigins").GetProperty("last7Days");

            Assert.Equal(2, week.GetProperty("sessions").GetInt32());
            Assert.Equal(1, week.GetProperty("byKind").GetProperty(SessionOriginKinds.Agent).GetInt32());
            Assert.Equal(1, week.GetProperty("byKind").GetProperty(SessionHistoryStore.NotRecorded).GetInt32());
            // The one that matters: it is NOT counted as human, which is what an agent share computed
            // over a partly-instrumented record would quietly assume.
            Assert.False(week.GetProperty("byKind").TryGetProperty(SessionOriginKinds.Human, out _));

            // And the feed says in words what the bucket means, so a reader never has to guess whether
            // "notRecorded" is a kind of session or the absence of a record.
            Assert.Contains("predates the origin fields",
                doc.RootElement.GetProperty("sessionOrigins").GetProperty("notRecordedMeans").GetString());
        }
        finally
        {
            http.Dispose();
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task With_no_history_store_the_block_is_absent_rather_than_zero()
    {
        // A zero here would read as "no agent ever started a session" - a different and much more
        // interesting claim than "this Gateway is not keeping the record".
        var (app, http) = await StartAsync(history: null);
        try
        {
            using var doc = JsonDocument.Parse(await http.GetStringAsync("/stats/data"));
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("sessionOrigins").ValueKind);
        }
        finally
        {
            http.Dispose();
            await app.StopAsync();
        }
    }
}
