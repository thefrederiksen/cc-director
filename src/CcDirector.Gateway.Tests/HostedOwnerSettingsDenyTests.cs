using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// One place naming EVERY route in the owner-settings group, with the verb each one actually answers on.
/// Both sides of the proof read this table: the hosted side asserts every row is REFUSED, the self-host
/// side asserts every row is SERVED. Writing the table once is what makes "every route, both directions"
/// a checkable claim rather than a sentence in a pull request.
/// </summary>
public static class OwnerSettingsRoutes
{
    /// <summary>The refusal a route in <c>SettingsEndpoints</c> answers with on hosted.</summary>
    public const string SettingsRefusal = "gateway settings are not available on the hosted gateway";

    /// <summary>The refusal a route in <c>AiModelsEndpoint</c> answers with on hosted.</summary>
    public const string ModelsRefusal = "the model settings are not available on the hosted gateway";

    /// <summary>The refusal a route in <c>TelemetryConsentEndpoint</c> answers with on hosted.</summary>
    public const string TelemetryRefusal = "the telemetry consent setting is not available on the hosted gateway";

    /// <summary>A route in the group: the verb it answers, its path, and a well-formed body for writes.</summary>
    public sealed record Route(string Verb, string Path, string? Body, string Refusal)
    {
        public override string ToString() => Verb + " /" + Path;
    }

    /// <summary>
    /// The 22 routes mapped directly by <c>SettingsEndpoints</c>. Bodies are WELL FORMED and would be
    /// accepted on self-host, so a refusal here can never be mistaken for a validation rejection.
    /// </summary>
    public static readonly Route[] Settings =
    {
        new("GET",  "gateway/settings",                 null, SettingsRefusal),
        new("POST", "gateway/brain/restart",            null, SettingsRefusal),
        new("PUT",  "gateway/brain/config",             "{\"agentId\":\"agent-1\",\"model\":\"opus\"}", SettingsRefusal),
        new("GET",  "gateway/wingman/training-capture", null, SettingsRefusal),
        new("PUT",  "gateway/wingman/training-capture", "{\"enabled\":true}", SettingsRefusal),
        new("GET",  "gateway/addressing-mode",          null, SettingsRefusal),
        new("PUT",  "gateway/addressing-mode",          "{\"mode\":\"lan\"}", SettingsRefusal),
        new("GET",  "gateway/snooze-default",           null, SettingsRefusal),
        new("PUT",  "gateway/snooze-default",           "{\"minutes\":45}", SettingsRefusal),
        new("GET",  "gateway/injected-text",            null, SettingsRefusal),
        new("PUT",  "gateway/injected-text",            "{\"use_yours\":true,\"yours\":\"words from another tenant\"}", SettingsRefusal),
        new("GET",  "gateway/snooze-presets",           null, SettingsRefusal),
        new("PUT",  "gateway/snooze-presets",           "{\"presets\":[15,30,60],\"defaultMinutes\":30}", SettingsRefusal),
        new("GET",  "gateway/time-zone",                null, SettingsRefusal),
        new("PUT",  "gateway/time-zone",                "{\"timeZone\":\"America/New_York\"}", SettingsRefusal),
        new("GET",  "gateway/transcription-mode",       null, SettingsRefusal),
        new("PUT",  "gateway/transcription-mode",       "{\"mode\":\"devthrottle\"}", SettingsRefusal),
        new("GET",  "gateway/ai-provider",              null, SettingsRefusal),
        new("PUT",  "gateway/ai-provider",              "{\"provider\":\"devthrottle\"}", SettingsRefusal),
        new("GET",  "gateway/tts-voice",                null, SettingsRefusal),
        new("PUT",  "gateway/tts-voice",                "{\"voice\":\"shimmer\"}", SettingsRefusal),
        new("PUT",  "gateway/autostart",                "{\"enabled\":true}", SettingsRefusal),
    };

    /// <summary>The 7 routes mapped by <c>AiModelsEndpoint</c>.</summary>
    public static readonly Route[] Models =
    {
        new("GET",  "gateway/ai/models",              null, ModelsRefusal),
        new("POST", "gateway/ai/test-chat",           "{\"model\":\"some-model\"}", ModelsRefusal),
        new("PUT",  "gateway/ai/wingman-model",       "{\"model\":\"hosted-wingman\"}", ModelsRefusal),
        new("PUT",  "gateway/ai/wingman-fast-model",  "{\"model\":\"hosted-fast\"}", ModelsRefusal),
        new("PUT",  "gateway/ai/car-mode-model",      "{\"model\":\"hosted-car\"}", ModelsRefusal),
        new("PUT",  "gateway/ai/car-mode-end-phrase", "{\"phrase\":\"finished here\"}", ModelsRefusal),
        new("PUT",  "gateway/ai/tts-model",           "{\"model\":\"hosted-speech\"}", ModelsRefusal),
    };

    /// <summary>The 2 routes mapped by <c>TelemetryConsentEndpoint</c>.</summary>
    public static readonly Route[] Telemetry =
    {
        new("GET", "gateway/telemetry-consent", null, TelemetryRefusal),
        new("PUT", "gateway/telemetry-consent", "{\"enabled\":false}", TelemetryRefusal),
    };

    /// <summary>All 31 route-and-verb pairs the owner-settings group answers.</summary>
    public static IEnumerable<Route> All => Settings.Concat(Models).Concat(Telemetry);

    /// <summary>
    /// Every route as xUnit theory data, so no row can be silently left out of either side. Flattened to
    /// plain strings (an empty body string means "no body") DELIBERATELY: xUnit can only pre-enumerate and
    /// NAME theory cases whose arguments are serializable, and a run whose cases cannot be named
    /// individually cannot be checked against the declared method set by name.
    /// </summary>
    public static TheoryData<string, string, string, string> AllRoutes
    {
        get
        {
            var data = new TheoryData<string, string, string, string>();
            foreach (var route in All) data.Add(route.Verb, route.Path, route.Body ?? "", route.Refusal);
            return data;
        }
    }

    /// <summary>
    /// Sends one row. Kept here so the hosted and self-host sides drive the routes IDENTICALLY and any
    /// difference between them is the Gateway's behaviour, not the client's.
    /// </summary>
    public static Task<HttpResponseMessage> SendAsync(HttpClient http, string verb, string path, string body)
    {
        var request = new HttpRequestMessage(new HttpMethod(verb), path);
        if (!string.IsNullOrEmpty(body))
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return http.SendAsync(request);
    }

    /// <summary>Overload for the non-theory call sites that already hold a <see cref="Route"/>.</summary>
    public static Task<HttpResponseMessage> SendAsync(HttpClient http, Route route) =>
        SendAsync(http, route.Verb, route.Path, route.Body ?? "");

    /// <summary>
    /// Asserts a response is the hosted refusal and NOTHING ELSE.
    ///
    /// FORMAT FACTS FIRST, ON PURPOSE. The status and the media type are asserted BEFORE the body is
    /// parsed, because parsing is itself an unstated assertion about format. On this Gateway a 404 is not
    /// necessarily JSON - a single-page-application fallback answers unmatched paths and can reply with
    /// plain text, or on a release host with a 200 and HTML - so a test that parses first turns a real
    /// finding into a crash. A crash proves the mutation broke something upstream; it cannot say WHAT was
    /// served in place of the refusal, which is the entire claim a deny makes.
    ///
    /// The body is then compared as an ALLOW-LIST over the whole property set, not by substring. A
    /// substring match cannot see an extra leaked field, and a deny-list of today's keys rots the moment
    /// the payload grows.
    /// </summary>
    public static async Task AssertIsNothingButTheRefusal(HttpResponseMessage response, string expectedMessage)
    {
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);

        var properties = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "error" }, properties);
        Assert.Equal(expectedMessage, document.RootElement.GetProperty("error").GetString());
    }
}

/// <summary>
/// Issue #1863: the WHOLE owner-settings group is DENIED on the hosted Gateway.
///
/// THE DEFECT. Thirty-one routes across three endpoint classes read and write PROCESS-GLOBAL
/// configuration. There is no tenant dimension anywhere in them: config.json is one file for the whole
/// process, so every write is a FLEET-WIDE mutation performed by whichever authenticated caller happened
/// to send it - one tenant repointing the wingman model, the speech model, the time zone, the snooze
/// lengths, the network addressing mode or the fleet-wide telemetry consent for everybody else. And
/// <c>GET /gateway/injected-text</c> hands back the owner's own custom agent-launch instruction text,
/// which is his words, to any caller.
///
/// WHY A DENY AND NOT A PARTITION. There is nothing to partition BY. These are not per-tenant records
/// whose storage unit could carry a tenant - they are single global values, one per key, and a
/// "per-tenant" answer would have to be invented from an attribution that was never recorded. That is a
/// half-partition, which is worse than an honest refusal because it looks like isolation. The concept
/// does not survive the move either: this is the OWNER'S control panel for HIS OWN Gateway, and on
/// shared hosted infrastructure "the owner" is not a thing.
///
/// SELF-HOST IS THE CONTROL AND IS UNCHANGED. Self-host is single-tenant and every one of these is
/// legitimate owner function there. <see cref="HostedOwnerSettingsSelfHostControlTests"/> proves that
/// explicitly, in both non-hosted forms, by asserting the REAL served result of every route.
///
/// IT REFUSES, IT DOES NOT SERVE EMPTY. An empty settings snapshot is indistinguishable from "you have
/// no settings", which is a false statement rather than an absent one. Every refusal is a 404 whose body
/// is EXACTLY one <c>error</c> property, asserted as an allow-list over the whole property set.
///
/// ONE GROUP FILTER PER FAMILY, NOT A GUARD PER ROUTE. Each of the three endpoint classes puts one
/// endpoint filter on the route group it maps, so the refusal runs before every route in that group
/// INCLUDING ROUTES THAT DO NOT EXIST YET. <see cref="HostedOwnerSettingsGroupFilterTests"/> is the only
/// test of that property: a guard repeated per handler passes every other test in this file.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedOwnerSettingsDenyTests : IAsyncLifetime
{
    private const string Token = "test-token-12345";

    private readonly string _root;
    private readonly string? _priorRoot;
    private readonly string? _priorHosted;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-ownersettings-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private string _deviceKey = "";

    public HostedOwnerSettingsDenyTests()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-ownersettings-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        // EXPLICIT, never ambient. This class asserts hosted behaviour, so it states hosted mode itself
        // and proves the statement took, rather than inheriting whatever the runner happened to leave set.
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);
    }

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: FreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"));
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };

        // A fully enrolled, tenant-bound device key - the strongest caller hosted has. The point is that
        // even this one is refused: no credential makes an owner's control panel correct on shared
        // infrastructure, because there is no owner.
        _deviceKey = _gateway.Devices.Register("dev-a", "MA").DeviceKey;
        var tenant = _gateway.TenantRegistry.MintOrLookupBySubject("sub-alice", "alice@example.com");
        _gateway.Devices.SetAccountBinding("dev-a", "sub-alice", tenant.Value);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _deviceKey);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>
    /// EVERY route, on its OWN verb, refused to a fully enrolled tenant - and refused with a body carrying
    /// nothing but the refusal.
    /// </summary>
    [Theory]
    [MemberData(nameof(OwnerSettingsRoutes.AllRoutes), MemberType = typeof(OwnerSettingsRoutes))]
    public async Task Every_owner_settings_route_is_refused_on_hosted(
        string verb, string path, string body, string refusal)
    {
        var response = await OwnerSettingsRoutes.SendAsync(_http, verb, path, body);
        await OwnerSettingsRoutes.AssertIsNothingButTheRefusal(response, refusal);
    }

    /// <summary>
    /// The refusal is a REFUSAL, not an empty snapshot. The exact-property-set assertion above is what
    /// enforces this; this states the intent in the terms the mistake would be made in - the settings page
    /// must not be handed a snapshot that reads "you have no custom text, no presets, no time zone", which
    /// is a false statement where an absent one is merely absent.
    /// </summary>
    [Fact]
    public async Task The_refusal_is_not_an_empty_settings_snapshot()
    {
        foreach (var path in new[] { "gateway/settings", "gateway/injected-text", "gateway/snooze-presets" })
        {
            var response = await _http.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.DoesNotContain("\"placeholders\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"presets\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"brain\"", body, StringComparison.Ordinal);
            Assert.Contains("not available on the hosted gateway", body, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A refused write CHANGED NOTHING. The refusal is asserted by an INDEPENDENT re-read of the
    /// configuration store rather than by the response body, because a filter that returned 404 after the
    /// handler had already run would look identical from outside.
    /// </summary>
    [Fact]
    public async Task A_refused_write_does_not_reach_the_configuration_store()
    {
        var timeZoneBefore = TimeZoneConfig.Get();
        var voiceBefore = TtsVoiceConfig.Resolve(TranscriptionModeConfig.Get());
        var carModelBefore = CarModeModelConfig.Get();
        var consentBefore = TelemetryConsentConfig.Get();

        foreach (var route in OwnerSettingsRoutes.All.Where(r => r.Body is not null))
            await OwnerSettingsRoutes.AssertIsNothingButTheRefusal(
                await OwnerSettingsRoutes.SendAsync(_http, route), route.Refusal);

        Assert.Equal(timeZoneBefore, TimeZoneConfig.Get());
        Assert.Equal(voiceBefore, TtsVoiceConfig.Resolve(TranscriptionModeConfig.Get()));
        Assert.Equal(carModelBefore, CarModeModelConfig.Get());
        Assert.Equal(consentBefore, TelemetryConsentConfig.Get());
    }

    /// <summary>
    /// CONTROL, and it must stay green through the revert: the deny runs after the host-wide auth gate, so
    /// it must not have opened these routes up as a side effect.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_caller_is_still_rejected()
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("gateway/settings")).StatusCode);
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
/// Boots ONE owner-settings family onto a bare application with no authentication gate and NO
/// single-page-application fallback, and hands the caller the route group back so a test can map routes
/// onto it. That return value is what makes the future-route proof possible at all: the group is created
/// inside each <c>Map</c> method, so nothing outside those methods could otherwise state a property about
/// routes added to it later.
///
/// No fallback is deliberate as well. On the real Gateway an unmapped path is answered by the
/// single-page-application fallback, so "the route is not there" and "the route is there and refused" can
/// look alike. Here an unmapped path is a bare 404 with no body and an unmatched VERB is a 405, which is
/// what lets <see cref="HostedOwnerSettingsGroupFilterTests"/> prove a route exists on hosted at all.
/// </summary>
internal static class OwnerSettingsProbeHost
{
    public static async Task<(WebApplication app, HttpClient http)> StartAsync(
        Func<IEndpointRouteBuilder, RouteGroupBuilder> mapFamily,
        Action<RouteGroupBuilder>? mapIntoGroup = null,
        Action<IEndpointRouteBuilder>? mapOutsideGroup = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        var group = mapFamily(app);
        mapIntoGroup?.Invoke(group);
        mapOutsideGroup?.Invoke(app);

        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, http);
    }
}

/// <summary>
/// THE POINT OF THE WHOLE CHANGE: the hosted refusal is a filter on each route GROUP, so it covers routes
/// that have not been written yet.
///
/// A guard line repeated in every handler passes EXACTLY the same tests as a group filter for the routes
/// that exist today, which is precisely what makes it dangerous rather than merely untidy: the difference
/// only appears on the route somebody adds NEXT, when it is open on hosted by default and nothing fails.
/// That difference is not observable by driving the 31 routes that exist, so this class maps a BRAND-NEW
/// probe route onto each group and asserts it is refused with no deny written for it anywhere. These are
/// the tests that justify choosing a group filter at all, and they are the reason all three
/// <c>Map</c> methods now return their group.
///
/// The served half of the same probe - the SAME probe paths, with hosted mode explicitly OFF in both
/// non-hosted forms - is <see cref="HostedOwnerSettingsSelfHostProbeTests"/>. One direction alone cannot
/// tell a working gate apart from a brick: a filter that refused everything unconditionally would pass
/// every assertion in this class while having silently killed the routes for self-host too.
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class HostedOwnerSettingsGroupFilterTests : IAsyncLifetime
{
    internal const string ProbeSentinel = "probe-payload-that-must-never-be-served-on-hosted";

    private readonly string _root;
    private readonly string? _priorRoot;
    private readonly string? _priorHosted;
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-ownersettings-group-" + Guid.NewGuid().ToString("N"));

    private GatewayHost _gateway = null!;

    public HostedOwnerSettingsGroupFilterTests()
    {
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-ownersettings-group-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Assert.True(GatewayHostedMode.IsHosted);
    }

    public async Task InitializeAsync()
    {
        // A real, started GatewayHost, because SettingsEndpoints.Map needs one. Its own routes are not
        // driven here; the probe application maps a second copy of the family and is what the tests call.
        _gateway = new GatewayHost(port: HostedOwnerSettingsDenyTests.FreePort(), token: "probe-token",
            authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_instancesDir, "snooze", "snooze.json"));
        await _gateway.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    /// <summary>The three families, each mapped on its own so one filter is the only thing in the way.</summary>
    internal Func<IEndpointRouteBuilder, RouteGroupBuilder> Family(string name) => name switch
    {
        "settings" => routes => SettingsEndpoints.Map(routes, _gateway),
        "models" => routes => AiModelsEndpoint.Map(routes, new KeyVault(Path.Combine(_root, "vault.json"))),
        "telemetry" => TelemetryConsentEndpoint.Map,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown owner-settings family"),
    };

    public static TheoryData<string, string> Families => new()
    {
        { "settings", "/gateway/added-after-the-deny-was-written" },
        { "models", "/gateway/ai/added-after-the-deny-was-written" },
        { "telemetry", "/gateway/telemetry-added-after-the-deny-was-written" },
    };

    /// <summary>
    /// A route that did not exist when the refusal was written is refused anyway. NOTHING in the endpoint
    /// class mentions this path and no guard is written for it here - the only thing between the caller and
    /// the probe payload is the group filter. Delete the filter and this serves the probe payload with a
    /// 200, which is the future-route hole stated out loud.
    /// </summary>
    [Theory]
    [MemberData(nameof(Families))]
    public async Task A_route_added_to_the_group_later_is_refused_on_hosted_with_no_deny_of_its_own(
        string family, string probePath)
    {
        var (app, http) = await OwnerSettingsProbeHost.StartAsync(
            Family(family),
            mapIntoGroup: group => group.MapGet(probePath, () => Results.Json(new { probe = ProbeSentinel })));
        try
        {
            var response = await http.GetAsync(probePath);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(ProbeSentinel, body, StringComparison.Ordinal);

            using var document = JsonDocument.Parse(body);
            Assert.Equal(new[] { "error" }, document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>
    /// CONTROL: the filter is scoped to its own group, not a blanket refusal on the whole application. A
    /// route mapped OUTSIDE the group still serves on hosted, so the refusals above are the filter doing
    /// its job rather than the host refusing everything. This must stay GREEN through the revert.
    /// </summary>
    [Theory]
    [MemberData(nameof(Families))]
    public async Task A_route_outside_the_group_still_serves_on_hosted(string family, string unusedProbePath)
    {
        _ = unusedProbePath;

        var (app, http) = await OwnerSettingsProbeHost.StartAsync(
            Family(family),
            mapOutsideGroup: routes => routes.MapGet("/not-an-owner-setting", () => Results.Json(new { served = "yes" })));
        try
        {
            var response = await http.GetAsync("/not-an-owner-setting");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("\"served\":\"yes\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }

    /// <summary>
    /// THE SERVED-SIDE POSITIVE FACT FOR <c>POST /gateway/brain/restart</c>, and the answer to the fact
    /// that a 404 deny is INDISTINGUISHABLE from a route that was never mapped.
    ///
    /// This is the one route in the group whose handler cannot be invoked in a test: its whole job is to
    /// start a coding-agent process on the machine, so calling it on the served side would spawn one. So
    /// instead of running the handler, this proves the route is REALLY THERE on hosted by asking for it
    /// with the wrong verb. A GET reaches routing, finds a path that exists with no GET on it, and is
    /// answered 405 with an <c>Allow: POST</c> header - a POSITIVE statement, emitted by the routing
    /// table, that a POST endpoint is registered at that exact path. The endpoint filter never runs,
    /// because no endpoint was selected, which is exactly why the 405 survives the deny.
    ///
    /// Put together with the POST being refused 404 in
    /// <see cref="HostedOwnerSettingsDenyTests.Every_owner_settings_route_is_refused_on_hosted"/>, the two
    /// say: the route exists on hosted, and the thing turning it away is the deny.
    /// </summary>
    [Fact]
    public async Task The_brain_restart_route_exists_on_hosted_and_is_reachable_only_as_a_post()
    {
        var (app, http) = await OwnerSettingsProbeHost.StartAsync(Family("settings"));
        try
        {
            var wrongVerb = await http.GetAsync("/gateway/brain/restart");
            Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongVerb.StatusCode);
            Assert.Contains("POST", wrongVerb.Content.Headers.Allow);

            // The path really is unique to this route: a path NOT mapped at all answers 404, not 405, on
            // this fallback-free probe host - so the 405 above is about a registered endpoint and not a
            // catch-all.
            var neverMapped = await http.GetAsync("/gateway/brain/no-such-route");
            Assert.Equal(HttpStatusCode.NotFound, neverMapped.StatusCode);
            Assert.Empty(neverMapped.Content.Headers.Allow);
        }
        finally { http.Dispose(); await app.DisposeAsync(); }
    }
}
