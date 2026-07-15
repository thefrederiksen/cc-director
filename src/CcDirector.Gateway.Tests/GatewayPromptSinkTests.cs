using System.Net;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests for <see cref="GatewayPromptSink"/> - the Director half of "the Director captures, the Gateway
/// stores" (issue #1551).
///
/// The whole weight of these tests rests on one fact: the Director keeps NO copy of the conversation.
/// Whatever the Gateway accepted IS the record. So this sink's return value is not a status - it is the
/// decision about whether the only copy exists. Return true for a message the Gateway never stored and
/// <see cref="ConversationIngestor"/> marks it done, the next ingest skips it as already-pushed, and the
/// message is gone from the single copy, permanently and silently.
///
/// These drive the REAL <see cref="GatewayClient"/> over a REAL HTTP connection to a Kestrel stub on a
/// loopback port - the pattern the mission-lookup and token-refresh tests use - so the wire behavior is
/// exercised rather than a hand-written fake that is politer than the transport.
/// </summary>
public sealed class GatewayPromptSinkTests
{
    /// <summary>
    /// A Kestrel stub standing in for the Gateway's POST /prompts, on an OS-assigned loopback port.
    /// <paramref name="handler"/> turns the records it received into the response.
    /// </summary>
    private static async Task<(WebApplication app, string url)> StartPromptStubAsync(
        Func<PromptIngestRequest, IResult> handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));   // OS-assigned free port
        var app = builder.Build();
        app.MapPost("/prompts", (PromptIngestRequest req) => handler(req));
        await app.StartAsync();
        return (app, app.Urls.First());
    }

    private static GatewayPromptSink SinkFor(string gatewayUrl)
    {
        var client = new GatewayClient(new GatewayConfig { Url = gatewayUrl }, Guid.NewGuid().ToString(), 7879, "1.0.0");
        return new GatewayPromptSink(() => client);
    }

    private static PromptRecord Record(string text) => new()
    {
        TsUtc = DateTime.UtcNow,
        Machine = Environment.MachineName,
        SessionId = Guid.NewGuid().ToString(),
        Role = "user",
        TimestampFromAgent = true,
        CharCount = text.Length,
        WordCount = 1,
        Text = text,
    };

    private static IReadOnlyList<PromptRecord> ThreeRecords() =>
        new[] { Record("first"), Record("second"), Record("third") };

    [Fact]
    public async Task A_partial_write_is_NOT_acknowledged_as_success()
    {
        // The Gateway's own Append swallows per-record write failures and reports a truthful count of
        // what it actually stored. Two of these three were never written down anywhere.
        var (app, url) = await StartPromptStubAsync(_ => Results.Json(new PromptIngestResponse { Written = 1 }));
        await using var _ = app;

        var accepted = await SinkFor(url).PushAsync(ThreeRecords());

        // False is what stops ConversationIngestor marking all three done. The Director keeps no copy,
        // so true here deletes the two lost messages from the only record that exists - and the log line
        // saying "wrote 1 of 3" is the only trace, in a file nobody reads.
        Assert.False(accepted);
    }

    [Fact]
    public async Task A_gateway_that_stored_nothing_is_not_success()
    {
        var (app, url) = await StartPromptStubAsync(_ => Results.Json(new PromptIngestResponse { Written = 0 }));
        await using var _ = app;

        Assert.False(await SinkFor(url).PushAsync(ThreeRecords()));
    }

    // ===== the controls: the false path must stay narrow, or ingest retries forever and duplicates =====

    [Fact]
    public async Task A_complete_write_IS_acknowledged_as_success()
    {
        var (app, url) = await StartPromptStubAsync(req => Results.Json(new PromptIngestResponse { Written = req.Records.Count }));
        await using var _ = app;

        Assert.True(await SinkFor(url).PushAsync(ThreeRecords()));
    }

    [Fact]
    public async Task An_error_from_the_gateway_is_not_success()
    {
        var (app, url) = await StartPromptStubAsync(_ => Results.StatusCode(500));
        await using var _ = app;

        Assert.False(await SinkFor(url).PushAsync(ThreeRecords()));
    }

    [Fact]
    public async Task A_director_with_no_gateway_records_nothing_and_says_so()
    {
        // Local-only Director: there is no log to write to, so it must not claim the messages are stored.
        Assert.False(await new GatewayPromptSink(() => null).PushAsync(ThreeRecords()));
    }

    [Fact]
    public async Task Pushing_nothing_is_trivially_fine()
    {
        // Nothing to lose, so nothing to retry - this must not become a permanent false.
        Assert.True(await new GatewayPromptSink(() => null).PushAsync(Array.Empty<PromptRecord>()));
    }
}
