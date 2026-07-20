using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.CarMode;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Car Mode telemetry routes over the WIRE, with two callers holding DIFFERENT device credentials
/// (hosted-Gateway collection census row 40). Boots the real <see cref="CarModeEndpoint"/> route map on an
/// ephemeral port over a temp-file store, so these drive the shipped handlers, not a stand-in.
///
/// The two facts are asserted SEPARATELY:
///   1. the WRITE records the partition - proven from the stored document itself, so it cannot be satisfied
///      by a read filter alone (a read-only filter is a deferred leak: records would keep accumulating
///      unpartitioned behind it);
///   2. the READ filters by the partition - neither caller's data read returns the other's records or
///      counts them.
///
/// Every cross-device assertion carries a positive control in the same test - the caller reading its OWN
/// record back on the same route - so an empty answer can never pass for isolation when it was really a
/// failed seed. Status code and media type are asserted BEFORE any body is parsed, because parsing is
/// itself an assertion about format and a parse failure would arrive as a crash rather than a verdict.
/// </summary>
public sealed class CarModeTelemetryEndpointPartitionTests : IAsyncLifetime
{
    private const string CredentialA = "device-a-credential-0000000000";
    private const string CredentialB = "device-b-credential-1111111111";

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"carmode-telemetry-http-{Guid.NewGuid():N}.json");
    private WebApplication _app = null!;
    private string _baseAddress = "";

    public async Task InitializeAsync()
    {
        var telemetry = new CarModeTelemetryStore(_path, _ => { });
        var brain = new CarModeBrain(
            new UnusedChat(),
            new UnusedFleet(),
            new CarModeConversationStore(_ => { }),
            new CarModePendingStore(_ => { }),
            new CarModeSubjectStore(_ => { }),
            _ => { });
        var warmup = new CarModeWarmup(
            () => ("http://127.0.0.1:1", "model", "key"),
            () => ("http://127.0.0.1:1", "voice", "model", "key"),
            log: _ => { });

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        var port = AllocateFreePort();
        _app.Urls.Add($"http://127.0.0.1:{port}");
        CarModeEndpoint.Map(_app, brain, new CarModeTurnCache(_ => { }), telemetry, warmup);
        await _app.StartAsync();
        _baseAddress = $"http://127.0.0.1:{port}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static int AllocateFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private HttpClient ClientFor(string credential)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseAddress) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return client;
    }

    /// <summary>Post one telemetry record as this caller, asserting the wire contract before any parse.</summary>
    private static async Task PostTurnAsync(HttpClient client, string turnId)
    {
        var response = await client.PostAsJsonAsync("/carmode/telemetry", new
        {
            turnId,
            totalTurnMs = 2500.0,
            brainMs = 1200.0,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Read this caller's telemetry, asserting status and media type BEFORE parsing the body.</summary>
    private static async Task<(int Held, string[] TurnIds)> ReadTurnsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/carmode/telemetry/data");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var held = doc.RootElement.GetProperty("held").GetInt32();
        var ids = doc.RootElement.GetProperty("records")
            .EnumerateArray()
            .Select(r => r.GetProperty("turnId").GetString() ?? "")
            .ToArray();
        return (held, ids);
    }

    [Fact]
    public async Task TwoDevices_NeitherDataReadReturnsTheOthersRecords()
    {
        using var a = ClientFor(CredentialA);
        using var b = ClientFor(CredentialB);
        await PostTurnAsync(a, "turn-from-a");
        await PostTurnAsync(b, "turn-from-b");

        var readA = await ReadTurnsAsync(a);
        var readB = await ReadTurnsAsync(b);

        // Positive control on each side first: each caller reads its OWN record back on this very route, so
        // an empty result cannot be mistaken for isolation.
        Assert.Equal(new[] { "turn-from-a" }, readA.TurnIds);
        Assert.Equal(new[] { "turn-from-b" }, readB.TurnIds);
        // The isolation assertion itself.
        Assert.DoesNotContain("turn-from-b", readA.TurnIds);
        Assert.DoesNotContain("turn-from-a", readB.TurnIds);
    }

    [Fact]
    public async Task TwoDevices_TheHeldCountIsPerDevice_NotTheProcessWideTotal()
    {
        using var a = ClientFor(CredentialA);
        using var b = ClientFor(CredentialB);
        await PostTurnAsync(a, "a-one");
        await PostTurnAsync(b, "b-one");
        await PostTurnAsync(b, "b-two");

        var readA = await ReadTurnsAsync(a);
        var readB = await ReadTurnsAsync(b);

        Assert.Equal(1, readA.Held); // positive control: A's own turn is counted
        Assert.Equal(2, readB.Held); // positive control: B's own turns are counted
    }

    [Fact]
    public async Task TheWriteRecordsThePartition_ProvenFromTheStoredDocument()
    {
        // Fact 1, proven WITHOUT going through the read filter: the record on disk carries the writing
        // device's own hash, so the two callers' records are separated at rest, not just when served.
        using var a = ClientFor(CredentialA);
        using var b = ClientFor(CredentialB);
        await PostTurnAsync(a, "stored-by-a");
        await PostTurnAsync(b, "stored-by-b");

        using var doc = JsonDocument.Parse(File.ReadAllText(_path));

        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        var stored = doc.RootElement.EnumerateArray()
            .ToDictionary(r => r.GetProperty("TurnId").GetString() ?? "", r => r.GetProperty("DeviceHash").GetString() ?? "");
        Assert.Equal(CarModeDeviceHash.Of(CredentialA), stored["stored-by-a"]);
        Assert.Equal(CarModeDeviceHash.Of(CredentialB), stored["stored-by-b"]);
        Assert.NotEqual(stored["stored-by-a"], stored["stored-by-b"]);
    }

    [Fact]
    public async Task ThePostedBodyCannotChooseThePartition()
    {
        // A caller-supplied value is never the discriminator: caller A posts a body claiming B's hash and a
        // turn id shaped like B's, and it still lands in A's partition only.
        using var a = ClientFor(CredentialA);
        using var b = ClientFor(CredentialB);
        await PostTurnAsync(b, "really-b");

        var response = await a.PostAsJsonAsync("/carmode/telemetry", new
        {
            turnId = "claimed-by-a",
            deviceHash = CarModeDeviceHash.Of(CredentialB),
            totalTurnMs = 1000.0,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var readA = await ReadTurnsAsync(a);
        var readB = await ReadTurnsAsync(b);

        Assert.Equal(new[] { "claimed-by-a" }, readA.TurnIds); // positive control: it was recorded, under A
        Assert.Equal(new[] { "really-b" }, readB.TurnIds);     // and B's view is untouched by the claim
    }

    /// <summary>The brain is never called by these tests - the telemetry routes do not touch it. It throws
    ///  loudly rather than returning a plausible answer if that assumption ever breaks.</summary>
    private sealed class UnusedChat : ICarModeChat
    {
        public Task<CarModeAssistantTurn> CompleteAsync(string messagesJson, string toolsJson, CancellationToken ct)
            => throw new InvalidOperationException("The telemetry routes must not call the model.");
    }

    private sealed class UnusedFleet : ICarModeFleet
    {
        private static InvalidOperationException Unexpected()
            => new("The telemetry routes must not call the fleet.");

        public Task<IReadOnlyList<CarModeSessionInfo>> ListSessionsAsync(CancellationToken ct) => throw Unexpected();
        public Task<CarModeActivity?> GetSessionActivityAsync(string sessionReference, CancellationToken ct) => throw Unexpected();
        public Task<CarModeSessionInfo?> ResolveSessionAsync(string sessionReference, CancellationToken ct) => throw Unexpected();
        public Task<string> StartSessionAsync(string repo, CancellationToken ct) => throw Unexpected();
        public Task MessageSessionAsync(string sessionId, string message, CancellationToken ct) => throw Unexpected();
        public Task ApproveSessionAsync(string sessionId, CancellationToken ct) => throw Unexpected();
        public Task DeleteSessionAsync(string sessionId, CancellationToken ct) => throw Unexpected();
        public Task<CarModeExplain> ExplainSessionAsync(string sessionId, CancellationToken ct) => throw Unexpected();
        public Task SwitchVoiceModeAsync(string sessionId, bool enabled, CancellationToken ct) => throw Unexpected();
        public Task SnoozeSessionAsync(string sessionId, CancellationToken ct) => throw Unexpected();
    }
}
