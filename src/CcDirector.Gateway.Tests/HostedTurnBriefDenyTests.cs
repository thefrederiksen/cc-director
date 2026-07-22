using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Gateway;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

// ============================================================================
// MTR audit gap H5 - the turn-brief surface is DENIED IN WHOLE on the hosted Gateway.
//
// The store behind these routes (GatewayTurnBriefStore) addresses briefs, explain reports,
// packages and feedback by BARE session id under one shared directory, with no tenant in any
// path, file name, or record. The read routes resolve NO tenant and prove NO session ownership,
// and POST feedback overwrites a caller-named record without proving ownership. Issue #549 retired
// the only writer, so the store is legacy read-only data that cannot be attributed to a tenant
// after the fact - which is why the fix is a DENY (quarantine-not-serve), not a half-partition.
//
// The reproduction is faithful because these routes carry no tenant awareness at all: whatever a
// PRIOR account left under bare session id S is exactly what ANY hosted caller receives for S. The
// self-host control class proves the same routes really serve that data (so the hosted 404 is a
// gate firing, not a route that never existed), and the same feedback POST really overwrites a
// record (so the hosted survival assertion is a capable operation being stopped).
//
// REVERT-PROOF - the recipe to RUN, not describe. In
// src/CcDirector.Gateway/Api/TurnBriefGatewayEndpoints.cs change
//   var group = HostedRouteDeny.Group(app, "", Denial());
// so the family maps its real handlers on hosted too - the simplest such mutation returns an
// unguarded group (e.g. build a HostedDenyGroup that maps handlers regardless of mode, or map the
// handlers on `app` directly). Rebuild, CONFIRM ZERO ERRORS (a run after a failed build executes the
// previous binary and reports a false pass), then run this file and record every red BY NAME:
// Every_turnbrief_route_is_refused flips to "expected NotFound, got OK", and
// The_refused_feedback_did_not_overwrite_the_seeded_record reddens because the seeded vote/reason is
// really changed. A red only counts if it fails WITH THE SYMPTOM - an assertion naming what was
// served or changed - not a crash.
// ============================================================================
[Collection("DirectorRoot")]
public sealed class HostedTurnBriefDenyTests : IAsyncLifetime
{
    internal const string RefusalMessage = TurnBriefGatewayEndpoints.RefusalMessage;

    // A session id that "tenant B" produced briefs/feedback under, before the writer was retired.
    private static readonly string SeededSid = Guid.NewGuid().ToString();
    private const string SeededHeadline = "Another tenant's private headline";
    private const string SeededReason = "another tenant wrote this reason";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _briefsDir;
    private string? _priorHosted;

    private GatewayTurnBriefStore _store = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private string _seededFeedbackFile = "";

    public HostedTurnBriefDenyTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-hosted-tb-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _briefsDir = Path.Combine(_root, "gateway-turnbriefs");
    }

    public async Task InitializeAsync()
    {
        // EXPLICIT, not ambient: this class asserts hosted behaviour, so it states hosted mode itself and
        // proves the statement took, rather than inheriting whatever the runner happened to leave set.
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);

        // Legacy data from BEFORE the deny: a real brief and a real feedback record under bare session id,
        // seeded through the production store so a read that WRONGLY got through would have something real
        // to hand back and a write would have a real record to overwrite. A deny tested against an empty
        // store proves nothing.
        _store = new GatewayTurnBriefStore(_briefsDir);
        _store.Append(SeededSid, new TurnBriefDto
        {
            SessionId = SeededSid, TurnNumber = 3, Headline = SeededHeadline, Intent = "intent",
        });
        var seeded = _store.SaveFeedback(SeededSid,
            new TurnBriefDto { SessionId = SeededSid, TurnNumber = 3, Headline = SeededHeadline },
            "up", SeededReason);
        _seededFeedbackFile = seeded.File;

        (_app, _http) = await TurnBriefProbeHost.StartAsync(_store);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// Every production route in the group, in one theory. Every verb is here, because the feedback POST is
    /// the tampering half of this defect and a deny that closed only the value-returning reads would leave
    /// the overwrite path open. The last row is a verb the group NEVER mapped on a mapped path - the
    /// verb-less refusal must answer it too rather than leaking the route's existence through a 405.
    /// </summary>
    [Theory]
    [InlineData("GET", "sessions/{S}/turnbriefs", null)]
    [InlineData("GET", "sessions/{S}/turnbriefs/latest", null)]
    [InlineData("POST", "sessions/{S}/turnbriefs/feedback", "{\"turnNumber\":3,\"vote\":\"down\"}")]
    [InlineData("GET", "turnbriefs/feedback", null)]
    [InlineData("POST", "sessions/{S}/explain", null)]
    [InlineData("GET", "sessions/{S}/explain/latest", null)]
    [InlineData("DELETE", "sessions/{S}/turnbriefs", null)] // a verb the group never mapped
    public async Task Every_turnbrief_route_is_refused(string method, string path, string? body)
    {
        var resp = await Send(new HttpMethod(method), path.Replace("{S}", SeededSid), body);
        await AssertBodyIsNothingButTheRefusal(resp);
    }

    [Fact]
    public async Task The_refused_list_did_not_disclose_the_seeded_brief()
    {
        // Refuse, never serve an empty list: an empty "items" array is a FALSE statement about a session that
        // has a brief on disk, where an absent one is merely absent. The exact-property assertion proves there
        // is no items array at all, and the headline of another tenant's brief never appears.
        var resp = await Send(HttpMethod.Get, $"sessions/{SeededSid}/turnbriefs");
        Assert.DoesNotContain(SeededHeadline, await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await AssertBodyIsNothingButTheRefusal(resp);
    }

    [Fact]
    public async Task The_refused_feedback_list_did_not_disclose_the_corpus()
    {
        // /turnbriefs/feedback enumerates EVERY feedback file with no tenant filter - the disclosure half of
        // the defect. On hosted it is refused and the seeded reason never appears.
        var resp = await Send(HttpMethod.Get, "turnbriefs/feedback");
        Assert.DoesNotContain(SeededReason, await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(SeededSid, await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await AssertBodyIsNothingButTheRefusal(resp);
    }

    [Fact]
    public async Task The_refused_feedback_did_not_overwrite_the_seeded_record()
    {
        // The tampering path: POST feedback takes a caller-supplied feedbackId, turns it into the shared
        // filename, and rewrites the record's vote/reason with no ownership proof. Here the caller names the
        // seeded record and tries to flip it to a down-vote with hijacked text. On hosted the refusal
        // short-circuits before SaveFeedback runs, so the record on disk still reads exactly as seeded.
        var seededId = Path.GetFileNameWithoutExtension(_seededFeedbackFile);
        var resp = await Send(HttpMethod.Post, $"sessions/{SeededSid}/turnbriefs/feedback",
            $"{{\"turnNumber\":3,\"vote\":\"down\",\"note\":\"hijacked\",\"feedbackId\":\"{seededId}\"}}");
        await AssertBodyIsNothingButTheRefusal(resp);

        // Read the store back: the seeded record must be untouched.
        Assert.True(File.Exists(_seededFeedbackFile), "the seeded feedback file must survive a refused POST");
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_seededFeedbackFile));
        Assert.Equal("up", doc.RootElement.GetProperty("vote").GetString());
        Assert.Equal(SeededReason, doc.RootElement.GetProperty("reason").GetString());
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return _http.SendAsync(req);
    }

    /// <summary>
    /// AN ALLOW-LIST, NOT A DENY-LIST, and FORMAT FACTS BEFORE PARSING. Asserting the property set is EXACTLY
    /// one error field inverts a rotting deny-list: anything that leaked reddens automatically. The status and
    /// media type are asserted FIRST so a revert reddens as a STATEMENT - "expected NotFound, got OK" - rather
    /// than as a parser exception on a non-JSON body.
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
}

/// <summary>
/// Boots ONLY the turn-brief endpoint group on an ephemeral port and hands the caller the store it reads
/// plus the denied group handle back, so a test can seed data, drive the routes, and map a future route
/// through the returned handle. There is no auth middleware here on purpose: these routes carry no auth of
/// their own (the host-wide token gate does), so the mode gate in the route MAPPING is exactly what this
/// harness isolates.
/// </summary>
internal static class TurnBriefProbeHost
{
    public static async Task<(WebApplication app, HttpClient http)> StartAsync(
        GatewayTurnBriefStore store,
        Action<HostedDenyGroup>? mapIntoGroup = null,
        Action<IEndpointRouteBuilder>? mapOutsideGroup = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        var group = TurnBriefGatewayEndpoints.Map(app, store, _ => "None", requestExplainAsync: null);
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
        var properties = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "error" }, properties);
        Assert.Equal(HostedTurnBriefDenyTests.RefusalMessage, doc.RootElement.GetProperty("error").GetString());
    }
}

/// <summary>
/// THE SELF-HOST CONTROL, in BOTH non-hosted forms, with real payloads and real effects.
///
/// Self-host is the control for this whole mission, so it is PROVEN rather than INHERITED. This class sets
/// CC_GATEWAY_HOSTED itself, to both non-hosted values that occur in practice - absent, and
/// present-but-not-"1" - and asserts the mode took before driving anything. It asserts REAL PAYLOADS AND REAL
/// EFFECTS, not the absence of the refusal string: an empty-but-successful response would satisfy "the
/// refusal is absent" while still being a broken self-host. It also carries the CAPABILITY CONTROL for the
/// hosted survival assertion: the same feedback POST that is refused on hosted really overwrites the record
/// here. Every test here must stay GREEN through the revert described on HostedTurnBriefDenyTests.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SelfHostTurnBriefControlTests : IDisposable
{
    private static readonly string Sid = Guid.NewGuid().ToString();
    private const string Headline = "The owner's own headline";

    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string _briefsDir;
    private readonly string? _priorHosted;

    public SelfHostTurnBriefControlTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-selfhost-tb-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _briefsDir = Path.Combine(_root, "gateway-turnbriefs");
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private static void DeclareSelfHost(string? value)
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", value);
        Assert.False(GatewayHostedMode.IsHosted);
    }

    /// <summary>null = absent. "0" = present and explicitly not hosted. Both are real non-hosted deployments.</summary>
    public static TheoryData<string?> NonHostedValues => new() { null, "0" };

    /// <summary>HANDLER-POSITIVE RECEIPT for the list route: the route really exists and really answers with
    /// the owner's brief. A 404 deny is indistinguishable from a route that was never mapped, so without a
    /// receipt like this the hosted 404 would prove nothing about a gate.</summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task The_owner_still_reads_his_brief_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);
        var store = new GatewayTurnBriefStore(_briefsDir);
        store.Append(Sid, new TurnBriefDto { SessionId = Sid, TurnNumber = 1, Headline = Headline, Intent = "i" });

        var (app, http) = await TurnBriefProbeHost.StartAsync(store);
        try
        {
            var resp = await http.GetAsync($"sessions/{Sid}/turnbriefs");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var headlines = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(e => e.GetProperty("headline").GetString()).ToArray();
            Assert.Contains(Headline, headlines);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>CAPABILITY CONTROL for the hosted survival assertion: the same feedback POST really overwrites
    /// the record on self-host. The hosted test asserts a seeded record SURVIVES a refused POST; that claim is
    /// only meaningful if the same operation is CAPABLE of changing it.</summary>
    [Theory]
    [MemberData(nameof(NonHostedValues))]
    public async Task The_same_feedback_post_overwrites_the_record_on_self_host(string? hostedValue)
    {
        DeclareSelfHost(hostedValue);
        var store = new GatewayTurnBriefStore(_briefsDir);
        store.Append(Sid, new TurnBriefDto { SessionId = Sid, TurnNumber = 1, Headline = Headline });
        var seeded = store.SaveFeedback(Sid, new TurnBriefDto { SessionId = Sid, TurnNumber = 1 }, "up", "original");
        var seededId = Path.GetFileNameWithoutExtension(seeded.File);

        var (app, http) = await TurnBriefProbeHost.StartAsync(store);
        try
        {
            var resp = await http.PostAsync($"sessions/{Sid}/turnbriefs/feedback", new StringContent(
                $"{{\"turnNumber\":1,\"vote\":\"down\",\"note\":\"changed\",\"feedbackId\":\"{seededId}\"}}",
                Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(seeded.File));
            Assert.Equal("down", doc.RootElement.GetProperty("vote").GetString());
            Assert.Equal("changed", doc.RootElement.GetProperty("reason").GetString());
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }
}

/// <summary>
/// THE POINT OF THE PER-ROUTE DENY: the hosted refusal covers routes not written yet. A guard line repeated
/// in every handler passes exactly the same tests as this deny for the routes that exist today - the
/// difference only shows on the route somebody adds NEXT. So this maps a BRAND-NEW route through the group
/// handle and asserts it is already refused on hosted with no deny of its own, and the self-host mirror shows
/// the same route SERVES off hosted - one direction alone cannot tell a working gate from a brick that
/// refuses everything.
/// </summary>
[Collection("DirectorRoot")]
public sealed class TurnBriefGroupFutureRouteTests : IDisposable
{
    private readonly string _root;
    private readonly string? _prevRoot;
    private readonly string? _priorHosted;

    public TurnBriefGroupFutureRouteTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-tb-future-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch (Exception) { /* best effort */ }
    }

    [Fact]
    public async Task A_route_added_to_the_group_later_is_refused_on_hosted()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);
        var store = new GatewayTurnBriefStore(Path.Combine(_root, "gateway-turnbriefs"));

        var (app, http) = await TurnBriefProbeHost.StartAsync(store,
            mapIntoGroup: group => group.MapPost("/turnbriefs/added-after-the-deny",
                (FutureBody body) => Results.Json(new { echoed = body.Text })),
            mapOutsideGroup: routes => routes.MapPost("/undenied-equivalent",
                (FutureBody body) => Results.Json(new { echoed = body.Text })));
        try
        {
            // Every shape meets the refusal: a valid body, a malformed body, a wrong media type, and a verb
            // the route never mapped.
            foreach (var resp in new[]
                     {
                         await http.PostAsync("/turnbriefs/added-after-the-deny",
                             new StringContent("{\"text\":\"hi\"}", Encoding.UTF8, "application/json")),
                         await http.PostAsync("/turnbriefs/added-after-the-deny",
                             new StringContent("{ not json", Encoding.UTF8, "application/json")),
                         await http.PostAsync("/turnbriefs/added-after-the-deny",
                             new StringContent("hi", Encoding.UTF8, "text/plain")),
                         await http.GetAsync("/turnbriefs/added-after-the-deny"),
                     })
            {
                await TurnBriefProbeHost.AssertBodyIsNothingButTheRefusal(resp);
                Assert.DoesNotContain("echoed", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            // FRAMEWORK-400 CONTROL: the identical malformed body reaches the framework's own 400 on the
            // undenied equivalent, so the denied route short-circuits BEFORE framework binding.
            var undeniedMalformed = await http.PostAsync("/undenied-equivalent",
                new StringContent("{ not json", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, undeniedMalformed.StatusCode);

            // FRAMEWORK-415 CONTROL: a wrong media type reaches the framework's own 415 on the undenied
            // equivalent, so the denied route's wrong-media refusal short-circuits BEFORE endpoint selection.
            var undeniedWrongMedia = await http.PostAsync("/undenied-equivalent",
                new StringContent("hi", Encoding.UTF8, "text/plain"));
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, undeniedWrongMedia.StatusCode);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    [Theory]
    [MemberData(nameof(SelfHostTurnBriefControlTests.NonHostedValues), MemberType = typeof(SelfHostTurnBriefControlTests))]
    public async Task A_route_added_to_the_group_still_serves_on_self_host(string? hostedValue)
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", hostedValue);
        Assert.False(GatewayHostedMode.IsHosted);
        var store = new GatewayTurnBriefStore(Path.Combine(_root, "gateway-turnbriefs"));

        var (app, http) = await TurnBriefProbeHost.StartAsync(store,
            mapIntoGroup: group => group.MapPost("/turnbriefs/added-after-the-deny",
                (FutureBody body) => Results.Json(new { echoed = body.Text })));
        try
        {
            var served = await http.PostAsync("/turnbriefs/added-after-the-deny",
                new StringContent("{\"text\":\"hi\"}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
            using var doc = JsonDocument.Parse(await served.Content.ReadAsStringAsync());
            Assert.Equal("hi", doc.RootElement.GetProperty("echoed").GetString());
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>The future route's body, bound by the FRAMEWORK, so a malformed body off hosted reaches the
    /// framework's own 400 and the hosted refusal is proven to short-circuit before that binding.</summary>
    internal sealed record FutureBody(string Text);
}
