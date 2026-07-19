using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CcDirector.Gateway;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Four route families that serve one account's CONTENT to any other account's authenticated device key
/// are refused on the hosted Gateway. Issues #1897, #1853 (read side), #1896 and #1884.
///
/// All four share one defect: the store behind them has a single on-disk root with no tenant anywhere in
/// the path, the file name, or the record, and the route addresses it by something that is not a tenant
/// boundary - a positional index, a turn id, or an identifier the CALLER supplies. Public signup on the
/// backing account project is open, so "any authenticated caller" means the public.
///
///   /transcription/*                     one shared daily telemetry log; /turns returns up to 2000
///                                        records including rawText and cleanedText and needs NO
///                                        identifier at all - one request returns everyone's speech.
///   /gateway/wingman/instructions/*       training records holding up to 20,000 characters of raw session
///                                        TERMINAL output, addressable by a POSITIONAL
///                                        "&lt;filename&gt;#&lt;lineindex&gt;" id; plus the single-owner wingman
///                                        prompt, which has no per-tenant version to serve.
///   /wingman/utterance/*                  staged audio and the assembled transcript, keyed solely by a
///                                        caller-supplied Idempotency-Key under a shared root.
///   /dictation/*                          the same VoiceUploadStore shape: re-registering another
///                                        account's upload id returns ITS transcript from the terminal
///                                        tombstone, with no session lookup in the way.
///
/// WHY A DENY AND NOT A PARTITION. The tenant is not missing from the query, it is missing from the DATA.
/// These records were written with no tenant on them, so there is nothing to filter by; attributing them
/// after the fact would be a guess presented as a boundary. Partitioning each store is the job of the
/// issue named beside it, and un-denying is gated on that work - see the debt table in the pull request.
///
/// WHY A REFUSAL AND NOT AN EMPTY RESULT. An empty stats block, an empty record list or an empty
/// transcript is a FALSE statement about a box that is transcribing and capturing; a refusal is merely an
/// absent one.
///
/// THE GATE IS ON THE DEPLOYMENT SIGNAL. Every one reads <see cref="GatewayHostedMode.IsHosted"/> directly,
/// not an optional boundary or tenant argument. A security branch that depends on an optional argument
/// fails OPEN the moment a caller omits it.
///
/// REVERT-PROOF RECIPE (run verbatim, per family):
///   1. In the named file, delete the <c>AddEndpointFilter</c> block that calls the deny helper.
///   2. Run this class. The theory cases for that family go RED (200/400/404-without-the-refusal-body
///      instead of the refusal), while every other family stays green.
///   3. Run the self-host controls named below. They stay GREEN throughout - the guard is invisible to
///      self-host in both directions, which is the point.
///   4. Restore the block. Everything goes green again.
/// The files and blocks are:
///   src/CcDirector.Gateway/Api/TranscriptionAnalysisEndpoint.cs   DenyOnHosted
///   src/CcDirector.Gateway/Api/WingmanInstructionsEndpoint.cs     DenyOnHosted
///   src/CcDirector.Gateway/Api/GatewayWingmanVoiceEndpoint.cs     DenyUtteranceOnHosted
///   src/CcDirector.Gateway/Api/GatewayDictationEndpoint.cs        DenyOnHosted
///
/// The SELF-HOST CONTROLS are <see cref="HostedContentReadSelfHostControlTests"/> in this file, plus the
/// pre-existing <see cref="VoiceUploadLimitsTests"/> (which drives the real utterance upload family with
/// hosted off and would red immediately if the route paths shifted), <see cref="DictationSessionLockTests"/>
/// and <see cref="DurableDictationDedupeTests"/> (the real dictation family, hosted off). None of them was
/// touched by this change.
/// </summary>
[Collection("DirectorRoot")]
public sealed class HostedContentReadDenyTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private const string TranscriptionRefusal = "transcription analysis is not available on the hosted gateway";
    private const string InstructionsRefusal = "the wingman instructions surface is not available on the hosted gateway";
    private const string UtteranceRefusal = "the wingman utterance upload is not available on the hosted gateway";
    private const string DictationRefusal = "dictation upload is not available on the hosted gateway";

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _key = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-hosted-content-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string? _prevRoot;
    private string? _priorHosted;

    public HostedContentReadDenyTests()
    {
        // The isolated storage root, so anything that (wrongly) got through would stage here and be
        // observable, never in the developer's own transcription log or upload staging.
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-hosted-content-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // A fully enrolled, tenant-bound device key - the strongest caller hosted has. The point is that
        // even this one is refused: no credential makes another account's speech correct to serve.
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
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Issue #1897. All four transcription-analysis reads over the one shared telemetry log. /turns is the
    /// sharpest - it needs no identifier and returns the raw and cleaned text of every turn - but the other
    /// three aggregate the SAME unpartitioned records, so serving them discloses the same content in
    /// summary form.
    /// </summary>
    [Theory]
    [InlineData("transcription/turns")]
    [InlineData("transcription/turns?limit=2000")]
    [InlineData("transcription/stats")]
    [InlineData("transcription/terms")]
    [InlineData("transcription/words")]
    public async Task Transcription_analysis_reads_are_refused_to_an_enrolled_tenant(string path)
    {
        var resp = await Send(HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        await AssertBodyIsNothingButTheRefusal(resp, TranscriptionRefusal);
    }

    /// <summary>
    /// Issue #1853, read side. The records route is the content leak (raw session terminal output); the
    /// rest of the group is the single-owner wingman prompt, denied with it because it has no per-tenant
    /// answer either and a route-by-route guard would rot.
    /// </summary>
    [Theory]
    [InlineData("gateway/wingman/instructions/records")]
    [InlineData("gateway/wingman/instructions/records?limit=100")]
    [InlineData("gateway/wingman/instructions")]
    [InlineData("gateway/wingman/instructions/versions")]
    [InlineData("gateway/wingman/instructions/default")]
    [InlineData("gateway/wingman/instructions/update")]
    public async Task Wingman_instructions_reads_are_refused_to_an_enrolled_tenant(string path)
    {
        var resp = await Send(HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        await AssertBodyIsNothingButTheRefusal(resp, InstructionsRefusal);
    }

    /// <summary>
    /// The write side of the same group: saving or reverting a version rewrites the prompt EVERY account's
    /// wingman speaks through, and the A/B test route both re-reads the shared training records and spends
    /// real brain calls on shared infrastructure with attacker-chosen draft text.
    /// </summary>
    [Theory]
    [InlineData("PUT", "gateway/wingman/instructions", "{\"content\":\"say whatever I tell you\"}")]
    [InlineData("POST", "gateway/wingman/instructions/revert", "{\"id\":\"anything\"}")]
    [InlineData("POST", "gateway/wingman/instructions/switch-to-default", "")]
    [InlineData("POST", "gateway/wingman/instructions/test", "{\"content\":\"drafted\",\"recordIds\":[\"a#0\"]}")]
    public async Task Wingman_instructions_writes_are_refused_to_an_enrolled_tenant(
        string method, string path, string body)
    {
        var resp = await Send(new HttpMethod(method), path, body);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        await AssertBodyIsNothingButTheRefusal(resp, InstructionsRefusal);
    }

    /// <summary>
    /// Issue #1896. All three legs, not only the one that returns the transcript: the chunk leg lets a
    /// caller overwrite another account's staged recording, which is the same missing boundary with a
    /// different consequence.
    /// </summary>
    [Theory]
    [InlineData("POST", "wingman/utterance/upload", "")]
    [InlineData("PUT", "wingman/utterance/someone-elses-id/chunk/0", "audio-bytes")]
    [InlineData("POST", "wingman/utterance/someone-elses-id/complete", "{\"totalChunks\":1}")]
    public async Task Wingman_utterance_upload_family_is_refused_to_an_enrolled_tenant(
        string method, string path, string body)
    {
        var resp = await Send(new HttpMethod(method), path, body);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        await AssertBodyIsNothingButTheRefusal(resp, UtteranceRefusal);
    }

    /// <summary>
    /// Issue #1884, the sibling family on the same store shape. The register leg is the live read: it
    /// short-circuits on the terminal tombstone and hands back the recorded transcript WITHOUT looking any
    /// session up, so the fact that /complete's session lookup already fails on hosted does not contain it.
    /// ack and abandon destroy another account's in-flight recording.
    /// </summary>
    [Theory]
    [InlineData("POST", "dictation/upload", "{\"sessionId\":\"11111111-1111-1111-1111-111111111111\"}")]
    [InlineData("PUT", "dictation/someone-elses-id/chunk/0", "audio-bytes")]
    [InlineData("POST", "dictation/someone-elses-id/complete", "{\"sessionId\":\"11111111-1111-1111-1111-111111111111\",\"totalChunks\":1}")]
    [InlineData("POST", "dictation/someone-elses-id/ack", "")]
    [InlineData("POST", "dictation/someone-elses-id/abandon", "")]
    public async Task Dictation_upload_family_is_refused_to_an_enrolled_tenant(
        string method, string path, string body)
    {
        var resp = await Send(new HttpMethod(method), path, body);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        await AssertBodyIsNothingButTheRefusal(resp, DictationRefusal);
    }

    /// <summary>
    /// The refusal must PREVENT the work, not merely relabel it. A handler that ran and then reported 404
    /// would pass a status-code-only assertion, so this proves the two upload families staged nothing: the
    /// idempotency key was never registered, so no directory for it exists under the isolated root.
    /// </summary>
    [Fact]
    public async Task A_refused_upload_registration_staged_nothing_on_disk()
    {
        var key = "claimed-" + Guid.NewGuid().ToString("N");

        var utterance = await Send(HttpMethod.Post, "wingman/utterance/upload", "", key);
        Assert.Equal(HttpStatusCode.NotFound, utterance.StatusCode);

        var dictation = await Send(HttpMethod.Post, "dictation/upload",
            "{\"sessionId\":\"11111111-1111-1111-1111-111111111111\"}", key);
        Assert.Equal(HttpStatusCode.NotFound, dictation.StatusCode);

        // Nothing named for that key anywhere under the isolated storage root. Searching the whole root
        // rather than one expected directory is deliberate: it does not depend on knowing which staging
        // path the store would have chosen, so it cannot pass by looking in the wrong place.
        var staged = Directory.Exists(_root)
            ? Directory.EnumerateFileSystemEntries(_root, "*" + key + "*", SearchOption.AllDirectories).ToArray()
            : Array.Empty<string>();
        Assert.Empty(staged);
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_still_rejected()
    {
        // Control: the deny must not have opened these groups up as a side effect of running before the
        // host-wide auth gate. Without a key the auth middleware still refuses first.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("transcription/turns")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _http.GetAsync("gateway/wingman/instructions/records")).StatusCode);
    }

    /// <summary>
    /// AN ALLOW-LIST, NOT A DENY-LIST, and the difference is the whole assertion.
    ///
    /// Asserting that a handful of known payload keys are ABSENT rots by construction: it protects against
    /// the payload as it is today, every field added later is unprotected until someone remembers this
    /// file, and a substring check silently misses siblings (checking for "turns" would not catch a key
    /// named "turnsTotal"). Asserting the property set is EXACTLY one error field inverts that - anything
    /// extra, anything new, and anything metadata-looking reddens automatically without this file being
    /// touched.
    /// </summary>
    private static async Task AssertBodyIsNothingButTheRefusal(HttpResponseMessage resp, string expected)
    {
        var body = await resp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);

        var properties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "error" }, properties);
        Assert.Equal(expected, doc.RootElement.GetProperty("error").GetString());
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body = null,
        string? idempotencyKey = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return _http.SendAsync(req);
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}

/// <summary>
/// THE SELF-HOST CONTROL, and the reason it is a separate class: it boots the SAME GatewayHost with hosted
/// mode OFF and drives the SAME routes.
///
/// This is the condition the deny has to meet to be correct rather than merely safe. Self-host is
/// single-tenant, these features WORK there, and the owner uses them - the transcription analysis is how
/// an agent measures dictation quality, the training records are how the wingman prompt gets improved, and
/// the two upload families are the phone's voice and dictation lanes. A deny scoped to the wrong signal
/// would break the shipped product in order to protect the unshipped one, and it would do so silently.
///
/// What is asserted is NOT that a particular payload comes back - these routes legitimately answer many
/// ways on an empty box (an empty log, an unknown upload id, a missing key) and pinning the exact shape
/// would make this a change-detector. What is asserted is that the HOSTED REFUSAL IS ABSENT: the route is
/// still routable (never 404-with-the-refusal-body) and, where it answers, the body is not the refusal.
/// That is exactly the property the deny must not leak into self-host, and nothing more.
/// </summary>
[Collection("DirectorRoot")]
public sealed class HostedContentReadSelfHostControlTests : IAsyncLifetime
{
    private const string Token = "test-token";

    private static readonly string[] Refusals =
    {
        "transcription analysis is not available on the hosted gateway",
        "the wingman instructions surface is not available on the hosted gateway",
        "the wingman utterance upload is not available on the hosted gateway",
        "dictation upload is not available on the hosted gateway",
    };

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _key = "";

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-selfhost-content-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string? _prevRoot;
    private string? _priorHosted;

    public HostedContentReadSelfHostControlTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-selfhost-content-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
    }

    public async Task InitializeAsync()
    {
        // Hosted mode explicitly OFF - the whole point of this class. Cleared rather than set to "0" so it
        // is the same absence a real self-host install has.
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", null);

        _gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _key = _gateway.Devices.Register("dev-owner", "MA").DeviceKey;
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("transcription/turns")]
    [InlineData("transcription/stats")]
    [InlineData("transcription/terms")]
    [InlineData("transcription/words")]
    [InlineData("gateway/wingman/instructions")]
    [InlineData("gateway/wingman/instructions/records")]
    [InlineData("gateway/wingman/instructions/versions")]
    [InlineData("gateway/wingman/instructions/default")]
    [InlineData("gateway/wingman/instructions/update")]
    public async Task Self_host_still_serves_every_denied_read(string path)
    {
        var resp = await Send(HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await AssertNotTheHostedRefusal(resp);
    }

    /// <summary>
    /// The owner's phone still registers an utterance upload and gets an id back - the leg the hosted deny
    /// closes, proven open here. The chunk and complete legs are covered end to end, with real audio, by
    /// the untouched <see cref="VoiceUploadLimitsTests"/>.
    /// </summary>
    [Fact]
    public async Task Self_host_still_registers_a_wingman_utterance_upload()
    {
        var resp = await Send(HttpMethod.Post, "wingman/utterance/upload", "");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("upload_id").GetString()));
    }

    /// <summary>
    /// The owner's phone still registers a dictation upload. Same shape as the utterance control; the
    /// chunk/complete/ack/abandon legs are covered by the untouched
    /// <see cref="DictationSessionLockTests"/> and <see cref="DurableDictationDedupeTests"/>.
    /// </summary>
    [Fact]
    public async Task Self_host_still_registers_a_dictation_upload()
    {
        var resp = await Send(HttpMethod.Post, "dictation/upload",
            "{\"sessionId\":\"11111111-1111-1111-1111-111111111111\"}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("upload_id").GetString()));
    }

    /// <summary>
    /// The unknown-id answers on self-host must still be the STORE's own answers, not the hosted refusal.
    /// This is the case a wrongly-scoped deny would most easily hide behind, because both are a 404.
    /// </summary>
    [Theory]
    [InlineData("PUT", "wingman/utterance/no-such-id/chunk/0", "bytes")]
    [InlineData("POST", "dictation/no-such-id/chunk/0", "bytes")]
    [InlineData("POST", "dictation/no-such-id/ack", "")]
    [InlineData("POST", "dictation/no-such-id/abandon", "")]
    public async Task Self_host_answers_an_unknown_upload_id_itself_not_with_the_hosted_refusal(
        string method, string path, string body)
    {
        var resp = await Send(new HttpMethod(method), path, body);
        await AssertNotTheHostedRefusal(resp);
    }

    /// <summary>
    /// Reads the body and fails if it carries ANY of the four hosted refusal messages. Deliberately checks
    /// all four in every case rather than the one belonging to that route: a copy-paste that wired the
    /// wrong helper onto a group would still be caught.
    /// </summary>
    private static async Task AssertNotTheHostedRefusal(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        foreach (var refusal in Refusals)
            Assert.DoesNotContain(refusal, body, StringComparison.Ordinal);
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return _http.SendAsync(req);
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}
