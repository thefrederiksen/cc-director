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
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

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

    [Theory]
    [InlineData("/x/{id:int}", "/x/{id}")]
    [InlineData("/x/{id:int}/y/{name:alpha}", "/x/{id}/y/{name}")]
    [InlineData("/x/{id}", "/x/{id}")]
    [InlineData("/x/plain", "/x/plain")]
    [InlineData("/x/{**rest}", "/x/{**rest}")]
    public void The_refusal_pattern_drops_inline_constraints_and_nothing_else(string pattern, string expected)
        => Assert.Equal(expected, HostedDenyGroup.StripInlineConstraints(pattern));
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

    public static async Task<DenyProbeHost> StartAsync(bool hosted)
    {
        var priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", hosted ? "1" : null);

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

        // A literal sibling under the same path shape, mapped OUTSIDE the family: it is not denied and must
        // keep serving on hosted. This is the over-refusal direction.
        outer.MapPost("/family/typed/summary", () => Results.Json(new { neighbour = "still serving" }));

        // Mapped LAST, with a bound body: the future route. A parameterless GET here would prove nothing,
        // because a parameterless GET is the one shape the original defect cannot be seen through.
        group.MapPost("/future", (EchoBody body) => Results.Json(new { future = body.Text }));
    }

    public Task<HttpResponseMessage> PostJsonAsync(string path, string json)
        => PostAsync(path, json, "application/json");

    public Task<HttpResponseMessage> PostAsync(string path, string content, string mediaType)
        => _http.PostAsync(path, new StringContent(content, Encoding.UTF8, mediaType));

    public Task<HttpResponseMessage> GetAsync(string path) => _http.GetAsync(path);

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
