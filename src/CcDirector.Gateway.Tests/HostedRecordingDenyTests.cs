using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Recording;
using CcDirector.Core.Storage;
using CcDirector.Gateway;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The whole <c>/ingest</c> group is DENIED on the hosted Gateway - the recording reads, the writes, the
/// promote and delete, and the shared dictation glossary alike.
///
/// The store behind those routes carries NO tenant: the durable directory for a recording is built from the
/// caller-supplied recording id alone, and the glossary is one global file, and the group sits behind only
/// the host-wide authentication gate - which admits ANY enrolled device key from ANY account. So before this
/// change any hosted subscriber could list every recording on the box, read another account's raw audio and
/// its transcript, overwrite its metadata and chunks, promote it into the vault, or delete it outright. That
/// is theft and tampering of recorded conversations, which is why the whole surface closes and not only a
/// value-returning read.
///
/// WHY A DENY AND NOT A PARTITION. Partitioning this store is real work - the on-disk layout, the promote
/// target and the shared glossary all have to grow an owner - so a per-account answer would have to be
/// invented rather than read. That is a half-partition, which is worse than an honest refusal because it
/// looks like isolation. (The cached wingman voice READ surface, by contrast, WAS partitioned per tenant in
/// #1973 and is therefore SERVED, not denied - so this file deliberately says nothing about it.)
///
/// HOW THE DENY IS EXPRESSED - THE SHARED REFUSAL PRIMITIVE. The group is denied through
/// <see cref="HostedRouteDeny.ExclusiveGroup"/>, the ONE hosted-refusal boundary every deny family on this
/// Gateway adopts (the key-vault group in <see cref="VaultEndpoints"/> is the reference adoption). On hosted
/// the handlers are NEVER MAPPED - one verb-less catch-all refusal claims everything under <c>/ingest</c>
/// (plus a root refusal at the prefix itself), so every request shape meets the refusal: a valid body, a
/// malformed body, a wrong media type, a verb the group never mapped, and a route added LATER. Off hosted the
/// primitive maps the real handlers exactly as an unguarded builder would and creates no refusal at all.
///
/// THE GATE IS ON THE DEPLOYMENT SIGNAL. The primitive reads <see cref="GatewayHostedMode.IsHosted"/>
/// directly, never an optional argument that would fail OPEN the moment a caller omits it.
///
/// IT REFUSES, IT NEVER SERVES AN EMPTY ANSWER. An empty recordings list would be a FALSE statement about a
/// box that holds recordings; an absent route is merely absent.
///
/// STATUS AND MEDIA TYPE ARE ASSERTED BEFORE ANY PARSE. On this Gateway a 404 is not necessarily JSON - the
/// single-page-app fallback answers unmatched paths with something else - so a mutation that routed a denied
/// path to the fallback would make the parse THROW. That red is a crash, which proves only that the mutation
/// broke something upstream of the claim; it cannot say WHAT was served in place of the refusal, which is the
/// entire claim a deny makes.
///
/// A 404 DENY IS INDISTINGUISHABLE FROM A ROUTE THAT DOES NOT EXIST, so <see cref="SelfHostRecordingGroupControlTests"/>
/// carries handler-positive receipts proving each route is really there and really does the thing on
/// self-host, in both non-hosted forms. Survival assertions here carry a DESTRUCTIBILITY control there: the
/// same delete that is refused on hosted really destroys the same seeded recording on self-host, so "it
/// survived" is a claim about a request that was CAPABLE of destroying something.
///
/// REVERT-PROOF - the recipe to RUN, not to describe. In <c>src/CcDirector.Gateway/Api/RecordingEndpoints.cs</c>
/// change <c>HostedRouteDeny.ExclusiveGroup(outer, Prefix, Denial())</c> so the family maps its real handlers
/// on hosted too - the simplest such mutation is to construct a plain non-denied group with the same prefix
/// (<c>outer.MapGroup(Prefix)</c> wrapped so <c>MapRoutes</c> still accepts it) and map the routes on it, or
/// simply return <c>outer</c>'s group unguarded. The hosted deny is then absent. Rebuild, CONFIRM ZERO ERRORS
/// (a run after a failed build executes the previous binary and reports a false pass), then run this file and
/// record every red BY NAME: <see cref="Every_ingest_route_is_refused_to_an_enrolled_tenant"/> flips to
/// "expected NotFound, got OK/202" as each handler answers, and <see cref="The_refused_delete_did_not_take_effect"/>
/// reddens because the seeded recording is really gone. A red only counts if it fails WITH THE SYMPTOM - an
/// assertion naming what was served instead of the refusal; crash-reds are UNPROVEN.
/// </summary>
[Collection("DirectorRoot")]
public sealed class HostedRecordingDenyTests : IAsyncLifetime
{
    private const string Token = "test-token";
    internal const string RefusalMessage = RecordingEndpoints.RefusalMessage;
    private const string SeededId = "seeded-recording-of-another-tenant";
    private const string SeededTitle = "Another tenant's private call";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _key = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-hosted-rec-" + Guid.NewGuid().ToString("N"));
    private readonly string _vaultPath =
        Path.Combine(Path.GetTempPath(), "cc-hosted-rec-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly string _root;
    private readonly string? _prevRoot;
    private string? _priorHosted;

    public HostedRecordingDenyTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-hosted-rec-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        // EXPLICIT, not ambient: this class asserts hosted behaviour, so it states hosted mode itself and
        // proves the statement took, rather than inheriting whatever the runner happened to leave set.
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);

        // A real recording already on disk (via the production writer), so a read or a delete that WRONGLY
        // got through would have something real to hand back or destroy. A deny tested against an empty
        // transcripts root proves nothing.
        RecordingSeeder.Seed(SeededId, SeededTitle);

        _gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            keyVaultPath: _vaultPath,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // A fully enrolled, tenant-bound device key - the strongest caller hosted has. The point is that even
        // this one is refused: there is no credential that makes another account's recording readable.
        _key = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", tenant.Value);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (File.Exists(_vaultPath)) File.Delete(_vaultPath); } catch (Exception) { /* best effort */ }
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch (Exception) { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// Every production route in the group, in one theory rather than one test each, so a route added to the
    /// list is a one-line change and the shape of the assertion cannot drift between them. Every verb is
    /// here, because the write, the promote and the delete are the tampering half of this defect and a deny
    /// that closed only the reads would leave the damage path open.
    /// </summary>
    [Theory]
    [InlineData("GET", "ingest/recordings", null)]                                                 // list every record
    [InlineData("GET", "ingest/recording/" + SeededId + "/status", null)]                          // one record's status
    [InlineData("GET", "ingest/recording/" + SeededId + "/transcript", null)]                      // the transcript text
    [InlineData("GET", "ingest/recording/" + SeededId + "/audio/0", null)]                         // raw audio - the theft
    [InlineData("POST", "ingest/recording", "{\"recordingId\":\"x\",\"title\":\"t\",\"deviceId\":\"d\",\"startedAt\":\"2026-01-01T00:00:00Z\",\"codec\":\"mp3\",\"sampleRateHz\":16000,\"channels\":1}")]
    [InlineData("PUT", "ingest/recording/" + SeededId + "/chunk/0", "not-audio")]                  // overwrite a chunk
    [InlineData("POST", "ingest/recording/" + SeededId + "/complete", "{}")]
    [InlineData("POST", "ingest/recording/" + SeededId + "/promote", null)]                        // promote into the vault
    [InlineData("PATCH", "ingest/recording/" + SeededId + "/meta", "{\"title\":\"hijacked\"}")]     // overwrite metadata
    [InlineData("DELETE", "ingest/recording/" + SeededId, null)]                                   // destroy the record
    [InlineData("GET", "ingest/dictionary", null)]                                                 // the shared glossary
    [InlineData("PUT", "ingest/dictionary", "{\"vocabulary\":[],\"commonMistranscriptions\":{},\"profiles\":{}}")]
    [InlineData("POST", "ingest/dictionary/terms", "{\"terms\":[\"planted\"]}")]                    // mutate the glossary
    [InlineData("GET", "ingest/agent-info", null)]                                                 // the API guide
    public async Task Every_ingest_route_is_refused_to_an_enrolled_tenant(string method, string path, string? body)
    {
        var resp = await Send(new HttpMethod(method), path, body);
        await AssertBodyIsNothingButTheRefusal(resp);
    }

    [Fact]
    public async Task The_refused_list_did_not_name_the_recording_and_is_not_an_empty_list()
    {
        // Refuse, never serve an empty list: an empty "records" array is a FALSE statement about a box that
        // holds a recording, where an absent one is merely absent. The exact-property assertion below proves
        // there is no records array at all, empty or otherwise.
        var resp = await Send(HttpMethod.Get, "ingest/recordings");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.DoesNotContain(SeededId, await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(SeededTitle, await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await AssertBodyIsNothingButTheRefusal(resp);
    }

    [Fact]
    public async Task The_refused_delete_did_not_take_effect()
    {
        // The status code alone would not prove the destruction was prevented - a handler that ran, deleted,
        // and then reported 404 would pass that. This reads the store back through a fresh service.
        //
        // DESTRUCTIBILITY CONTROL: the identical delete DOES destroy this same seeded recording on self-host
        // (SelfHostRecordingGroupControlTests.The_same_delete_destroys_the_recording_on_self_host), so this
        // is a capable operation being stopped, not a no-op passing by construction.
        var resp = await Send(HttpMethod.Delete, "ingest/recording/" + SeededId);
        await AssertBodyIsNothingButTheRefusal(resp);

        Assert.True(RecordingSeeder.Exists(SeededId), "the seeded recording must survive a refused delete");
    }

    [Fact]
    public async Task The_refused_register_of_a_new_recording_did_not_create_it()
    {
        const string planted = "planted-by-attacker";
        var resp = await Send(HttpMethod.Post, "ingest/recording",
            "{\"recordingId\":\"" + planted + "\",\"title\":\"t\",\"deviceId\":\"d\",\"startedAt\":\"2026-01-01T00:00:00Z\",\"codec\":\"mp3\",\"sampleRateHz\":16000,\"channels\":1}");
        await AssertBodyIsNothingButTheRefusal(resp);

        Assert.False(RecordingSeeder.Exists(planted), "a refused register must not create a recording");
    }

    [Fact]
    public async Task A_verb_the_group_never_mapped_is_also_refused_on_hosted()
    {
        // The primitive maps a VERB-LESS refusal, so a method the family never mapped meets the refusal too -
        // it does not leak the route's existence through a 405. /ingest/recordings is a GET-only route; a
        // DELETE on it was never mapped by any verb, yet the catch-all refuses it.
        var resp = await Send(HttpMethod.Delete, "ingest/recordings");
        await AssertBodyIsNothingButTheRefusal(resp);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_still_rejected()
    {
        // Control: the deny must not have opened the group up as a side effect of running before the
        // host-wide authentication gate. Without a key the middleware still refuses first. GREEN in both
        // directions of the revert on purpose - a control that moves with the change under test is not a
        // control.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("ingest/recordings")).StatusCode);
    }

    /// <summary>
    /// AN ALLOW-LIST, NOT A DENY-LIST, and FORMAT FACTS BEFORE PARSING. Asserting the property set is EXACTLY
    /// one error field inverts a rotting deny-list: anything extra, anything new, anything that leaked reddens
    /// automatically. The status and media type are asserted FIRST so a revert reddens as a STATEMENT -
    /// "expected NotFound, got OK" - rather than as a parser exception on a non-JSON fallback body.
    /// </summary>
    internal static async Task AssertBodyIsNothingButTheRefusal(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        var properties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "error" }, properties);
        Assert.Equal(RefusalMessage, doc.RootElement.GetProperty("error").GetString());
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return _http.SendAsync(req);
    }

    internal static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}

/// <summary>
/// Seeds and inspects a recording through the PRODUCTION writer (<see cref="RecordingIngestService"/>)
/// against the process's <see cref="CcStorage.Transcripts"/> root, so a survival/destructibility assertion
/// is made against exactly the store the endpoint reads and writes - never a hand-authored status file that
/// could drift from what the service actually produces. The background worker is off (a deny test never
/// transcribes) and the transcriber factory throws (it must never be built); neither is reached by Register,
/// GetStatus or DeleteRecording.
/// </summary>
internal static class RecordingSeeder
{
    private static RecordingIngestService NewService() => new(
        CcStorage.Transcripts(),
        transcriberFactory: () => throw new InvalidOperationException("a deny test must never build the transcriber"),
        new CcVaultFiler(CcStorage.VaultTranscripts()),
        CcStorage.VaultTranscripts(),
        runWorker: false);

    public static void Seed(string recordingId, string title)
    {
        var svc = NewService();
        try
        {
            svc.Register(new RecordingRegisterRequest(
                recordingId, title, "seed-device", "2026-01-01T00:00:00Z", "mp3", 16000, 1));
        }
        finally { svc.Dispose(); }
    }

    public static bool Exists(string recordingId)
    {
        var svc = NewService();
        try { svc.GetStatus(recordingId); return true; }
        catch (InvalidOperationException) { return false; }
        finally { svc.Dispose(); }
    }

    public static void Delete(string recordingId)
    {
        var svc = NewService();
        try { svc.DeleteRecording(recordingId); }
        finally { svc.Dispose(); }
    }
}

/// <summary>
/// Boots ONLY the recording-ingest group on an ephemeral port and hands the caller the denied group handle
/// back so a test can map routes through it. That is what makes the future-route proof possible at all: the
/// group is created inside <see cref="RecordingEndpoints.Map"/>, so nothing outside that method could
/// otherwise state a property about routes added to it. Routes handed to <paramref name="mapIntoGroup"/> are
/// RELATIVE to the <c>/ingest</c> prefix, the same way the production routes are.
/// </summary>
internal static class RecordingGroupProbeHost
{
    public static async Task<(WebApplication app, HttpClient http)> StartAsync(
        Action<HostedDenyGroup>? mapIntoGroup = null,
        Action<IEndpointRouteBuilder>? mapOutsideGroup = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        var group = RecordingEndpoints.Map(app);
        mapIntoGroup?.Invoke(group);
        mapOutsideGroup?.Invoke(app);

        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }

    public static async Task AssertBodyIsNothingButTheRefusal(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        var properties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "error" }, properties);
        Assert.Equal(HostedRecordingDenyTests.RefusalMessage, doc.RootElement.GetProperty("error").GetString());
    }
}

/// <summary>
/// THE POINT OF THE WHOLE CHANGE: the hosted refusal covers routes that have not been written yet.
///
/// A guard line repeated in every handler passes exactly the same tests as an exclusive-prefix deny for the
/// routes that exist today, which is why it is dangerous - the difference only shows up on the route somebody
/// adds NEXT, when it is open by default and nothing fails. So this class maps a BRAND-NEW route through the
/// group and asserts it is already refused with no deny of its own written anywhere. The mirror half - the
/// same probe path SERVED with hosted mode explicitly off - is
/// <see cref="SelfHostRecordingGroupControlTests.A_route_added_to_the_group_still_serves_on_self_host"/>: one
/// direction alone cannot tell a working gate from a brick that refuses everything unconditionally.
/// </summary>
[Collection("DirectorRoot")]
public sealed class HostedRecordingGroupFilterTests : IDisposable
{
    private const string ProbePayloadSentinel = "probe-payload-that-must-never-be-served-on-hosted";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string? _priorHosted;

    public HostedRecordingGroupFilterTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "cc-rec-group-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// A route that did not exist when the refusal was written is refused anyway. NOTHING in
    /// <see cref="RecordingEndpoints"/> mentions this path, and no guard is written for it here - the
    /// exclusive catch-all under <c>/ingest</c> is the only thing standing between the caller and the probe
    /// payload. On hosted the handle DISCARDS the probe handler (nothing binds), so the catch-all answers.
    /// </summary>
    [Fact]
    public async Task A_route_added_to_the_group_later_is_refused_on_hosted_with_no_deny_of_its_own()
    {
        var (app, http) = await RecordingGroupProbeHost.StartAsync(
            mapIntoGroup: group => group.MapGet("/added-after-the-deny-was-written",
                () => Results.Json(new { probe = ProbePayloadSentinel })));
        try
        {
            var resp = await http.GetAsync("/ingest/added-after-the-deny-was-written");

            await RecordingGroupProbeHost.AssertBodyIsNothingButTheRefusal(resp);
            Assert.DoesNotContain(ProbePayloadSentinel, await resp.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>
    /// CONTROL: the deny is scoped to the <c>/ingest</c> prefix, not a blanket refusal on the whole
    /// application. A route mapped OUTSIDE the group still serves on hosted, so the passing tests above are
    /// the deny doing its job and not the host refusing everything.
    /// </summary>
    [Fact]
    public async Task A_route_outside_the_group_still_serves_on_hosted()
    {
        var (app, http) = await RecordingGroupProbeHost.StartAsync(
            mapOutsideGroup: routes => routes.MapGet("/not-an-ingest-route", () => Results.Json(new { ok = true })));
        try
        {
            var resp = await http.GetAsync("/not-an-ingest-route");

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }
}

/// <summary>
/// THE SELF-HOST CONTROL ON THE GROUP, in BOTH non-hosted forms, with the effects proven.
///
/// Self-host is the control for this whole mission, so it is PROVEN rather than INHERITED. This class sets
/// <c>CC_GATEWAY_HOSTED</c> itself, to both non-hosted values that occur in practice - absent, and
/// present-but-not-"1" - and asserts the mode took before driving anything. It asserts REAL PAYLOADS AND REAL
/// EFFECTS, not the absence of the refusal string: an empty-but-successful response would satisfy "the
/// refusal is absent" while still being a broken self-host.
///
/// It also carries the DESTRUCTIBILITY CONTROL for the hosted survival assertion: the same delete that is
/// refused on hosted really destroys the seeded recording here. Every test here must stay GREEN through the
/// revert described on <see cref="HostedRecordingDenyTests"/>.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SelfHostRecordingGroupControlTests : IDisposable
{
    private const string SeededId = "the-owners-own-recording";
    private const string SeededTitle = "The owner's own call";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string? _priorHosted;

    public SelfHostRecordingGroupControlTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "cc-rec-selfhost-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>Puts the process into a STATED non-hosted mode and proves it took, so no test below can
    /// silently be running in the mode it thinks it is not in.</summary>
    private static void DeclareSelfHost(string? value)
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", value);
        Assert.False(GatewayHostedMode.IsHosted);
    }

    /// <summary>null = the variable is absent. "0" = present and explicitly not hosted. Both are real
    /// non-hosted deployments and both must serve.</summary>
    public static TheoryData<string?> NonHostedValues => new() { null, "0" };

    /// <summary>
    /// HANDLER-POSITIVE RECEIPT for the list route: the route really exists and really answers with the
    /// owner's recording. A 404 deny is indistinguishable from a route that was never mapped, so without a
    /// receipt like this the hosted 404 would prove nothing about a guard.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task The_owner_still_lists_his_real_recording_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);
        RecordingSeeder.Seed(SeededId, SeededTitle);

        var (app, http) = await RecordingGroupProbeHost.StartAsync();
        try
        {
            var resp = await http.GetAsync("/ingest/recordings");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var ids = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("recordingId").GetString()).ToArray();
            Assert.Contains(SeededId, ids);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>HANDLER-POSITIVE RECEIPT for register: the route really creates a recording on disk.</summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task The_owner_can_register_a_recording_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);
        const string newId = "freshly-registered";

        var (app, http) = await RecordingGroupProbeHost.StartAsync();
        try
        {
            var resp = await http.PostAsync("/ingest/recording", new StringContent(
                "{\"recordingId\":\"" + newId + "\",\"title\":\"t\",\"deviceId\":\"d\",\"startedAt\":\"2026-01-01T00:00:00Z\",\"codec\":\"mp3\",\"sampleRateHz\":16000,\"channels\":1}",
                Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.True(RecordingSeeder.Exists(newId), "register must really create the recording on self-host");
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>
    /// DESTRUCTIBILITY CONTROL for the hosted survival assertion. The hosted test asserts a seeded recording
    /// SURVIVES a refused DELETE; that claim is only meaningful if the same operation is CAPABLE of destroying
    /// it. This drives <see cref="RecordingIngestService.DeleteRecording"/> - the exact method the DELETE
    /// handler invokes (<c>lazyService.Value.DeleteRecording(id)</c>) - and proves the seeded recording is
    /// really gone afterwards. It is driven through the production writer rather than over HTTP on purpose:
    /// the mode gate lives in the route mapping (proven by the hosted refusal + the self-host register/list
    /// receipts above), while THIS control is about the delete's capability, which the service call carries
    /// directly and without racing a freshly-built endpoint worker that is scanning the same directory.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public void The_same_delete_destroys_the_recording(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);
        RecordingSeeder.Seed(SeededId, SeededTitle);
        Assert.True(RecordingSeeder.Exists(SeededId));

        RecordingSeeder.Delete(SeededId);

        Assert.False(RecordingSeeder.Exists(SeededId), "the same DeleteRecording the route invokes must really destroy the recording");
    }

    /// <summary>
    /// The self-host mirror of the future-route probe: the SAME brand-new route mapped through the group
    /// SERVES on self-host, in both non-hosted forms. Paired with
    /// <see cref="HostedRecordingGroupFilterTests.A_route_added_to_the_group_later_is_refused_on_hosted_with_no_deny_of_its_own"/>,
    /// this proves the group is a working gate (refuse on hosted, serve off it) and not a brick that refuses
    /// everything unconditionally.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task A_route_added_to_the_group_still_serves_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);
        const string sentinel = "probe-payload-served-on-self-host";

        var (app, http) = await RecordingGroupProbeHost.StartAsync(
            mapIntoGroup: group => group.MapGet("/added-after-the-deny-was-written",
                () => Results.Json(new { probe = sentinel })));
        try
        {
            var resp = await http.GetAsync("/ingest/added-after-the-deny-was-written");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal(sentinel, doc.RootElement.GetProperty("probe").GetString());
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }
}
