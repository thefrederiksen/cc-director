using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE MAPPED ROUTE, DRIVEN. <c>POST /wingman/transcribe</c> on a hosted Gateway must REFUSE a caller whose
/// tenant does not resolve, never serve it - and this test is the only thing in the repository that drives
/// that route at all.
///
/// WHY THIS EXISTS RATHER THAN ANOTHER SERVICE-LEVEL TEST. The route used to pass the nullable result of
/// <c>GatewayEndpoints.ResolveReadTenant</c> straight into
/// <see cref="GatewayTranscriptionService.TranscribeAsync"/>. That resolver's own documentation says null is a
/// REFUSAL; the service's contract says null means the self-host single tenant. Handing one to the other
/// turned the refusal into <see cref="TenantId.Local"/> three layers down, with three consequences on a
/// hosted request - the SHARED FLAT GLOSSARY biased the caller's words, the shared history took their turn,
/// and their transcript evidence landed in the Local partition.
///
/// Every existing test of that behaviour constructs <see cref="GatewayTranscriptionService"/> DIRECTLY and
/// never maps the route, so all of them passed with this route wide open. A guard written the same way would
/// be worth exactly as much. So this one boots a real <see cref="GatewayHost"/>, authenticates with a real
/// device key through the real host-wide auth gate, and posts real multipart audio at the real path.
///
/// HOW THE UNRESOLVABLE TENANT IS PRODUCED, STATED EXACTLY, BECAUSE IT IS NOT THE OBVIOUS ONE.
/// <c>ResolveReadTenant</c> answers null on hosted in two documented cases: a key with no bound tenant, and
/// A BOUNDARY THAT IS NOT HOSTED-WIRED. The first is NOT reachable through a hosted host's front door - on
/// hosted, <c>DeviceRegistry.ResolveCredential</c> classifies a device row with no tenant binding as REVOKED,
/// so the auth gate answers 401 before any route runs (that is what
/// <see cref="HostedContentReadDenyTests.Every_family_is_refused_to_a_caller_carrying_no_tenant_at_all"/>
/// pins). The second is: the host is constructed while the process is not hosted, so its boundary is built
/// over the single-tenant context, and hosted mode - which is read LIVE from the environment - is on by the
/// time the request arrives. <c>ResolveReadTenant</c> then returns null WITHOUT consulting the boundary, and
/// the caller is authenticated, which is precisely the shape the guard has to answer.
///
/// WHAT THIS PROVES AND WHAT IT DOES NOT. It proves that when the route's tenant resolution answers null for
/// an authenticated hosted caller, the route refuses and NOTHING downstream runs. It does not claim the
/// unbound-device-key route into that null is reachable on the production hosted Gateway - it is not, today,
/// because authentication refuses it first. The guard is the contract's own floor, and the second half of
/// this file is what makes the floor load bearing.
///
/// THE STORE RECEIPT IS THE POINT, NOT THE STATUS CODE. A vault key IS configured here on purpose. Without
/// one the route answers 503 at its own key check before ever reaching the transcription owner, and every
/// "nothing was written" assertion below would be green whether the guard existed or not - decorative, not
/// load bearing. With the key set, removing the guard drives the request all the way into
/// <see cref="GatewayTranscriptionService.TranscribeAsync"/>, and the shared transcription history takes a
/// record. That receipt changes colour when the guard is removed, which is the only thing that makes it
/// evidence rather than decoration.
///
/// WHICH ASSERTIONS ARE PROVEN ABLE TO FAIL, AND WHICH ARE NOT. Stated because the difference is the whole
/// value of the file:
///   PROVEN able to fail - the refusal body and status, and the SHARED/LOCAL TRANSCRIPTION HISTORY being
///   empty. Both were watched going red with the guard removed, on this branch.
///   NOT proven able to fail - the transcript store's Local row count, and the flat glossary's terms being
///   absent from the answer. Both of those consequences sit behind a SUCCESSFUL provider transcription: the
///   store append is skipped when the provider produced no text, and the glossary is only read on the
///   success path. This route builds its own transcription service against a compile-time base URL, so a
///   test cannot stub the provider, and a success cannot be produced here. They are asserted because they
///   are the right things to assert and they are cheap - but this file does NOT claim to have watched them
///   redden. What IS established is upstream of all three: the service is never entered.
///   DELIBERATELY ABSENT - "the clip was not archived". <see cref="TranscriptionAudioArchive.TrySave"/>
///   skips every write on hosted by its own gate, so that assertion could never fail in either direction. An
///   assertion that cannot fail reads as proof and is not; it was removed rather than left in.
///
/// REVERT-PROOF RECIPE (run it; do not take the assertion on trust):
///   1. In <c>GatewayWingmanVoiceEndpoint.cs</c>, delete the <c>reqTenant is null</c> refusal at the top of
///      the <c>/wingman/transcribe</c> handler (and restore the old
///      <c>tenant: GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary)</c> argument).
///   2. Run this class. It goes RED on the refusal - the answer becomes the transcription service's own
///      provider error - and, with the refusal assertions temporarily moved aside so the receipt fails
///      first, RED on the history being non-empty. Both were observed on this branch.
///   3. Restore. Green again.
/// Note that step 2 makes ONE outbound provider call with a fake key, for the reason given above. The
/// committed, guarded test makes no network call at all - it is refused before the service is entered.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedWingmanTranscribeTenantRefusalTests : IAsyncLifetime
{
    private const string Token = "test-token";

    /// <summary>The exact refusal every sibling route on this surface returns for an unresolved tenant.</summary>
    private const string Refusal = "no tenant is bound to this request";

    /// <summary>A term only the SHARED FLAT glossary carries, so its presence anywhere would name that file.</summary>
    private const string FlatGlossaryTerm = "zqxjvflatglossary";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _key = "";

    /// <summary>The clip posted at the route - real bytes in a real multipart part, so the request is the
    /// shape the phone actually sends and not a body the handler rejects before the guard is reached.</summary>
    private readonly byte[] _clip = Encoding.UTF8.GetBytes("zqxjv-clip-" + Guid.NewGuid().ToString("N"));

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-transcribe-refusal-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string? _prevRoot;
    private string? _priorHosted;

    public HostedWingmanTranscribeTenantRefusalTests()
    {
        // The isolated storage root. Everything the refused request could have written - the glossary it
        // would have read, the audio archive, the transcription history - resolves under here, so a write
        // that should not happen is observable rather than lost in the developer's own folders.
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-transcribe-refusal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        // Hosted mode is OFF while the host is constructed, ON before the request. See the class comment:
        // this is what produces an authenticated caller whose tenant does not resolve.
        _priorHosted = Environment.GetEnvironmentVariable(GatewayHostedMode.HostedEnvVar);
        Environment.SetEnvironmentVariable(GatewayHostedMode.HostedEnvVar, null);

        // A key for the configured transcription mode, so the route's own no-key check cannot be what
        // answers. Without this the receipts below prove nothing - see the class comment.
        var vaultPath = Path.Combine(_root, "keyvault.json");
        TranscriptionModeConfig.Set(TranscriptionMode.DevThrottle);
        new KeyVault(vaultPath).Set(TranscriptionEndpointResolver.DevThrottleKeyName, "dt_live_notarealkey");

        // The SHARED FLAT glossary - the Local tenant's file, the one a null tenant reads. Seeded so it
        // exists and carries a term nothing else in this test could produce.
        var flat = GatewayTranscriptionService.DictionaryPath();
        Directory.CreateDirectory(Path.GetDirectoryName(flat)!);
        File.WriteAllText(flat, $"common_mistranscriptions:\n  {FlatGlossaryTerm}: [somethingelse]\n");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            keyVaultPath: vaultPath,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // A real, active device key. It authenticates: the registry was built while this process was not
        // hosted, so it does not apply the hosted binding check that would revoke it.
        _key = _gateway.Devices.Register("dev-a", "MA").DeviceKey;

        // PRECONDITION, asserted rather than assumed: nothing has been written yet, so a non-empty receipt
        // after the request can only have come from the request.
        Assert.Empty(HistoryFiles());
        Assert.Equal(0, _gateway.Transcripts.Count(TenantId.Local));

        // And now the process is hosted.
        Environment.SetEnvironmentVariable(GatewayHostedMode.HostedEnvVar, "1");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable(GatewayHostedMode.HostedEnvVar, _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Hosted_transcribe_with_no_resolvable_tenant_is_refused_and_reaches_nothing()
    {
        var resp = await PostClipAsync();

        // 1. THE REFUSAL, body first. The status is asserted after the fingerprint deliberately: the Cockpit
        //    single-page-app fallback answers any unclaimed path, so a status-first assertion would redden on
        //    "404 is not 403" if the route were renamed, which proves a route moved rather than proving this
        //    test can tell the guard's answer from a masking route's.
        var root = await ContentFingerprint.AsJsonObjectAsync(resp, "POST wingman/transcribe");
        Assert.Equal(new[] { "error" }, root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(Refusal, ContentFingerprint.Text(root, "error", "POST wingman/transcribe"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        // 2. THE TRANSCRIPTION OWNER WAS NEVER ENTERED - THE ONE RECEIPT THAT IS PROVEN ABLE TO FAIL.
        //    RecordHistory runs on EVERY TranscribeAsync outcome, including a failed provider call, and with a
        //    null tenant it selects the injected shared history - which is the Local directory. So an empty
        //    history is the receipt that the service was not reached at all, and therefore that the glossary
        //    read and the transcript write below could not have happened either. Watched going red with the
        //    guard removed; see the recipe in the class comment.
        Assert.Empty(HistoryFiles());

        // 3. THE OTHER LOCAL STORE. The Local partition of the transcript store is where a null tenant's
        //    transcript evidence lands. NOT proven able to fail here - the append is skipped when the provider
        //    produced no text, and this test cannot produce a successful transcription. Asserted anyway,
        //    because it is the right thing to assert and it is free; not offered as proof.
        Assert.Equal(0, _gateway.Transcripts.Count(TenantId.Local));

        // 4. THE SHARED FLAT GLOSSARY WAS NOT APPLIED. A refusal carries no transcript at all, so there is no
        //    text for the flat file's terms to have altered - and the file is still exactly as seeded, so
        //    nothing rewrote it. Also NOT proven able to fail, for the same reason as 3: the glossary is read
        //    only on the success path. Receipt 2 is what actually rules the read out, by proving the code
        //    that performs it never ran.
        Assert.DoesNotContain(FlatGlossaryTerm, await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains(FlatGlossaryTerm, File.ReadAllText(GatewayTranscriptionService.DictionaryPath()));
    }

    /// <summary>
    /// THE SELF-HOST CONTROL, and it is not optional: a guard that refused everybody would satisfy the test
    /// above perfectly. Off hosted mode, <c>ResolveReadTenant</c> answers <see cref="TenantId.Local"/> for
    /// every authenticated caller, so the SAME request on the SAME host must get past the guard.
    ///
    /// It is proved by the answer that comes from FURTHER IN than the guard - the route's own bad-body
    /// message, which nothing else in the Gateway emits and which is reached only after the tenant check.
    /// A body-shape refusal is used rather than a full transcription because completing one would mean a
    /// real provider call: this route builds its own transcription service, so there is no client to stub.
    /// </summary>
    [Fact]
    public async Task Self_host_transcribe_is_not_refused_by_the_tenant_guard()
    {
        Environment.SetEnvironmentVariable(GatewayHostedMode.HostedEnvVar, null);

        var req = new HttpRequestMessage(HttpMethod.Post, "wingman/transcribe");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req);

        var root = await ContentFingerprint.AsJsonObjectAsync(resp, "POST wingman/transcribe (self-host)");
        var error = ContentFingerprint.Text(root, "error", "POST wingman/transcribe (self-host)");
        Assert.Equal("send the recording as multipart form-data with an 'audio' file", error);
        Assert.NotEqual(Refusal, error);
    }

    private Task<HttpResponseMessage> PostClipAsync()
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(_clip);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/webm");
        form.Add(file, "audio", "clip.webm");

        var req = new HttpRequestMessage(HttpMethod.Post, "wingman/transcribe")
        {
            Content = form,
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        return _http.SendAsync(req);
    }

    /// <summary>Every file the shared (Local) transcription history holds. Empty is the receipt.</summary>
    private static string[] HistoryFiles()
    {
        var dir = TranscriptionHistoryLog.DefaultDirectory();
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
    }
}
