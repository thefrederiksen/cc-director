using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Tenancy;

/// <summary>
/// The shared hosted-refusal primitive: the ONE boundary every deny family adopts, proved here ONCE for all
/// of them.
///
/// WHY IT EXISTS. A refusal attached as a route-group endpoint filter does not answer uniformly across
/// request shapes. The mechanism and the measurements are in the PRIVATE architecture record; what belongs
/// here is the guarantee being asserted, one test per shape.
///
/// WHAT IS PROVED HERE, AND WHY EACH FAMILY DOES NOT RE-PROVE IT. On hosted the family's handler is never
/// mapped: a verb-less, constraint-free refusal route takes its place. An adopter therefore cannot be
/// attached AND still bind, because attachment IS the substitution - there is no state of the world where
/// attachment holds and the property fails. So the pre-binding property is proved once, here, and each
/// adopting family owes only attachment, gate direction, and its own body-bound future-route probe.
///
/// THE SHAPE SET IS THE WHOLE POINT, and it is enumerated rather than sampled because this defect is
/// invisible to the obvious probe: a parameterless GET is exactly the shape that CANNOT see it. Every shape
/// below was measured to be answered by something other than the refusal under at least one candidate
/// design that looked correct:
///
///   valid body | malformed body | wrong media type | custom binder | future route | wrong verb |
///   route-constraint miss | route-constraint hit
///
/// A GUARD HAS TWO FAILURE DIRECTIONS AND ONLY ONE OF THEM IS A LEAK. Under-refusal leaks; OVER-refusal is
/// an outage on a route nobody denied. The neighbour probe below is what tests the second direction, and it
/// is the reason the alternative design - a host-wide matcher refusing by path prefix - was rejected: it was
/// measured refusing a sibling route that was never part of the denied family. Every deny from here on
/// carries a neighbour probe.
/// </summary>
[Collection(HostedRouteDenyCollection.Name)]
public sealed class HostedRouteDenyOnHostedTests : IAsyncLifetime
{
    private DenyProbeHost _host = null!;

    public async Task InitializeAsync() => _host = await DenyProbeHost.StartAsync(hosted: true);

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    [Theory]
    [InlineData("/family/echo")]        // a body-bound POST
    [InlineData("/family/custom")]      // a parameter with a custom binder, whose execution is observable
    [InlineData("/family/future")]      // mapped into the group AFTER everything else: the future route
    public async Task Every_route_in_the_group_is_refused_on_hosted(string path)
    {
        var response = await _host.PostJsonAsync(path, "{\"text\":\"hello\"}");
        await AssertIsExactlyTheRefusal(response);
    }

    [Fact]
    public async Task A_malformed_body_meets_the_refusal_and_not_the_frameworks_own_400()
    {
        // A shape that an earlier boundary let the framework answer instead of the refusal.
        var response = await _host.PostJsonAsync("/family/echo", "{ not json");
        await AssertIsExactlyTheRefusal(response);
    }

    [Fact]
    public async Task A_wrong_media_type_meets_the_refusal_and_not_the_frameworks_own_415()
    {
        // The shape that survived the longest across candidate designs: a body parameter makes the
        // framework infer a media-type constraint that endpoint SELECTION enforces ahead of any handler.
        // Mapping no handler at all is what removes the constraint along with the handler.
        var response = await _host.PostAsync("/family/echo", "hello", "text/plain");
        await AssertIsExactlyTheRefusal(response);
    }

    [Fact]
    public async Task A_verb_the_family_never_mapped_meets_the_refusal_and_not_a_405()
    {
        // A 405 would disclose that the route EXISTS on a Gateway whose refusal says it does not. A wrong
        // verb is a request shape, and the standard is that the refusal is uniform across shapes.
        var response = await _host.GetAsync("/family/echo");
        await AssertIsExactlyTheRefusal(response);
    }

    [Theory]
    [InlineData("/family/typed/7")]     // satisfies the real route's {id:int}
    [InlineData("/family/typed/abc")]   // FAILS it - and so would never select a refusal carrying it
    public async Task A_constrained_route_is_refused_whether_or_not_the_constraint_is_satisfied(string path)
    {
        // The miss is the one that matters. A segment that fails an inline constraint fails endpoint
        // selection, so a refusal mapped on the same constrained pattern is never selected either and the
        // framework answers its own 404 with the refusal never running. The refusal is therefore mapped on
        // the pattern with its constraints stripped.
        var response = await _host.PostJsonAsync(path, "{\"text\":\"hello\"}");
        await AssertIsExactlyTheRefusal(response);
    }

    [Fact]
    public async Task No_argument_binder_runs_behind_the_refusal_on_any_shape()
    {
        // The claim is not "the response was right" - it is that NO handler-bound code executed at all. This
        // is the assertion that separates a refusal placed before binding from one placed after it, and it
        // is the one that was false under the filter on every shape including a valid body.
        ObservableBinding.Reset();

        await _host.PostJsonAsync("/family/custom", "{\"text\":\"hello\"}");
        await _host.PostJsonAsync("/family/custom", "{ not json");
        await _host.PostAsync("/family/custom", "hello", "text/plain");
        await _host.GetAsync("/family/custom");

        Assert.Equal(0, ObservableBinding.Count);
    }

    [Fact]
    public async Task A_neighbouring_route_outside_the_family_still_serves()
    {
        // The OTHER failure direction. A guard that refuses a route nobody denied is an outage, and it is
        // the measured defect of the rejected path-prefix design. Route precedence is what protects this:
        // a literal segment outranks the refusal's loosened parameter segment.
        var response = await _host.PostJsonAsync("/family/typed/summary", "{\"text\":\"hello\"}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("neighbour", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_HEAD_request_meets_the_refusal_status_with_no_body()
    {
        // Stated deliberately rather than folded into "every request shape": HTTP says a HEAD carries the
        // headers and no body, and the framework enforces that. So the uniformity claim is about the STATUS
        // and the HEADERS here, not the bytes - and a reader is entitled to see which it is.
        var response = await _host.SendAsync(HttpMethod.Head, "/family/echo");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("/family/multi")]
    public async Task A_family_mapping_several_verbs_on_one_path_gets_ONE_refusal_and_not_a_tie(string path)
    {
        // The multi-verb path adopting families rely on, and it was previously unproved. Two verbs on one
        // path must produce ONE verb-less refusal: a second would TIE with the first, and a tie surfaces as
        // a 500 at request time - on the denied route, which is the one nobody exercises until a caller
        // does. Every verb here must meet the refusal, including one the family never mapped.
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Post, HttpMethod.Delete })
        {
            var response = await _host.SendAsync(method, path);
            await AssertIsExactlyTheRefusal(response);
        }
    }

    [Fact]
    public async Task A_neighbouring_route_with_a_PARAMETER_segment_still_serves()
    {
        // The neighbour probe in its second direction. The literal case proves precedence protects a fixed
        // sibling; this proves the refusal has not widened across a path boundary into a different route
        // shape entirely. One case tests one thing, and over-refusal is the direction no leak-shaped test
        // would ever notice.
        var response = await _host.PostJsonAsync("/family/other/anything", "{\"text\":\"hello\"}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("neighbour", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Asserts the response is the family's refusal and NOTHING ELSE. Status and media type are asserted
    /// BEFORE the body is parsed, deliberately: parsing is itself an assertion about format, so a guard that
    /// has gone and a route now serving HTML must redden as "expected application/json, got text/html"
    /// rather than as a parser exception. A red that arrives as a crash proves the mutation landed, not that
    /// the guard was the thing holding.
    /// </summary>
    private static async Task AssertIsExactlyTheRefusal(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // The WHOLE header value, parameters included. Asserting only the media type would pass on a
        // refusal served with a different charset, and a refusal is a contract about exactly what is
        // served - "close enough" on a content type is how a caller parses something other than it expected.
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);

        // The whole property set against a one-name allow-list. A deny-list of today's keys cannot see an
        // extra field that leaks tomorrow; enumerating the set reddens on anything extra with no edit here.
        Assert.Equal(new[] { "error" }, document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(DenyProbeHost.RefusalMessage, document.RootElement.GetProperty("error").GetString());
    }
}

/// <summary>
/// THE GATE DIRECTION, AND THE CONTROL. Off hosted the primitive maps the family's real handlers and creates
/// no refusal at all, so the family is byte-identical to one mapped on an unguarded builder.
///
/// This class is what fails if the hosted condition is ever inverted or hard-wired: a refusal that fired
/// everywhere would look perfect to every test in the class above and would silently break every self-host
/// install. Absence of the refusal is asserted POSITIVELY - the real handler's own payload is required -
/// because an assertion that merely checks "not the refusal" passes on a framework error too.
/// </summary>
[Collection(HostedRouteDenyCollection.Name)]
public sealed class HostedRouteDenySelfHostControlTests : IAsyncLifetime
{
    private DenyProbeHost _host = null!;

    public async Task InitializeAsync() => _host = await DenyProbeHost.StartAsync(hosted: false);

    public Task DisposeAsync() => _host.DisposeAsync().AsTask();

    /// <summary>
    /// BOTH declared non-hosted forms, explicitly. The variable ABSENT and the variable present but not
    /// "1" are two different states of the world, and a control exercising one of them proves the guard
    /// for one of them. The absent case additionally must be SET rather than inherited: a control that
    /// passes because the runner happened to leave the variable unset is reporting an ambient condition,
    /// not a property of the code.
    /// </summary>
    [Theory]
    [InlineData(null)]      // absent
    [InlineData("0")]       // present, not "1"
    [InlineData("")]        // present, empty
    public async Task The_family_serves_normally_in_EITHER_non_hosted_form(string? nonHostedForm)
    {
        await using var host = await DenyProbeHost.StartAsync(hosted: false, nonHostedForm: nonHostedForm);

        var response = await host.PostJsonAsync("/family/echo", "{\"text\":\"hello\"}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("hello", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_family_serves_normally_off_hosted()
    {
        var response = await _host.PostJsonAsync("/family/echo", "{\"text\":\"hello\"}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("hello", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_route_added_to_the_group_later_serves_normally_off_hosted()
    {
        var response = await _host.PostJsonAsync("/family/future", "{\"text\":\"hello\"}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("hello", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_real_binder_runs_off_hosted()
    {
        // The positive twin of the hosted no-binding assertion. Without this, a primitive that broke binding
        // outright on every deployment would satisfy the hosted class and nothing would notice.
        ObservableBinding.Reset();

        var response = await _host.PostJsonAsync("/family/custom", "{\"text\":\"hello\"}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, ObservableBinding.Count);
    }

    [Fact]
    public async Task The_constrained_route_still_constrains_off_hosted()
    {
        // Self-host keeps the real route's constraint: the loosened pattern exists ONLY on hosted, so a
        // constraint miss here is the framework's own 404 and carries no refusal body.
        var response = await _host.PostJsonAsync("/family/typed/abc", "{\"text\":\"hello\"}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, (await response.Content.ReadAsStringAsync()).Length);
    }
}

/// <summary>The refusal payload is validated where it is CONSTRUCTED, so a bad one fails at startup.</summary>
[Collection(HostedRouteDenyCollection.Name)]
public sealed class HostedDenialValidationTests
{
    [Theory]
    [InlineData("", "message", "reason", "undeny")]
    [InlineData("family", "", "reason", "undeny")]
    [InlineData("family", "message", "", "undeny")]
    [InlineData("family", "message", "reason", "")]
    public void A_denial_that_would_refuse_meaninglessly_is_refused_at_construction(
        string family, string message, string reason, string unDeny)
    {
        // Failing to boot is the correct direction. A blank refusal serves something a caller cannot act on
        // and a proof cannot assert - which reads, from outside, like a working route.
        Assert.Throws<ArgumentException>(() => new HostedDenial(family, message, reason, unDeny));
    }

    [Fact]
    public void A_denial_must_refuse_with_a_failure_status()
    {
        Assert.Throws<ArgumentException>(() =>
            new HostedDenial("family", "message", "reason", "undeny", statusCode: StatusCodes.Status200OK));
    }

}

/// <summary>
/// CHANGE 2, the exclusive-mode premise, proved over the FINALISED endpoint set and with a request probe.
/// An exclusive family maps ONE catch-all refusal and NO per-route refusals, so conflicting declared shapes
/// - the case/verb/policy ties a per-route family could manufacture - cannot become an ambiguous denied
/// route. Before this fix, every <c>Map*</c> on an exclusive group ALSO installed a per-route refusal, so
/// the exclusive family carried the exact ties its single catch-all was supposed to make impossible.
/// </summary>
[Collection(HostedRouteDenyCollection.Name)]
public sealed class HostedExclusiveDenyTests
{
    [Fact]
    public async Task An_exclusive_family_maps_one_catch_all_and_no_per_route_refusals()
    {
        await using var host = await ValidatedHostedApp.StartAsync(outer =>
        {
            var group = HostedRouteDeny.ExclusiveGroup(outer, "/exgroup", ProbeDenial());

            // Declared shapes a per-route family would have turned into TYING verb-less refusals: the same
            // path under two verbs and two cases, and a multi-verb path. On an exclusive family these are all
            // discarded - the catch-all is the whole mechanism.
            group.MapGet("/x/{id}", (int id) => Results.Json(new { id }));
            group.MapPost("/X/{name}", (string name) => Results.Json(new { name }));
            group.MapGet("/multi", () => Results.Json(new { m = "get" }));
            group.MapPut("/multi", () => Results.Json(new { m = "put" }));
        });

        // The finalised endpoint set carries EXACTLY the catch-all and the prefix-root refusal - two refusal
        // endpoints, no matter how many handlers were declared. That is the property that makes the ties
        // impossible: there is no per-route refusal to tie with anything.
        var refusals = host.Endpoints.OfType<RouteEndpoint>()
            .Count(e => e.Metadata.GetMetadata<HostedRefusalMarker>() is not null);
        Assert.Equal(2, refusals);

        // The production validator is content: with no per-route refusal, nothing competes.
        HostedRefusalRouteSpace.Validate(host.Endpoints);

        // The request probe: every declared path, and one never declared at all, answers the refusal
        // deterministically - a 404 refusal, never the 500 an ambiguous denied route would produce.
        foreach (var path in new[]
                 {
                     "/exgroup/x/7", "/exgroup/x/anything", "/exgroup/X/7",
                     "/exgroup/multi", "/exgroup", "/exgroup/never/declared/deep",
                 })
        {
            var response = await host.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        }
    }

    [Fact]
    public async Task An_exclusive_family_serves_its_real_handlers_off_hosted()
    {
        // The control: off hosted the exclusive flag is inert, the catch-all is never mapped, and the real
        // handlers serve exactly as any group's would. Without this, a bug that discarded handlers on
        // self-host too would satisfy the hosted assertion above and silently break every desktop install.
        await using var host = await ValidatedHostedApp.StartAsync(
            outer =>
            {
                var group = HostedRouteDeny.ExclusiveGroup(outer, "/exgroup", ProbeDenial());
                group.MapGet("/x/{id}", (int id) => Results.Json(new { id }));
            },
            hosted: false);

        var response = await host.GetAsync("/exgroup/x/7");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"id\":7", await response.Content.ReadAsStringAsync());
    }

    private static HostedDenial ProbeDenial(string family = "probe") => ProbeDenialFactory.Make(family);
}

/// <summary>
/// CHANGE 1, the finalised route-space check, proved through REAL mapping rather than hand-built endpoints:
/// the tie is manufactured by two families that each map a refusal the way an adopting family does, and the
/// production validator is shown to THROW over the actual finalised endpoint set - not to let the tie become
/// a request-time 500. The companion single-family case shows the case-fold dedup already collapses the
/// Codex counterexample to one refusal, so it never reaches the validator as a tie at all.
/// </summary>
[Collection(HostedRouteDenyCollection.Name)]
public sealed class HostedRefusalRouteSpaceCrossGroupTests
{
    [Fact]
    public async Task Two_families_denying_a_method_disambiguated_shape_fail_the_validator()
    {
        // Cross-group: family A denies GET /shared/{id}, family B denies POST /shared/{name}. Self-host holds
        // them apart by method; hosted strips the method, so both become verb-less refusals on the same shape
        // and TIE. Dedup is per-group and cannot see across families, so the finalised route-space check is
        // the only thing that can - and it must fail the start rather than allow the request-time 500.
        await using var host = await ValidatedHostedApp.StartAsync(outer =>
        {
            var a = HostedRouteDeny.Group(outer, "/shared", ProbeDenial("A"));
            a.MapGet("/{id}", (int id) => Results.Json(new { id }));

            var b = HostedRouteDeny.Group(outer, "/shared", ProbeDenial("B"));
            b.MapPost("/{name}", (string name) => Results.Json(new { name }));
        });

        var error = Assert.Throws<InvalidOperationException>(() => HostedRefusalRouteSpace.Validate(host.Endpoints));
        Assert.Contains("compete for the same route shape", error.Message);
    }

    [Fact]
    public async Task A_single_family_denying_one_shape_under_two_verbs_and_cases_dedups_to_one_refusal()
    {
        // The Codex counterexample within ONE family: GET /x/{id} and POST /X/{name}. The case-folded shape
        // key collapses them to a SINGLE verb-less refusal, so the validator sees no tie and every denied
        // path answers the refusal rather than the 500 the un-folded key would have produced.
        await using var host = await ValidatedHostedApp.StartAsync(outer =>
        {
            var group = HostedRouteDeny.Group(outer, "", ProbeDenial());
            group.MapGet("/x/{id}", (int id) => Results.Json(new { id }));
            group.MapPost("/X/{name}", (string name) => Results.Json(new { name }));
        });

        HostedRefusalRouteSpace.Validate(host.Endpoints);

        var refusals = host.Endpoints.OfType<RouteEndpoint>()
            .Count(e => e.Metadata.GetMetadata<HostedRefusalMarker>() is not null);
        Assert.Equal(1, refusals);

        foreach (var path in new[] { "/x/7", "/x/anything", "/X/7" })
        {
            var response = await host.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    private static HostedDenial ProbeDenial(string family = "probe") => ProbeDenialFactory.Make(family);
}

/// <summary>The refusal payload used by the exclusive and cross-group hosts.</summary>
internal static class ProbeDenialFactory
{
    public static HostedDenial Make(string family = "probe") => new(
        family: family,
        message: "this family is not available on the hosted gateway",
        reason: "the probe family exists only to prove the primitive",
        unDenyInstruction: "nothing to un-deny: this family is a test fixture and stores nothing");
}

/// <summary>
/// A minimal HOSTED (or, on request, self-host) app that lets a test map families the way an adopter does,
/// then reads the FINALISED endpoint set and probes it. The endpoint set is captured the same way
/// GatewayHost captures it - the aggregate <see cref="EndpointDataSource"/> after every Map - so the
/// validator under test sees exactly what it sees in production.
/// </summary>
internal sealed class ValidatedHostedApp : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _http;
    private readonly string? _priorHosted;

    public IReadOnlyList<Endpoint> Endpoints { get; }

    private ValidatedHostedApp(WebApplication app, HttpClient http, string? priorHosted, IReadOnlyList<Endpoint> endpoints)
    {
        _app = app;
        _http = http;
        _priorHosted = priorHosted;
        Endpoints = endpoints;
    }

    public static async Task<ValidatedHostedApp> StartAsync(Action<IEndpointRouteBuilder> map, bool hosted = true)
    {
        var priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", hosted ? "1" : null);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        map(app);

        await app.StartAsync();

        // The FINALISED endpoint set, read the same way GatewayHost reads it - the aggregate
        // EndpointDataSource - but after Start, which is when the routing middleware materialises the group
        // endpoints into it.
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return new ValidatedHostedApp(app, http, priorHosted, endpoints);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => _http.GetAsync(path);

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
    }
}

/// <summary>
/// Serialises these classes. They set the process-wide <c>CC_GATEWAY_HOSTED</c> variable, and one class
/// running while another flips it would produce exactly the kind of unexplained cross-class contamination
/// this mission has already had to chase down inside its own instrument.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HostedRouteDenyCollection
{
    public const string Name = "hosted-route-deny";
}

/// <summary>
/// A minimal Gateway-shaped host carrying ONE denied family, mapped through the typed handle, plus a
/// neighbouring route mapped OUTSIDE the family that must keep serving.
/// </summary>
internal sealed class DenyProbeHost : IAsyncDisposable
{
    public const string RefusalMessage = "this family is not available on the hosted gateway";

    private readonly WebApplication _app;
    private readonly HttpClient _http;
    private readonly string? _priorHosted;

    private DenyProbeHost(WebApplication app, HttpClient http, string? priorHosted)
    {
        _app = app;
        _http = http;
        _priorHosted = priorHosted;
    }

    /// <param name="nonHostedForm">
    /// WHICH non-hosted form to set when <paramref name="hosted"/> is false. There are TWO declared ways to
    /// be non-hosted - the variable ABSENT, and the variable present but not "1" - and a control that only
    /// ever exercises one of them proves the guard for one of them. Worse, a control that leans on the
    /// variable being absent passes only because the test runner happened to leave it unset, which is an
    /// ambient condition and not a proof.
    /// </param>
    public static async Task<DenyProbeHost> StartAsync(bool hosted, string? nonHostedForm = null)
    {
        var priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", hosted ? "1" : nonHostedForm);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");

        MapFamily(app);

        await app.StartAsync();
        var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return new DenyProbeHost(app, http, priorHosted);
    }

    /// <summary>
    /// The family, mapped the way an adopting family maps: the group is opened here and the routes go
    /// through the typed handle. The neighbour is mapped on the OUTER builder, which is what a route outside
    /// the denied family looks like.
    /// </summary>
    private static void MapFamily(IEndpointRouteBuilder outer)
    {
        var denial = new HostedDenial(
            family: "probe",
            message: RefusalMessage,
            reason: "the probe family exists only to prove the primitive",
            unDenyInstruction: "nothing to un-deny: this family is a test fixture and stores nothing");

        var group = HostedRouteDeny.Group(outer, "/family", denial);

        group.MapPost("/echo", (EchoBody body) => Results.Json(new { echoed = body.Text }));
        group.MapPost("/custom", (ObservableBinding probe) => Results.Json(new { probe = probe.Value }));
        group.MapPost("/typed/{id:int}", (int id, EchoBody body) => Results.Json(new { id, body.Text }));

        // TWO verbs on ONE path: the multi-verb shape adopting families rely on. On hosted these must
        // collapse to a single verb-less refusal rather than two endpoints that tie.
        group.MapGet("/multi", () => Results.Json(new { multi = "get" }));
        group.MapPut("/multi", (EchoBody body) => Results.Json(new { multi = body.Text }));

        // Neighbours mapped OUTSIDE the family: neither is denied and both must keep serving on hosted.
        // A literal sibling under the same path shape (precedence protects it) and a route on a different
        // path shape entirely (the refusal must not have widened across a path boundary).
        outer.MapPost("/family/typed/summary", () => Results.Json(new { neighbour = "still serving" }));
        outer.MapPost("/family/other/{name}", (string name) => Results.Json(new { neighbour = name }));

        // Mapped LAST, with a bound body: the future route. A parameterless GET here would prove nothing,
        // because a parameterless GET is the one shape the original defect cannot be seen through.
        group.MapPost("/future", (EchoBody body) => Results.Json(new { future = body.Text }));
    }

    public Task<HttpResponseMessage> PostJsonAsync(string path, string json)
        => PostAsync(path, json, "application/json");

    public Task<HttpResponseMessage> PostAsync(string path, string content, string mediaType)
        => _http.PostAsync(path, new StringContent(content, Encoding.UTF8, mediaType));

    public Task<HttpResponseMessage> GetAsync(string path) => _http.GetAsync(path);

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path)
        => _http.SendAsync(new HttpRequestMessage(method, path));

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
    }

    private sealed record EchoBody(string Text);
}

/// <summary>
/// A parameter whose BINDING IS OBSERVABLE. Argument binding leaves no trace of its own, so proving that
/// nothing bound requires a parameter that records the fact it was bound. This is the instrument for the
/// central claim - not that the response was right, but that no handler-bound code ran at all.
/// </summary>
internal sealed class ObservableBinding
{
    private static int _count;

    public string Value { get; init; } = "";

    public static int Count => Volatile.Read(ref _count);

    public static void Reset() => Interlocked.Exchange(ref _count, 0);

    public static ValueTask<ObservableBinding?> BindAsync(HttpContext context)
    {
        Interlocked.Increment(ref _count);
        return ValueTask.FromResult<ObservableBinding?>(new ObservableBinding { Value = "bound" });
    }
}
