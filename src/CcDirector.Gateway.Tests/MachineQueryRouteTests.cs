using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The two QUERY routes on the machine family - GET /machines/{machine}/apps and GET /machines/{machine}/files -
/// held to the same standard as the action verbs it sits beside.
///
/// WHY THIS FILE EXISTS SEPARATELY FROM THE ACTION-VERB PROOFS. When the hosted deny was replaced by tenant
/// scoping, the exclusive-prefix catch-all that used to cover EVERY route under /machines - including routes
/// nobody had written yet - went with it. A new route under this prefix is therefore live and tenant-blind the
/// moment it is mapped, and nothing in the routing table will notice. These two routes are the first added
/// after that safety net was removed, so they carry their own proof that they did not become the hole the
/// deny was protecting against.
///
/// EVERY CROSS-TENANT TEST OBSERVES THE ACT, NOT ONLY THE STATUS CODE, for the reason the action-verb tests
/// give: a 404 to Bob is also what a simply-broken Gateway returns. So the stub launcher records every dial it
/// receives, and the cross-tenant tests assert it was NOT dialed, while the same-tenant tests assert it WAS -
/// which is what proves the instrument is live rather than merely quiet.
///
/// THE QUERY-SPECIFIC PROPERTY. A query has an answer, and an answer that does not survive the relay is the
/// same as no feature at all. The action verbs could not have caught that: their reply is synthesised by the
/// relay because there is nothing to carry. So this file also pins that the launcher's own document arrives at
/// the caller intact rather than wrapped, summarised, or replaced by a success flag.
/// </summary>
public sealed class MachineQueryRouteTests : IAsyncLifetime
{
    private const string Token = "test-token";
    private const string AliceMachine = "ALICE-PC";
    private const string AliceLauncherToken = "alice-launcher-token";

    /// <summary>A value that exists nowhere but the stub, so finding it in the caller's response proves the
    /// answer travelled the whole way rather than being manufactured somewhere in the middle.</summary>
    private const string StubApplicationName = "Sentinel Application 4242";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private WebApplication? _aliceLauncher;
    private int _aliceLauncherPort;
    private readonly List<string> _aliceLauncherHits = new();

    private string _aliceKey = "";
    private string _bobKey = "";
    private string _unboundKey = "";
    private TenantId _aliceTenant;
    private TenantId _bobTenant;
    private string? _priorHosted;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-machine-query-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);

        _aliceLauncher = await StartStubLauncherAsync();

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        _aliceKey = _gateway.Devices.Register("dev-alice", "ALICE-PC").DeviceKey;
        _aliceTenant = _gateway.TenantRegistry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        _gateway.Devices.SetAccountBinding("dev-alice", "sub-alice", _aliceTenant.Value);

        _bobKey = _gateway.Devices.Register("dev-bob", "BOB-PC").DeviceKey;
        _bobTenant = _gateway.TenantRegistry.MintOrLookupBySubject("sub-bob", "bob@example.com");
        _gateway.Devices.SetAccountBinding("dev-bob", "sub-bob", _bobTenant.Value);

        Assert.NotEqual(_aliceTenant.Value, _bobTenant.Value);

        _unboundKey = _gateway.Devices.Register("dev-unbound", "NOBODY-PC").DeviceKey;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        if (_aliceLauncher is not null) await _aliceLauncher.DisposeAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// The stub cc-launcher on Alice's machine, answering the two query routes the way the real one does: a
    /// catalogue document and a search-result document, each carrying the sentinel.
    /// </summary>
    private async Task<WebApplication> StartStubLauncherAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var stub = builder.Build();
        stub.Urls.Add("http://127.0.0.1:0");

        stub.MapGet("/apps", (HttpContext ctx) =>
        {
            lock (_aliceLauncherHits) _aliceLauncherHits.Add($"apps?q={ctx.Request.Query["q"]}");
            return Results.Json(new AppSearchResultDto
            {
                Machine = AliceMachine,
                TotalMatches = 1,
                Apps = new List<InstalledAppDto>
                {
                    new() { Name = StubApplicationName, Path = @"C:\Alice\App.lnk", Source = "start-menu-user" },
                },
            });
        });

        stub.MapGet("/files", (HttpContext ctx) =>
        {
            lock (_aliceLauncherHits) _aliceLauncherHits.Add($"files?q={ctx.Request.Query["q"]}");
            return Results.Json(new FileSearchResultDto
            {
                Machine = AliceMachine,
                Query = ctx.Request.Query["q"].ToString(),
                Files = new List<FileHitDto>
                {
                    new() { Name = "sentinel.pptx", Path = @"C:\Alice\sentinel.pptx", SizeBytes = 4242 },
                },
                Truncated = true,
                TruncationReason = "limit",
                UnreadableDirectories = 7,
            });
        });

        await stub.StartAsync();
        _aliceLauncherPort = new Uri(stub.Urls.First()).Port;
        return stub;
    }

    private void RegisterAliceLauncher() =>
        _gateway.Launchers.Upsert(_aliceTenant, new LauncherRegistrationRequest
        {
            MachineName = AliceMachine,
            Port = _aliceLauncherPort,
            NetworkAddress = "",
            Token = AliceLauncherToken,
            Pid = 4242,
            Version = "1.2.3",
        });

    private string[] AliceLauncherHits()
    {
        lock (_aliceLauncherHits) return _aliceLauncherHits.ToArray();
    }

    private Task<HttpResponseMessage> Get(string path, string deviceKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
        return _http.SendAsync(request);
    }

    // =====================================================================================================
    // THE CAPABILITY: a tenant queries its OWN machine.
    // =====================================================================================================

    [Fact]
    public async Task Owner_searching_its_own_machine_for_applications_gets_the_launchers_catalogue()
    {
        RegisterAliceLauncher();

        var response = await Get($"machines/{AliceMachine}/apps?q=sentinel", _aliceKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var catalogue = await response.Content.ReadFromJsonAsync<AppSearchResultDto>();
        Assert.Equal(StubApplicationName, Assert.Single(catalogue!.Apps).Name);
        Assert.Contains("apps?q=sentinel", AliceLauncherHits());
    }

    [Fact]
    public async Task Owner_searching_its_own_machine_for_files_gets_the_launchers_results()
    {
        RegisterAliceLauncher();

        var response = await Get($"machines/{AliceMachine}/files?q=*.pptx", _aliceKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<FileSearchResultDto>();
        Assert.Equal("sentinel.pptx", Assert.Single(results!.Files).Name);
        Assert.Contains("files?q=*.pptx", AliceLauncherHits());
    }

    /// <summary>
    /// The property no action verb could have caught. A truncated search that arrives looking complete would
    /// make the caller believe it had seen everything on the machine, so the truncation fields specifically
    /// have to survive the relay - not merely the list of files.
    /// </summary>
    [Fact]
    public async Task A_truncated_answer_arrives_still_marked_truncated_and_still_naming_the_reason()
    {
        RegisterAliceLauncher();

        var response = await Get($"machines/{AliceMachine}/files?q=*.pptx", _aliceKey);
        var results = await response.Content.ReadFromJsonAsync<FileSearchResultDto>();

        Assert.True(results!.Truncated);
        Assert.Equal("limit", results.TruncationReason);
        Assert.Equal(7, results.UnreadableDirectories);
    }

    /// <summary>
    /// The answer is the launcher's own document, served AS the response body. If the query were rendered
    /// through the action verbs' relay envelope instead, this would deserialise to a wrapper whose payload
    /// field held the catalogue as a string - and every caller would have to parse twice to reach it.
    /// </summary>
    [Fact]
    public async Task The_answer_is_the_launchers_document_and_is_not_wrapped_in_a_relay_envelope()
    {
        RegisterAliceLauncher();

        var response = await Get($"machines/{AliceMachine}/apps", _aliceKey);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(document.RootElement.TryGetProperty("apps", out _));
        Assert.False(document.RootElement.TryGetProperty("payload", out _));
        Assert.False(document.RootElement.TryGetProperty("relayStatus", out _));
    }

    // =====================================================================================================
    // THE ISOLATION: nobody else reaches that machine, and the launcher is never dialed on their behalf.
    // =====================================================================================================

    [Fact]
    public async Task Another_tenant_naming_the_same_machine_is_refused_and_the_launcher_is_never_dialed()
    {
        RegisterAliceLauncher();

        var apps = await Get($"machines/{AliceMachine}/apps?q=", _bobKey);
        var files = await Get($"machines/{AliceMachine}/files?q=*.pptx", _bobKey);

        Assert.Equal(HttpStatusCode.NotFound, apps.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, files.StatusCode);

        // The act, not the status code: Alice's launcher process received nothing at all.
        Assert.Empty(AliceLauncherHits());
    }

    /// <summary>
    /// The control for the test above. It proves the stub records dials at all, so "Bob did not reach it" is a
    /// finding rather than an artefact of an instrument that never fires.
    /// </summary>
    [Fact]
    public async Task The_same_machine_IS_dialed_when_its_own_owner_asks_which_proves_the_instrument_is_live()
    {
        RegisterAliceLauncher();

        Assert.Empty(AliceLauncherHits());
        await Get($"machines/{AliceMachine}/apps", _aliceKey);
        Assert.NotEmpty(AliceLauncherHits());
    }

    /// <summary>
    /// An enrolled device bound to no account reaches neither machine nor launcher. The refusal is 401 rather
    /// than the route's own 403 because the host-wide authentication gate turns an unbound key away BEFORE the
    /// route runs - the same answer the action verbs give. The route's tenant check is the second line behind
    /// it, and what matters to this file either way is the last assertion: nothing was dialed.
    /// </summary>
    [Fact]
    public async Task A_device_key_bound_to_no_account_is_refused_rather_than_falling_back_to_a_partition()
    {
        RegisterAliceLauncher();

        var apps = await Get($"machines/{AliceMachine}/apps", _unboundKey);
        var files = await Get($"machines/{AliceMachine}/files?q=*.pptx", _unboundKey);

        Assert.Equal(HttpStatusCode.Unauthorized, apps.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, files.StatusCode);
        Assert.Empty(AliceLauncherHits());
    }

    [Fact]
    public async Task A_machine_this_tenant_has_not_registered_is_a_not_found_naming_the_machine()
    {
        var response = await Get("machines/NO-SUCH-MACHINE/apps", _aliceKey);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("NO-SUCH-MACHINE", await response.Content.ReadAsStringAsync());
    }

    // =====================================================================================================
    // Request validation.
    // =====================================================================================================

    /// <summary>
    /// A file search with no query would walk every drive on the target machine to return an arbitrary first
    /// handful of files. It is refused at the Gateway so the machine is never asked to do that work, and the
    /// refusal is proved by the launcher not being dialed.
    /// </summary>
    [Fact]
    public async Task A_file_search_with_no_query_is_refused_before_the_machine_is_asked_to_do_the_work()
    {
        RegisterAliceLauncher();

        var response = await Get($"machines/{AliceMachine}/files", _aliceKey);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("q is required", await response.Content.ReadAsStringAsync());
        Assert.Empty(AliceLauncherHits());
    }

    /// <summary>An application search with no query is the legitimate "list everything" case, not an error.</summary>
    [Fact]
    public async Task An_application_search_with_no_query_lists_the_whole_catalogue()
    {
        RegisterAliceLauncher();

        var response = await Get($"machines/{AliceMachine}/apps", _aliceKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
