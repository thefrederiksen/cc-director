using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// GUARDS FOR FIXES THAT WERE OTHERWISE ONLY MADE.
///
/// Three review rounds on this branch produced six repairs, and when asked "if someone removed this
/// tomorrow, does a test go red and NAME it?", the honest answer for four of them was no. They were fixed;
/// they were not protected. That distinction is the whole reason this file exists: a repair with no guard
/// is a repair that survives exactly as long as nobody edits near it, and the proof that it was ever
/// correct lives in a review transcript that will not outlive the session that produced it.
///
/// Three of the four had the same shape, and it is worth naming because it decides where the work goes:
/// THE FIXTURE COULD NOT EXHIBIT THE FAILURE. A single-tenant context cannot show a tenant being lost; a
/// queue exercised directly cannot show host wiring being removed; a hub never sent a rejected push cannot
/// show a rejected push being counted. In each case the assertion was never the problem - it never got the
/// chance. So the fixture is the work here, not the assertion.
/// </summary>
public sealed class StepOneFixesAreProtectedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-step1-guard-" + Guid.NewGuid().ToString("N"));

    public StepOneFixesAreProtectedTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    // -------------------------------------------------------------------------------------------------
    // GUARD 1: statistics must not move for a push the store REJECTED.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// A rejected push is not authoritative, so it must not feed the statistics. The remove path is the one
    /// that damages data if this regresses: Forget DELETES the session's high-water row, so a rejected
    /// remove from a superseded connection drops the row while the session is still live, and the next
    /// authoritative delta then presents the same cumulative counters as new - counting them twice.
    ///
    /// The fixture is the point. Nothing in the suite drove a rejected push at all, so the gate could have
    /// been deleted and every test would still have passed.
    /// </summary>
    [Fact]
    public void ARejectedPushDoesNotMoveTheStatistics()
    {
        var store = new PushedSessionStore();
        using var registry = new DirectorRegistry(Path.Combine(_dir, "instances-rejected"));
        var stats = new GatewayInputStatsAggregator(Path.Combine(_dir, "rejected.db"));
        using var queue = new StatisticsObservationQueueHandle(TimeSpan.FromSeconds(5));

        const string director = "dir-1";
        // Two connections for one Director: the second supersedes the first, so the first's pushes are
        // rejected from then on - the real shape, not a synthetic flag.
        store.RegisterConnection(TenantId.Local, director, "conn-old");
        store.RegisterConnection(TenantId.Local, director, "conn-new");

        var hub = NewHub(store, registry, stats, queue, "conn-new");
        hub.Hello(new DirectorStreamHello { DirectorId = director, MachineName = "m", User = "u", Version = "1", Pid = 1 });
        hub.PushSnapshot(1, new[] { SessionWithTypedCharacters("s-1", 100) });
        WaitFor(() => TypedCharacters(stats) == 100, TimeSpan.FromSeconds(5));
        Assert.Equal(100, TypedCharacters(stats));

        // The SUPERSEDED connection now pushes a much larger tally, and removes the session. Both are
        // rejected by the store, so neither may reach the statistics.
        var stale = NewHub(store, registry, stats, queue, "conn-old");
        stale.Hello(new DirectorStreamHello { DirectorId = director, MachineName = "m", User = "u", Version = "1", Pid = 1 });
        stale.PushSnapshot(2, new[] { SessionWithTypedCharacters("s-1", 999_999) });
        stale.RemoveSession(3, "s-1");

        // Give the queue every chance to have written the wrong thing before concluding it did not.
        Thread.Sleep(500);
        Assert.Equal(100, TypedCharacters(stats));
    }

    // -------------------------------------------------------------------------------------------------
    // GUARD 2: the writer's health must actually reach the page.
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The four health counters existed for a whole review round with no consumer: nothing read them, so a
    /// corrupted store could log failures forever while the page answered 200 from aggregates going stale.
    /// This asserts they are IN THE RESPONSE - the only thing that makes them a health surface rather than
    /// a field on an object.
    /// </summary>
    [Fact]
    public async Task TheWritersHealthAppearsOnTheStatsPage()
    {
        var stats = new GatewayInputStatsAggregator(Path.Combine(_dir, "health.db"));
        using var queue = new StatisticsObservationQueueHandle(TimeSpan.FromSeconds(5));

        // A write that fails, so there is something for the page to report.
        queue.Queue.Offer(StatisticsObservationQueue.InputStatsObserver,
            _ => throw new InvalidOperationException("database disk image is malformed"));
        WaitFor(() => queue.Queue.IsDegraded(), TimeSpan.FromSeconds(5));
        Assert.True(queue.Queue.IsDegraded(), "the queue never recorded the failure, so this proves nothing");

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        StatsPageEndpoint.Map(app, stats, writeQueue: queue.Queue);
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            var body = JsonDocument.Parse(await http.GetStringAsync("/stats/data"));

            var health = body.RootElement.GetProperty("writeHealth");
            Assert.True(health.GetProperty("degraded").GetBoolean(),
                "the page does not report the writer as degraded while the writer is failing");

            var observer = health.GetProperty("observers").EnumerateArray()
                .Single(o => o.GetProperty("observer").GetString() == StatisticsObservationQueue.InputStatsObserver);
            Assert.True(observer.GetProperty("failureCount").GetInt64() > 0);
            Assert.Contains("malformed", observer.GetProperty("lastError").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------

    /// <summary>Disposes the queue synchronously so a [Fact] need not be async just to tear one down.</summary>
    private sealed class StatisticsObservationQueueHandle : IDisposable
    {
        public StatisticsObservationQueue Queue { get; }
        public StatisticsObservationQueueHandle(TimeSpan bound) => Queue = new StatisticsObservationQueue(bound);
        public void Dispose() => Queue.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static DirectorHub NewHub(PushedSessionStore store, DirectorRegistry registry,
        GatewayInputStatsAggregator stats, StatisticsObservationQueueHandle queue, string connectionId) =>
        new(store, registry, stats, new GatewayStreamRegistry(), statsQueue: queue.Queue)
        {
            Context = new FakeHubCallerContext(connectionId),
        };

    private static SessionDto SessionWithTypedCharacters(string id, long characters)
    {
        var dto = new SessionDto { SessionId = id, ActivityState = "Working", InputStats = new InputStatsDto() };
        dto.InputStats!.Buckets.Add(new InputStatBucketDto
        {
            Modality = "typed", Surface = "desktop", Turns = 1, Characters = characters,
        });
        return dto;
    }

    private static long TypedCharacters(GatewayInputStatsAggregator stats) =>
        stats.CurrentTotals(TenantId.Local).Buckets.Sum(b => b.Characters);

    /// <summary>A minimal SignalR caller context. The one in DirectorHubTests is private to that class, and
    /// duplicating fifteen lines beats making an unrelated test file's internals public for this.</summary>
    private sealed class FakeHubCallerContext : HubCallerContext
    {
        public FakeHubCallerContext(string connectionId) => ConnectionId = connectionId;
        public override string ConnectionId { get; }
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }
}
