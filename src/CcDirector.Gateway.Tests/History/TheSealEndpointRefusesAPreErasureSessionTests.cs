using System.Net;
using System.Net.Http.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// THE ONE BOUNDARY FACT THAT BINDS A PORT, AND THE ONLY REASON IT IS IN THIS PROJECT.
///
/// Its five siblings - the summariser window and its control, the orphaned roll-up, the future-start
/// refusal, and the failure writer - are pure store facts and live in
/// <c>CcDirector.Gateway.UnitTests</c>. This one starts a real <see cref="WebApplication"/> and binds
/// a socket, which is what the split's rule sends to the machine-wide lock, so it stays here.
///
/// WHAT THAT BUYS TODAY IS LESS THAN IT LOOKS, AND SAYING SO IS THE POINT. This comment first claimed
/// the five siblings therefore run on every local gate. They do not: the fast project is itself parked
/// out of the default run (scripts/test-local.ps1), on a measured runtime rather than on principle, so
/// both projects reach a runner only under -Parked. The placement is still the right one - these five
/// need nothing machine-global and that stays true whichever list they sit on - but the benefit is
/// available later, not banked now.
/// </summary>
public sealed class TheSealEndpointRefusesAPreErasureSessionTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static SessionDto Session(string id = "s1", DateTime? created = null) => new()
    {
        SessionId = id,
        Name = null,
        RepoPath = @"D:\ReposFred\devthrottle",
        RepoName = "thefrederiksen/devthrottle",
        Agent = "TestAgent",
        MachineName = "SOREN_NORTH",
        CreatedAt = created ?? DateTime.UtcNow.AddHours(-2),
        LastActivityAt = DateTime.UtcNow.AddMinutes(-5),
        ActivityState = "Working",
        Status = "Running",
    };

    /// <summary>
    /// FINDING 3, driven through the REAL endpoint rather than the store. The seal request carries no
    /// material time, and the endpoint used to substitute the moment the request ARRIVED - which is always
    /// newer than an erasure that already happened, so every seal was admitted after every delete. The
    /// previous test passed a backdated value the endpoint never produces, which is why it passed over a
    /// live hole.
    ///
    /// There is now no time to pass: admission compares the watermark against the first moment THIS
    /// GATEWAY saw the session, which the Gateway stamps with its own clock and never moves. This fact
    /// goes over HTTP so the thing under test is the path a session actually uses.
    /// </summary>
    [Fact]
    public async Task A_seal_arriving_after_the_delete_is_refused_by_the_real_endpoint()
    {
        var db = _harness.Open();
        var store = new SessionHistoryStore(db);
        var startedBeforeTheDelete = DateTime.UtcNow.AddHours(-2);
        store.UpsertLive("dir-1", Session(created: startedBeforeTheDelete), startedBeforeTheDelete);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        HistoryEndpoints.Map(app, store, tenantBoundary: null);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        try
        {
            store.ErasePromptDerived();

            var response = await client.PostAsJsonAsync("/history/sessions/s1/summary", new SealSessionSummaryRequest
            {
                Summary = "A farewell composed from the conversation the member just erased.",
                WhatWasBuilt = new[] { "something" },
            });

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            using var ctx = db.CreateContext();
            var row = ctx.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s1");
            Assert.Null(row.SummaryText);
            Assert.Null(row.SummaryKind);

            // A CALLER-CONTROLLED START NO LONGER BUYS ADMISSION. This session claims it began after the
            // erasure - the exact value the previous rule trusted - but this Gateway first saw it now, and
            // "now" is after the erasure, so it seals. The point of the assertion below is not that it
            // succeeds but that the value deciding it is OURS: see the refusal fact in
            // CcDirector.Gateway.UnitTests, which drives a Director claiming a future start.
            store.UpsertLive("dir-1", Session(id: "s2", created: DateTime.UtcNow.AddSeconds(1)), DateTime.UtcNow);
            var ok = await client.PostAsJsonAsync("/history/sessions/s2/summary", new SealSessionSummaryRequest
            {
                Summary = "A farewell for work that started after the delete.",
            });
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            using var after = db.CreateContext();
            Assert.Equal("A farewell for work that started after the delete.",
                after.SessionHistory.AsNoTracking().Single(e => e.SessionId == "s2").SummaryText);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
