using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1884, the FAIL-OPEN case: a HOSTED Gateway whose dictation endpoint was wired with NO tenant
/// boundary must REFUSE on all five legs, never quietly serve the shared self-host partition.
///
/// Why this is its own test class. The tenant boundary is a SECURITY argument, and it used to be declared
/// optional and nullable (<c>HostedTenantBoundary? tenantBoundary = null</c>) with <c>ResolveTenant</c>
/// answering <see cref="TenantId.Local"/> whenever it was absent - WITHOUT ever asking
/// <see cref="GatewayHostedMode.IsHosted"/>. Correctness therefore rested on every present and future call
/// site remembering a one-word argument. Any hosted call site, test, or rewire that omitted it sent
/// register, chunk, complete, ack and abandon to the base shared root - reopening transcript disclosure,
/// chunk overwrite, ack, abandon and completion-cache joining - and NOTHING failed loud to say so. An
/// optional security argument is indistinguishable from a resolved one at the call site, which is exactly
/// why that mistake survives review.
///
/// TWO DEFENCES, and this class proves the runtime one. The compile-time one is that
/// <c>GatewayDictationEndpoint.Map</c> now takes the boundary as a REQUIRED parameter, so omitting it will
/// not build; the tests below have to force a null through explicitly (<c>null!</c>) to reproduce the
/// miswire at all, which is the point - the accident is now unrepresentable, and even the deliberate
/// version is refused.
///
/// EVERY LEG, not one. A fix proven on one leg says nothing about the other four: each leg calls
/// <c>ResolveTenant</c> itself, so each is its own opportunity to fall back to Local.
///
/// EVERY ABSENCE CLAIM HAS A POSITIVE COMPANION. Each leg asserts the concrete refusal STATUS (403), the
/// exact refusal the deny path - and only the deny path - produces, and the response's property set as an
/// ALLOW-LIST (a substring check cannot see an extra leaked field). On top of that, a secret is seeded in
/// the LOCAL partition under the very same upload id, and each leg is checked to have left it alone: not
/// disclosed, not overwritten, not retired, not abandoned. Before the fix, the register leg alone handed
/// that secret transcript straight back in its terminal-register body.
///
/// AND THE ROUTES ARE PROVEN TO EXIST. A test that drives a verb or path production does not map gets a
/// framework 405/404 BEFORE endpoint selection, so no handler runs and the test passes for the wrong
/// reason. Every request here is replayed against an IDENTICAL harness differing ONLY in the boundary
/// argument (<see cref="Wired"/>), which answers each of them successfully - so a 403 above is the guard
/// refusing, never a route that does not exist.
///
/// REVERT-PROVE: restore <c>ResolveTenant</c> to <c>boundary is null ? TenantId.Local : ...</c> and all five
/// tests below go RED - the unwired legs answer 200 and the register leg hands back the seeded secret.
///
/// The assembly runs sequentially (TestParallelization), so toggling CC_GATEWAY_HOSTED and the storage root
/// here is safe; both are restored in DisposeAsync.
/// </summary>
public sealed class DictationOmittedTenantBoundaryTests : IAsyncLifetime
{
    private const string GatewayToken = "test-token";

    /// <summary>The thing an unwired hosted leg must never read, overwrite, retire, or abandon.</summary>
    private const string LocalSecretTranscript = "self-host-partition-secret-transcript";
    private const string LocalSecretAudio = "self-host-partition-secret-audio";

    /// <summary>The one message the deny path produces. Asserted exactly, so a 403 from anywhere else fails.</summary>
    private const string DenyMessage = "no tenant is bound to this request";

    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "cc-dictation-nobound-" + Guid.NewGuid().ToString("N"));

    private string? _priorHosted;
    private string? _priorRoot;

    /// <summary>The miswired host: hosted mode ON, no tenant boundary. The subject of every test here.</summary>
    private Harness _unwired = null!;

    /// <summary>The identical host WITH the boundary. The control that proves the routes and harness work.</summary>
    private Harness _wired = null!;

    private TenantId _tenant;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _storageRoot);

        // A canonical lowercase minted GUID - the ONE tenant spelling the registry produces and the upload
        // store accepts as a partition name.
        _tenant = new TenantId(Guid.NewGuid().ToString("D"));

        _unwired = await Harness.StartAsync(_storageRoot, "unwired", _tenant, wireBoundary: false);
        _wired = await Harness.StartAsync(_storageRoot, "wired", _tenant, wireBoundary: true);
    }

    public async Task DisposeAsync()
    {
        await _unwired.DisposeAsync();
        await _wired.DisposeAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, true); } catch { /* cleanup */ }
    }

    // ===== leg 1: register =========================================================================

    [Fact]
    public async Task Register_refuses_on_hosted_when_the_boundary_was_omitted()
    {
        // THE CONCRETE DISCLOSURE this class exists for. Seed a delivered upload in the LOCAL partition -
        // the base shared root, where self-host's dictations live - and register that same id against the
        // unwired hosted host. Before the fix the missing boundary resolved to Local, the terminal-register
        // short-circuit fired, and the response body carried the seeded transcript verbatim.
        var id = Guid.NewGuid().ToString();
        LocalStore().MarkDelivered(id, submitted: true, movedOn: false, transcript: LocalSecretTranscript);

        var (status, body) = await Read(await _unwired.RegisterAsync(id));
        AssertRefusal(status, body);

        // The local record is untouched: still delivered, still its own words.
        var record = LocalStore().ReadRecord(id);
        Assert.NotNull(record);
        Assert.Equal(DictationDeliveryState.Delivered, record!.State);
        Assert.Equal(LocalSecretTranscript, record.Transcript);

        // Positive companion: the SAME request on the SAME harness WITH a boundary is served, so the 403
        // above is the guard refusing and not a route that does not exist or a host that answers nothing.
        var wired = await Read(await _wired.RegisterAsync(id));
        Assert.Equal(HttpStatusCode.OK, wired.status);
        Assert.DoesNotContain(LocalSecretTranscript, wired.body, StringComparison.Ordinal);
        var wiredRegister = JsonDocument.Parse(wired.body).RootElement;
        // The fresh-register shape, which ONLY the genuine handler produces: an upload id, and no terminal
        // short-circuit - the local partition's delivered record was not this host's to see.
        Assert.False(string.IsNullOrWhiteSpace(wiredRegister.GetProperty("upload_id").GetString()));
        Assert.False(wiredRegister.TryGetProperty("terminal", out _));
    }

    // ===== leg 2: chunk ============================================================================

    [Fact]
    public async Task Chunk_refuses_on_hosted_when_the_boundary_was_omitted()
    {
        // Staged audio in the local partition. An unwired hosted chunk must not be able to overwrite it,
        // extend it, or even confirm that the upload id exists.
        var id = Guid.NewGuid().ToString();
        var local = LocalStore();
        local.Register(id);
        await local.StoreChunkAsync(id, 0, Encoding.UTF8.GetBytes(LocalSecretAudio), null, default);
        Assert.Equal(LocalSecretAudio, await StagedTextAsync(local, id));

        var (status, body) = await Read(await _unwired.PutChunkAsync(id, 0, "overwritten-by-the-unwired-host"));
        AssertRefusal(status, body);
        Assert.Equal(LocalSecretAudio, await StagedTextAsync(local, id));

        // Positive companion: PUT on this exact path is a real production route and the wired host stores
        // the chunk - into its OWN tenant partition, leaving the local bytes alone.
        await _wired.RegisterAsync(id);
        var wired = await Read(await _wired.PutChunkAsync(id, 0, "wired-host-audio"));
        Assert.Equal(HttpStatusCode.OK, wired.status);
        Assert.True(JsonDocument.Parse(wired.body).RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(LocalSecretAudio, await StagedTextAsync(local, id));
    }

    // ===== leg 3: complete =========================================================================

    [Fact]
    public async Task Complete_refuses_on_hosted_when_the_boundary_was_omitted()
    {
        // The other door onto the same disclosure: complete's cached-terminal-outcome short-circuit. It also
        // reaches the STATIC single-flight cache, which an unwired host would key as "Local|<id>" - joining
        // the self-host partition's in-flight run.
        var id = Guid.NewGuid().ToString();
        LocalStore().MarkDelivered(id, submitted: true, movedOn: false, transcript: LocalSecretTranscript);

        var (status, body) = await Read(await _unwired.CompleteAsync(id));
        AssertRefusal(status, body);

        // Nothing of the local record was disclosed and nothing of it moved.
        var record = LocalStore().ReadRecord(id);
        Assert.NotNull(record);
        Assert.Equal(DictationDeliveryState.Delivered, record!.State);
        Assert.Equal(LocalSecretTranscript, record.Transcript);

        // Positive companion: the wired host reaches its own handler on this exact verb and path. It is NOT
        // refused, and it does not see the local partition's terminal outcome either.
        var wired = await Read(await _wired.CompleteAsync(id));
        Assert.NotEqual(HttpStatusCode.Forbidden, wired.status);
        Assert.NotEqual(HttpStatusCode.MethodNotAllowed, wired.status);
        Assert.DoesNotContain(LocalSecretTranscript, wired.body, StringComparison.Ordinal);
    }

    // ===== leg 4: ack ==============================================================================

    [Fact]
    public async Task Ack_refuses_on_hosted_when_the_boundary_was_omitted()
    {
        // An ack DELETES state - it retires the durable tombstone that stops a delivered turn being injected
        // twice. An unwired hosted host must not be able to retire the self-host partition's tombstone.
        var id = Guid.NewGuid().ToString();
        LocalStore().MarkDelivered(id, submitted: true, movedOn: false, transcript: LocalSecretTranscript);

        var (status, body) = await Read(await _unwired.AckAsync(id));
        AssertRefusal(status, body);
        Assert.NotNull(LocalStore().ReadRecord(id));
        Assert.Equal(LocalSecretTranscript, LocalStore().ReadRecord(id)!.Transcript);

        // Positive companion: the wired host's ack really does run - it answers the handler's own shape and
        // reports retired=false for an id that is not in ITS partition, which is a refusal by partition, not
        // a broken route. The local tombstone is still standing.
        var wired = await Read(await _wired.AckAsync(id));
        Assert.Equal(HttpStatusCode.OK, wired.status);
        var wiredRoot = JsonDocument.Parse(wired.body).RootElement;
        Assert.True(wiredRoot.GetProperty("ok").GetBoolean());
        Assert.False(wiredRoot.GetProperty("retired").GetBoolean());
        Assert.NotNull(LocalStore().ReadRecord(id));
    }

    // ===== leg 5: abandon ==========================================================================

    [Fact]
    public async Task Abandon_refuses_on_hosted_when_the_boundary_was_omitted()
    {
        // Abandon DISCARDS staged audio and clears the session lock. An unwired hosted host must not be able
        // to resolve or discard the self-host partition's live dictation.
        var sessionId = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        var local = LocalStore();
        local.Register(id);
        local.MarkPending(id, sessionId);
        await local.StoreChunkAsync(id, 0, Encoding.UTF8.GetBytes(LocalSecretAudio), null, default);
        Assert.True(local.IsSessionLocked(sessionId));

        var (status, body) = await Read(await _unwired.AbandonAsync(id));
        AssertRefusal(status, body);
        Assert.Equal(DictationDeliveryState.Pending, local.ReadRecord(id)!.State);
        Assert.Equal(LocalSecretAudio, await StagedTextAsync(local, id));
        Assert.True(local.IsSessionLocked(sessionId));

        // Positive companion: the wired host's abandon really does run on this exact verb and path - it
        // answers the handler's own shape, inside its own partition, and the local dictation is still live.
        var wired = await Read(await _wired.AbandonAsync(id));
        Assert.Equal(HttpStatusCode.OK, wired.status);
        Assert.True(JsonDocument.Parse(wired.body).RootElement.GetProperty("abandoned").GetBoolean());
        Assert.Equal(DictationDeliveryState.Pending, local.ReadRecord(id)!.State);
        Assert.True(local.IsSessionLocked(sessionId));
    }

    // ===== the refusal itself ======================================================================

    /// <summary>
    /// The refusal, asserted positively: the concrete status, the exact message only the deny path emits,
    /// and the response's property set as an ALLOW-LIST. The allow-list matters - a substring check for what
    /// must be absent cannot see an EXTRA field that leaked, and the whole hazard here is a body that
    /// carries another partition's state.
    /// </summary>
    private static void AssertRefusal(HttpStatusCode status, string body)
    {
        Assert.Equal(HttpStatusCode.Forbidden, status);
        var root = JsonDocument.Parse(body).RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(new[] { "error" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(DenyMessage, root.GetProperty("error").GetString());
        Assert.DoesNotContain(LocalSecretTranscript, body, StringComparison.Ordinal);
        Assert.DoesNotContain(LocalSecretAudio, body, StringComparison.Ordinal);
    }

    // ===== helpers =================================================================================

    /// <summary>The LOCAL (base, shared, self-host) partition - the one an unwired hosted leg would take.</summary>
    private VoiceUploadStore LocalStore() => new VoiceUploadStore(Harness.UploadRoot(_storageRoot));

    private static async Task<string> StagedTextAsync(VoiceUploadStore store, string uploadId)
    {
        var assembled = await store.AssembleAsync(uploadId, 1);
        return assembled.Audio is null ? "" : Encoding.UTF8.GetString(assembled.Audio);
    }

    private static async Task<(HttpStatusCode status, string body)> Read(HttpResponseMessage resp)
    {
        using (resp) return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// A minimal host that maps ONLY the dictation endpoint. The two instances are built by the same code
    /// with the same dependencies and differ in EXACTLY ONE argument - whether the tenant boundary is
    /// supplied - so a difference in behaviour between them can only be that argument.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _http;

        private Harness(WebApplication app, HttpClient http)
        {
            _app = app;
            _http = http;
        }

        internal static string UploadRoot(string storageRoot) => Path.Combine(storageRoot, "dictation-uploads");

        internal static async Task<Harness> StartAsync(string storageRoot, string name, TenantId tenant, bool wireBoundary)
        {
            var devices = new DeviceRegistry(Path.Combine(storageRoot, name, "devices.json"));
            var deviceKey = devices.Register("dev-" + name, "M-" + name).DeviceKey;
            devices.SetAccountBinding("dev-" + name, "sub-" + name, tenant.Value);

            // BOTH harnesses share ONE upload root, so "the unwired host did not touch the local partition"
            // is a claim about the very bytes the wired host would have to reach through its own partition.
            var uploads = new VoiceUploadStore(UploadRoot(storageRoot));

            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Urls.Add("http://127.0.0.1:0");

            var transcription = new GatewayTranscriptionService(
                new KeyVault(Path.Combine(storageRoot, name, "keyvault.json")));

            // The ONE argument under test. The unwired case has to be forced through with null! because the
            // parameter is REQUIRED - the compile-time half of the fix - so the accidental version of this
            // miswire cannot be written at all.
            HostedTenantBoundary? boundary = wireBoundary
                ? new HostedTenantBoundary(new AsyncLocalTenantContext(), devices)
                : null;

            GatewayDictationEndpoint.Map(app,
                new DirectorRegistry(Path.Combine(storageRoot, name, "instances")),
                owners: null,
                token: GatewayToken,
                transcription,
                new TranscribingSessions(),
                uploads,
                devices,
                boundary!);

            await app.StartAsync();
            var http = new HttpClient
            {
                BaseAddress = new Uri(app.Urls.First()),
                Timeout = TimeSpan.FromSeconds(30),
            };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceKey);
            return new Harness(app, http);
        }

        // The verbs and paths below are the production ones, and the wired harness answering each of them
        // is what proves it: a verb or path production does not map is answered 405/404 by the framework
        // BEFORE endpoint selection, so no handler would run on either harness.
        internal async Task<HttpResponseMessage> RegisterAsync(string uploadId, string? sessionId = null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/dictation/upload")
            {
                Content = JsonContent.Create(new { sessionId = sessionId ?? Guid.NewGuid().ToString(), baselineBufferBytes = 0 }),
            };
            req.Headers.Add("Idempotency-Key", uploadId);
            return await _http.SendAsync(req);
        }

        internal async Task<HttpResponseMessage> PutChunkAsync(string uploadId, int index, string text)
        {
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return await _http.PutAsync($"/dictation/{uploadId}/chunk/{index}", content);
        }

        internal Task<HttpResponseMessage> CompleteAsync(string uploadId)
            => _http.PostAsJsonAsync($"/dictation/{uploadId}/complete",
                new { sessionId = Guid.NewGuid().ToString(), totalChunks = 1 });

        internal Task<HttpResponseMessage> AckAsync(string uploadId)
            => _http.PostAsync($"/dictation/{uploadId}/ack", content: null);

        internal Task<HttpResponseMessage> AbandonAsync(string uploadId)
            => _http.PostAsync($"/dictation/{uploadId}/abandon", content: null);

        public async ValueTask DisposeAsync()
        {
            _http.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
