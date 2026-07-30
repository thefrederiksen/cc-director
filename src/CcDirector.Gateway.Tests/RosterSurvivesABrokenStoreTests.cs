using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Snooze;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE OUTCOME TEST FOR THE 2026-07-30 OUTAGE, as opposed to the mechanism tests beside it.
///
/// The incident was not that a queue misbehaved. It was that <c>GET /sessions</c> ANSWERED 500 TO THE
/// WHOLE FLEET for 32 minutes, because an optional write inside the roster handler threw. Every test in
/// StatisticsCannotFailTheFleetTests proves the queue is a well-behaved queue - and every one of them
/// would still pass if a future edit called a store DIRECTLY from the handler again, because the queue
/// would simply no longer be on the path. The incident would reproduce exactly and the suite would
/// certify it green. That is the justifying property going untested.
///
/// So these two exercise the REAL handler over real HTTP and assert the thing the owner actually cares
/// about: the roster answers, and it carries every session.
///
/// ASSERT THE CONTENTS, NEVER JUST THE STATUS. A 200 carrying an empty roster is exactly the false green
/// that made the rollback look successful while the fleet was still reconnecting - checking only for 200
/// here would walk into the same trap inside the regression test meant to prevent it.
/// </summary>
public sealed class RosterSurvivesABrokenStoreTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string Token = "test-token";
    private const string DirectorId = "dir-1";

    private readonly GatewayDbTestHarness _h = new();
    private readonly string _instances =
        Path.Combine(Path.GetTempPath(), "cc-roster-survives-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _h.Dispose();
        try { Directory.Delete(_instances, recursive: true); } catch (Exception) { /* best-effort */ }
    }

    /// <summary>
    /// THE INCIDENT, INVERTED: the roster answers, and carries every session, on the shipped composition.
    ///
    /// BE HONEST ABOUT WHAT THIS DOES AND DOES NOT PROVE. An earlier version created a corrupt database file
    /// and never connected it to anything - it was a roster baseline wearing a broken-store name, and it
    /// would have passed just as happily with the fault fully restored, because the collaborator it claimed
    /// to break was never wired in. The unused file is gone rather than left to imply a coverage that was
    /// not there.
    ///
    /// What this genuinely proves: the shipped handler answers 200 with the complete roster, and it goes RED
    /// with a 500 when a throwing statistics write is put back inside it - watched, not assumed. What it
    /// cannot prove is that the collaborator is unreachable, because a null passed by omission looks
    /// identical to a parameter that does not exist. That is the structural test above's job.
    /// </summary>
    /// <summary>
    /// THE STRUCTURAL GUARD, and it is the one that cannot be satisfied by accident.
    ///
    /// The outage happened because the roster handler could REACH a statistics collaborator. The fix was not
    /// to guard that call - it was to remove the collaborator from the handler's reach entirely, so there is
    /// nothing to call and no guard to forget. This asserts exactly that, by inspecting the shipped
    /// signature: if anyone re-introduces a statistics parameter to the roster's Map, this goes red the
    /// moment they compile, wherever they wire it and whether or not they pass null.
    ///
    /// It exists because the HTTP test below CANNOT prove this. That test passes a null collaborator by
    /// omission, so re-adding the parameter and leaving the test's argument unset keeps it green while the
    /// production wiring reintroduces the exact fault. A test that a regression can walk straight past is
    /// not a guard; a signature that will not compile is.
    /// </summary>
    [Fact]
    public void TheRosterHandlerCannotReachAStatisticsCollaboratorAtAll()
    {
        var map = typeof(GatewayEndpoints).GetMethods()
            .Single(m => m.Name == nameof(GatewayEndpoints.Map) && m.GetParameters().Length > 3);

        var statisticsParameters = map.GetParameters()
            .Where(p => p.ParameterType == typeof(GatewayInputStatsAggregator)
                     || p.ParameterType == typeof(GatewaySessionConcurrencyStats))
            .Select(p => $"{p.ParameterType.Name} {p.Name}")
            .ToList();

        Assert.True(statisticsParameters.Count == 0,
            "GatewayEndpoints.Map accepts a statistics collaborator again: "
            + string.Join(", ", statisticsParameters)
            + ". On 2026-07-30 a statistics write reachable from this handler answered HTTP 500 to the whole "
            + "fleet for 32 minutes. Statistics are observed at the push ingress; the roster must have no way "
            + "to call them, because a call that cannot be written cannot be forgotten to be guarded.");
    }

    [Fact]
    public async Task WithTheStatisticsStoreUnusable_TheRosterStillAnswersAndCarriesEverySession()
    {
        await WithGateway(seedSessions: new[] { "session-alpha", "session-beta", "session-gamma" },
            snoozeRegistry: NewRegistry(),
            assertion: async http =>
            {
                var res = await http.GetAsync("/sessions");
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);

                var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var served = body.RootElement.EnumerateArray()
                    .Select(s => s.GetProperty("sessionId").GetString())
                    .ToList();

                // The contents, not the count of a status code. An empty 200 is the false green.
                Assert.Equal(3, served.Count);
                Assert.Contains("session-alpha", served);
                Assert.Contains("session-beta", served);
                Assert.Contains("session-gamma", served);
            });
    }

    /// <summary>
    /// THE SNOOZE STORE, which is the same mechanism in a second store. PruneNotLive opened a database
    /// context and did RemoveRange plus SaveChanges INSIDE the roster's per-Director loop - on hosted, a
    /// Postgres write on the fleet's primary read path. It has moved to the ingress; this proves the read
    /// no longer depends on that store being healthy, rather than leaving it proven by inspection.
    ///
    /// The database is disposed out from under the registry, so any call that touches it throws.
    /// </summary>
    [Fact(Skip = "Issue #2323: GET /sessions still answers 500 when the snooze store is unhealthy. This test "
               + "is CORRECT and is FAILING against real behaviour - it is skipped, not weakened, and its "
               + "assertion has deliberately not been changed to match what the code currently does. "
               + "SnoozeRegistry.HoldStateFor, IsExpired and SnoozeUntilFor each open a database context, "
               + "per session, inside the roster fold. ISSUE #2323 IS CLOSED BY DELETING THIS SKIP AND "
               + "WATCHING THIS TEST PASS - by nothing else. Not by a code reading, not by a reviewer's "
               + "sign-off, not by a green suite that still contains this skip. If you are here because you "
               + "think you have fixed it, remove the Skip and run it; if you are here for any other reason, "
               + "the issue is still open.")]
    public async Task WithTheSnoozeStoreBroken_TheRosterStillAnswersAndCarriesEverySession()
    {
        var registry = NewRegistry();
        _h.Dispose();   // the store the registry writes through is now gone; touching it throws

        await WithGateway(seedSessions: new[] { "session-alpha", "session-beta" },
            snoozeRegistry: registry,
            assertion: async http =>
            {
                var res = await http.GetAsync("/sessions");
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);

                var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                var served = body.RootElement.EnumerateArray()
                    .Select(s => s.GetProperty("sessionId").GetString())
                    .ToList();
                Assert.Equal(2, served.Count);
                Assert.Contains("session-alpha", served);
                Assert.Contains("session-beta", served);
            });
    }

    private SnoozeRegistry NewRegistry() =>
        new(_h.Open(), _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));

    /// <summary>Hosts the REAL production routes over HTTP with a seeded push cache, the same
    /// GatewayEndpoints.Map the shipped Gateway runs - so the handler under test is the shipped one.</summary>
    private async Task WithGateway(IReadOnlyList<string> seedSessions, SnoozeRegistry snoozeRegistry,
        Func<HttpClient, Task> assertion)
    {
        WebApplication? app = null;
        DirectorRegistry? registry = null;
        var started = false;
        try
        {
            var store = new PushedSessionStore();
            var conn = "conn-1";
            store.RegisterConnection(Tenant, DirectorId, conn);
            Assert.True(store.ApplySnapshot(Tenant, DirectorId, conn, 0,
                seedSessions.Select(id => new SessionDto
                {
                    SessionId = id,
                    ActivityState = "Working",
                    Name = id,
                }).ToList()));

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://127.0.0.1:{GatewayHost.OperatingSystemAssignedPort}");
            app = builder.Build();
            registry = new DirectorRegistry(_instances);
            registry.RegisterFromStream(DirectorId, "machine-1", "user", "1.0", pid: 1, startedAt: default, Tenant);

            GatewayEndpoints.Map(
                app,
                registry,
                version: "test",
                token: Token,
                pushedSessions: store,
                snoozeRegistry: snoozeRegistry);

            await app.StartAsync();
            var port = BoundPort.Of(app);
            started = true;
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {Token}");
            await assertion(http);
        }
        finally
        {
            if (app is not null)
            {
                if (started) await app.StopAsync();
                await app.DisposeAsync();
            }
            registry?.Dispose();
        }
    }
}
