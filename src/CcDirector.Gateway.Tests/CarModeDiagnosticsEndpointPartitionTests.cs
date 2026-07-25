using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.CarMode;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Car Mode diagnostics routes over the WIRE, BEHIND THE REAL AUTHENTICATION GATE, with two callers
/// holding different enrolled device keys (hosted-Gateway collection census row 40). The pipeline is the
/// shipped one: <see cref="AuthMiddleware.Run"/> over a real <see cref="DeviceRegistry"/>, then the real
/// <see cref="CarModeEndpoint"/> route map, on an ephemeral port over a temp-file store.
///
/// THE GATE IS IN THE PIPELINE ON PURPOSE. An earlier version of these tests mapped the routes with no
/// authentication and treated any Bearer string as accepted, and that is precisely why they could not see
/// the defect they were meant to guard: the partition was resolved by a SECOND reading of the raw request,
/// which could disagree with the reading the gate had just done. A test that skips authentication cannot
/// observe a disagreement between authentication and the partition, because it only ever runs one of them.
/// <see cref="NoCredential_IsRejected_SoTheseTestsReallyRunBehindTheGate"/> keeps that honest.
///
/// The two facts are asserted SEPARATELY: the WRITE records the partition (proven from the stored document,
/// so a read filter alone could not satisfy it) and the READS filter by it. Every cross-device assertion
/// carries a positive control in the same test - the caller reading its OWN record back on the same route -
/// so an empty answer cannot pass for isolation when it was really a failed seed. Status code and media
/// type are asserted BEFORE any body is parsed, because parsing is itself an assertion about format.
/// </summary>
public sealed class CarModeDiagnosticsEndpointPartitionTests : IAsyncLifetime
{
    private const string SharedMachineToken = "shared-machine-token-for-this-test";

    private readonly string _storePath = Path.Combine(Path.GetTempPath(), $"carmode-diagnostics-http-{Guid.NewGuid():N}.json");
    private readonly string _registryPath = Path.Combine(Path.GetTempPath(), $"carmode-devices-{Guid.NewGuid():N}.json");

    private WebApplication _app = null!;
    private string _baseAddress = "";

    /// <summary>Device A's enrolled per-device key - a real key minted by the real registry.</summary>
    private string _keyA = "";

    /// <summary>Device B's enrolled per-device key.</summary>
    private string _keyB = "";

    public async Task InitializeAsync()
    {
        var devices = new DeviceRegistry(_registryPath);
        _keyA = devices.Register("device-a", "PHONE-A", "android", "phone").DeviceKey;
        _keyB = devices.Register("device-b", "PHONE-B", "android", "phone").DeviceKey;

        var diagnostics = new CarModeDiagnosticsStore(_storePath, _ => { });
        var brain = new CarModeBrain(
            new UnusedChat(),
            _ => new UnusedFleet(),
            new CarModeConversationStore(_ => { }),
            new CarModePendingStore(_ => { }),
            new CarModeSubjectStore(_ => { }),
            _ => { });
        var warmup = new CarModeWarmup(
            _ => ("http://127.0.0.1:1", "model", "key"),
            _ => ("http://127.0.0.1:1", "voice", "model", "key"),
            log: _ => { });

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        // Issue #2161: bind an operating-system-assigned port; the address is read back after start.
        _app.Urls.Add($"http://127.0.0.1:{GatewayHost.OperatingSystemAssignedPort}");

        // The real host-wide gate, exactly as GatewayHost installs it.
        var requireToken = new AuthMiddleware.RequireToken { Token = SharedMachineToken, Devices = devices };
        _app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, requireToken, next));

        CarModeEndpoint.Map(_app, brain, brain, new CarModeTurnCache(_ => { }), diagnostics, warmup, null!);
        await _app.StartAsync();
        _baseAddress = $"http://127.0.0.1:{BoundPort.Of(_app)}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (File.Exists(_storePath)) File.Delete(_storePath);
        if (File.Exists(_registryPath)) File.Delete(_registryPath);
    }


    private HttpClient Client() => new() { BaseAddress = new Uri(_baseAddress) };

    /// <summary>Post one diagnostics record with whatever credentials <paramref name="present"/> attaches,
    ///  asserting the wire contract before any parse.</summary>
    private async Task PostTurnAsync(HttpClient client, string turnId, Action<HttpRequestMessage> present)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/carmode/diagnostics")
        {
            Content = JsonContent.Create(new { turnId, totalTurnMs = 2500.0, brainMs = 1200.0 }),
        };
        present(request);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>Read diagnostics with whatever credentials <paramref name="present"/> attaches, asserting
    ///  status and media type BEFORE parsing the body.</summary>
    private async Task<(int Held, string[] TurnIds)> ReadTurnsAsync(HttpClient client, Action<HttpRequestMessage> present)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/carmode/diagnostics/data");
        present(request);

        var response = await client.SendAsync(request);

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

    /// <summary>Every device hash present in the stored document, keyed by turn id. Read straight from the
    ///  file so the write is proven without going through the read filter.</summary>
    private Dictionary<string, string> StoredPartitions()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(_storePath));
        // The store persists a versioned envelope: an object with a Version and a Records array.
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        return doc.RootElement.GetProperty("Records").EnumerateArray()
            .ToDictionary(
                r => r.GetProperty("TurnId").GetString() ?? "",
                r => r.GetProperty("DeviceHash").GetString() ?? "");
    }

    /// <summary>Authenticate the way an enrolled phone does: the device key as the Bearer.</summary>
    private static Action<HttpRequestMessage> Bearer(string key)
        => request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

    /// <summary>Authenticate the way a browser does: the device key in the cc-gateway-token cookie.</summary>
    private static Action<HttpRequestMessage> Cookie(string key)
        => request => request.Headers.Add("Cookie", $"{AuthMiddleware.CookieName}={key}");

    /// <summary>The attack shape: a valid cookie (which is what the gate will accept) presented ALONGSIDE a
    ///  Bearer value the caller chose and the gate will reject.</summary>
    private static Action<HttpRequestMessage> CookiePlusChosenBearer(string cookieKey, string chosenBearer)
        => request =>
        {
            request.Headers.Add("Cookie", $"{AuthMiddleware.CookieName}={cookieKey}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", chosenBearer);
        };

    /// <summary>The duplicate-cookie shape the gate explicitly tolerates: a stale first cc-gateway-token
    ///  that Request.Cookies exposes, and the valid one second, which is the one the gate validates.</summary>
    private static Action<HttpRequestMessage> StaleThenValidCookie(string staleValue, string validKey)
        => request => request.Headers.Add(
            "Cookie", $"{AuthMiddleware.CookieName}={staleValue}; {AuthMiddleware.CookieName}={validKey}");

    // ---- The gate really is in front of these routes ----

    [Fact]
    public async Task NoCredential_IsRejected_SoTheseTestsReallyRunBehindTheGate()
    {
        using var client = Client();

        var write = await client.PostAsJsonAsync("/carmode/diagnostics", new { turnId = "no-credential" });
        var read = await client.GetAsync("/carmode/diagnostics/data");

        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
    }

    // ---- Two authenticated devices ----

    [Fact]
    public async Task TwoDevices_NeitherDataReadReturnsTheOthersRecords()
    {
        using var client = Client();
        await PostTurnAsync(client, "turn-from-a", Bearer(_keyA));
        await PostTurnAsync(client, "turn-from-b", Bearer(_keyB));

        var readA = await ReadTurnsAsync(client, Bearer(_keyA));
        var readB = await ReadTurnsAsync(client, Bearer(_keyB));

        // Positive control on each side first: each device reads its OWN record back on this very route, so
        // an empty result cannot be mistaken for isolation.
        Assert.Equal(new[] { "turn-from-a" }, readA.TurnIds);
        Assert.Equal(new[] { "turn-from-b" }, readB.TurnIds);
        Assert.DoesNotContain("turn-from-b", readA.TurnIds);
        Assert.DoesNotContain("turn-from-a", readB.TurnIds);
    }

    [Fact]
    public async Task SameTurnId_UnderTwoCredentials_DoesNotCross()
    {
        // The requested colliding-key proof, on the wire behind the gate: the SAME Car Mode key (an identical
        // TurnId) written under two DIFFERENT authenticated device credentials must land in two partitions,
        // never one. Distinguishable payloads (a different brainMs on each side) make "each side reads its
        // OWN record" a real assertion rather than a turn-id match that a merge would also satisfy.
        const string sharedTurn = "same-turn-id-under-both";
        using var client = Client();

        var writeA = new HttpRequestMessage(HttpMethod.Post, "/carmode/diagnostics")
        { Content = JsonContent.Create(new { turnId = sharedTurn, totalTurnMs = 2500.0, brainMs = 111.0 }) };
        Bearer(_keyA)(writeA);
        var respA = await client.SendAsync(writeA);
        Assert.Equal(HttpStatusCode.OK, respA.StatusCode);

        var writeB = new HttpRequestMessage(HttpMethod.Post, "/carmode/diagnostics")
        { Content = JsonContent.Create(new { turnId = sharedTurn, totalTurnMs = 2500.0, brainMs = 222.0 }) };
        Bearer(_keyB)(writeB);
        var respB = await client.SendAsync(writeB);
        Assert.Equal(HttpStatusCode.OK, respB.StatusCode);

        // Positive read on each side, plus the cross-credential exclusion: each device holds exactly ONE
        // record for the colliding key - its own - not two.
        var readA = await ReadTurnsAsync(client, Bearer(_keyA));
        var readB = await ReadTurnsAsync(client, Bearer(_keyB));
        Assert.Equal(new[] { sharedTurn }, readA.TurnIds);
        Assert.Equal(new[] { sharedTurn }, readB.TurnIds);
        Assert.Equal(1, readA.Held);
        Assert.Equal(1, readB.Held);

        // And at rest: the colliding key exists as TWO records, one per partition, with the two payloads kept
        // apart - so nothing was overwritten or merged when the keys collided.
        using var stored = JsonDocument.Parse(File.ReadAllText(_storePath));
        var records = stored.RootElement.GetProperty("Records").EnumerateArray()
            .Where(r => r.GetProperty("TurnId").GetString() == sharedTurn)
            .Select(r => (Hash: r.GetProperty("DeviceHash").GetString(), Brain: r.GetProperty("BrainMs").GetDouble()))
            .ToArray();
        Assert.Equal(2, records.Length);
        Assert.Contains(records, r => r.Hash == CarModeDeviceHash.Of(_keyA) && r.Brain == 111.0);
        Assert.Contains(records, r => r.Hash == CarModeDeviceHash.Of(_keyB) && r.Brain == 222.0);
    }

    [Fact]
    public async Task TwoDevices_TheHeldCountIsPerDevice_NotTheProcessWideTotal()
    {
        using var client = Client();
        await PostTurnAsync(client, "a-one", Bearer(_keyA));
        await PostTurnAsync(client, "b-one", Bearer(_keyB));
        await PostTurnAsync(client, "b-two", Bearer(_keyB));

        var readA = await ReadTurnsAsync(client, Bearer(_keyA));
        var readB = await ReadTurnsAsync(client, Bearer(_keyB));

        Assert.Equal(1, readA.Held);
        Assert.Equal(2, readB.Held);
    }

    [Fact]
    public async Task TheWriteRecordsThePartition_ProvenFromTheStoredDocument()
    {
        // Fact 1, proven WITHOUT going through the read filter: the record on disk carries the writing
        // device's own hash, so the callers' records are separated at rest, not only when served.
        using var client = Client();
        await PostTurnAsync(client, "stored-by-a", Bearer(_keyA));
        await PostTurnAsync(client, "stored-by-b", Bearer(_keyB));

        var stored = StoredPartitions();

        Assert.Equal(CarModeDeviceHash.Of(_keyA), stored["stored-by-a"]);
        Assert.Equal(CarModeDeviceHash.Of(_keyB), stored["stored-by-b"]);
        Assert.NotEqual(stored["stored-by-a"], stored["stored-by-b"]);
    }

    [Fact]
    public async Task ThePostedBodyCannotChooseThePartition()
    {
        using var client = Client();
        await PostTurnAsync(client, "really-b", Bearer(_keyB));

        var request = new HttpRequestMessage(HttpMethod.Post, "/carmode/diagnostics")
        {
            Content = JsonContent.Create(new { turnId = "claimed-by-a", deviceHash = CarModeDeviceHash.Of(_keyB), totalTurnMs = 1000.0 }),
        };
        Bearer(_keyA)(request);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var readA = await ReadTurnsAsync(client, Bearer(_keyA));
        var readB = await ReadTurnsAsync(client, Bearer(_keyB));

        Assert.Equal(new[] { "claimed-by-a" }, readA.TurnIds); // positive control: recorded, under A
        Assert.Equal(new[] { "really-b" }, readB.TurnIds);     // and B's view is untouched by the claim
    }

    // ---- The partition follows the credential that AUTHENTICATED, not one presented alongside it ----

    [Fact]
    public async Task ChosenBearerBesideAValidCookie_WritesIntoTheCookiesPartition_NotTheChosenOne()
    {
        // The gate tries the Bearer, rejects it, then accepts the request on the cookie. The partition must
        // therefore be the cookie's. While the route did its own reading of the request it preferred the
        // Bearer, so a caller could name their own partition on a write.
        const string chosenBearer = "attacker-chosen-partition-key-0001";
        using var client = Client();
        await PostTurnAsync(client, "a-normal", Cookie(_keyA));

        await PostTurnAsync(client, "a-with-chosen-bearer", CookiePlusChosenBearer(_keyA, chosenBearer));

        var stored = StoredPartitions();
        Assert.Equal(CarModeDeviceHash.Of(_keyA), stored["a-normal"]);              // positive control
        Assert.Equal(CarModeDeviceHash.Of(_keyA), stored["a-with-chosen-bearer"]);
        Assert.DoesNotContain(CarModeDeviceHash.Of(chosenBearer), stored.Values);
    }

    [Fact]
    public async Task ChosenBearerBesideAValidCookie_ReadsTheCookiesPartition_NotTheChosenOne()
    {
        // The same mismatch on the READ path: the answer is the authenticating cookie's records.
        const string chosenBearer = "attacker-chosen-partition-key-0002";
        using var client = Client();
        await PostTurnAsync(client, "a-one", Cookie(_keyA));
        await PostTurnAsync(client, "b-one", Bearer(_keyB));

        var mismatched = await ReadTurnsAsync(client, CookiePlusChosenBearer(_keyA, chosenBearer));

        Assert.Equal(new[] { "a-one" }, mismatched.TurnIds); // positive control AND the isolation fact
        Assert.Equal(1, mismatched.Held);
        Assert.DoesNotContain("b-one", mismatched.TurnIds);
    }

    [Fact]
    public async Task DuplicateCookies_PartitionOnTheCookieTheGateValidated_NotTheFirstOne()
    {
        // The gate explicitly tolerates a stale duplicate cc-gateway-token and accepts the request on
        // whichever value is valid. The partition must follow that same value, not the one a single-cookie
        // reading of the request would have picked up first.
        const string stale = "stale-duplicate-cookie-value-0003";
        using var client = Client();

        await PostTurnAsync(client, "written-with-duplicates", StaleThenValidCookie(stale, _keyA));

        var readA = await ReadTurnsAsync(client, Bearer(_keyA));
        Assert.Equal(new[] { "written-with-duplicates" }, readA.TurnIds); // positive control

        var stored = StoredPartitions();
        Assert.Equal(CarModeDeviceHash.Of(_keyA), stored["written-with-duplicates"]);
        Assert.DoesNotContain(CarModeDeviceHash.Of(stale), stored.Values);
    }

    [Fact]
    public async Task TheSharedMachineToken_IsItsOwnPartition_AndNotADevices()
    {
        // The shared machine token is an accepted credential shape too, and it is accepted on a code path
        // that stashes no device key. It must still be partitioned as itself - neither merged into a
        // device's records nor left with no partition at all.
        using var client = Client();
        await PostTurnAsync(client, "by-device-a", Bearer(_keyA));

        await PostTurnAsync(client, "by-shared-token", Bearer(SharedMachineToken));

        var readShared = await ReadTurnsAsync(client, Bearer(SharedMachineToken));
        var readA = await ReadTurnsAsync(client, Bearer(_keyA));

        Assert.Equal(new[] { "by-shared-token" }, readShared.TurnIds); // positive control
        Assert.Equal(new[] { "by-device-a" }, readA.TurnIds);          // positive control
        Assert.DoesNotContain("anonymous", StoredPartitions().Values);  // and never the credential-free bucket
    }

    /// <summary>The brain is never called by these tests - the diagnostics routes do not touch it. It throws
    ///  loudly rather than returning a plausible answer if that assumption ever breaks.</summary>
    private sealed class UnusedChat : ICarModeChat
    {
        public Task<CarModeAssistantTurn> CompleteAsync(TenantId tenant, string messagesJson, string toolsJson, CancellationToken ct)
            => throw new InvalidOperationException("The diagnostics routes must not call the model.");
    }

    private sealed class UnusedFleet : ICarModeFleet
    {
        private static InvalidOperationException Unexpected()
            => new("The diagnostics routes must not call the fleet.");

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
        public Task<CarModeCredits> GetCreditsAsync(CancellationToken ct) => throw Unexpected();
        public Task<IReadOnlyList<CarModeMachineInfo>> ListMachinesAsync(CancellationToken ct) => throw Unexpected();
        public Task<IReadOnlyList<CarModeScheduleInfo>> ListSchedulesAsync(CancellationToken ct) => throw Unexpected();
        public Task<CarModeSpendSummary> GetSpendAsync(int days, CancellationToken ct) => throw Unexpected();
    }
}
