using System;
using System.Linq;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Xunit;

namespace CcDirector.Gateway.Tests.Tenancy;

/// <summary>
/// The route-pattern normaliser: what the refusal is mapped on, and the grammar it claims to cover.
///
/// WHY THIS CLASS EXISTS AS ITS OWN THING. The normaliser was originally hand-written against the pattern
/// TEXT and validated against simple inline policies only - and that is a narrower question set than the
/// grammar it has to survive. The route grammar has optional parameters, default values, catch-alls with
/// two sigils, escaped braces, and policy arguments that themselves contain the delimiters a text scan is
/// looking for. A scan that is right on simple shapes and silently wrong on exotic ones does not produce a
/// visible bug: it produces a refusal that does not cover its own route, while the family's author believes
/// it does. That is manufactured coverage, which is the exact defect the boundary exists to remove.
///
/// So the normaliser now rebuilds the PARSED MODEL and this class states the grammar it covers, one test
/// per element, plus the behaviour on something it does not model: it THROWS, and does not pass through.
/// </summary>
public sealed class HostedRefusalPatternTests
{
    /// <summary>
    /// The ONE transformation. Everything else in these tests is a preservation claim; this is the change,
    /// and it is the reason the whole class exists - a refusal carrying the real route's constraint is never
    /// selected for the request that fails that constraint, which is the hole this closes.
    /// </summary>
    [Theory]
    [InlineData("/x/{id:int}", "id")]
    [InlineData("/x/{id:int:min(1)}", "id")]                    // several policies on one parameter
    [InlineData("/x/{name:regex(^[[a-z]]+$)}", "name")]          // a policy argument containing delimiters
    [InlineData("/x/{a:int}/y/{b:alpha}", "a")]                  // several constrained parameters
    public void Every_parameter_policy_is_removed(string pattern, string firstParameterName)
    {
        var normalised = HostedRefusalPattern.WithoutPolicies(pattern, "probe");

        Assert.All(AllParameters(normalised), p => Assert.Empty(p.ParameterPolicies));
        Assert.Equal(firstParameterName, AllParameters(normalised).First().Name);
    }

    /// <summary>
    /// Preservation, element by element. Each row is a piece of grammar the normaliser CLAIMS to cover, and
    /// the claim is worth exactly as much as the row that tests it - which is why they are enumerated rather
    /// than sampled. Losing any of these would change which requests the refusal matches.
    /// </summary>
    [Theory]
    [InlineData("/x/{id?}")]                       // optional
    [InlineData("/x/{id:int?}")]                   // optional AND constrained
    [InlineData("/x/{id=7}")]                      // default value
    [InlineData("/x/{id:int=7}")]                  // default AND constrained
    [InlineData("/x/{**rest}")]                    // catch-all, slash-preserving
    [InlineData("/x/{*rest}")]                     // catch-all, the other sigil
    [InlineData("/x/y")]                           // pure literals
    [InlineData("/x/{a}-{b}")]                     // two parameters in one complex segment
    [InlineData("/x/file.{ext}")]                  // literal and parameter sharing a segment
    [InlineData("/{a}/{b}/{c}")]                   // several segments
    public void The_shape_of_the_route_survives_normalisation(string pattern)
    {
        var original = RoutePatternFactory.Parse(pattern);
        var normalised = HostedRefusalPattern.WithoutPolicies(pattern, "probe");

        // Same segment structure, part for part.
        Assert.Equal(original.PathSegments.Count, normalised.PathSegments.Count);
        foreach (var (before, after) in original.PathSegments.Zip(normalised.PathSegments))
            Assert.Equal(before.Parts.Count, after.Parts.Count);

        // Same parameters, with kind and default intact - optionality and catch-all semantics are the real
        // route's, not something the normaliser decided.
        var originalParameters = AllParameters(original).ToList();
        var normalisedParameters = AllParameters(normalised).ToList();

        Assert.Equal(originalParameters.Count, normalisedParameters.Count);
        foreach (var (before, after) in originalParameters.Zip(normalisedParameters))
        {
            Assert.Equal(before.Name, after.Name);
            Assert.Equal(before.ParameterKind, after.ParameterKind);
            Assert.Equal(before.Default, after.Default);
        }

        // Literal text is untouched, which is what keeps a refusal from widening across a path boundary.
        Assert.Equal(LiteralText(original), LiteralText(normalised));
    }

    /// <summary>
    /// A pattern the framework itself rejects is not the normaliser's to reinterpret. It is parsed by the
    /// framework's parser, so a malformed pattern fails there with the framework's own message rather than
    /// being quietly accepted by a lenient hand-written scan.
    /// </summary>
    [Theory]
    [InlineData("/x/{unclosed")]
    [InlineData("/x/{}")]
    public void A_malformed_pattern_is_refused_by_the_frameworks_own_parser(string pattern)
        => Assert.ThrowsAny<Exception>(() => HostedRefusalPattern.WithoutPolicies(pattern, "probe"));

    /// <summary>
    /// THE EXHAUSTIVENESS CLAIM, which is what the fail-loud rule actually rests on here.
    ///
    /// The normaliser handles three route part kinds and THROWS on anything else. That throw is
    /// unreachable today - and I would rather say so than imply it is covered: <c>RoutePatternPart</c> has
    /// an internal constructor, so the framework's three subclasses are the entire hierarchy and no test in
    /// this repository can manufacture a fourth. The defensive arm therefore has no test, deliberately, and
    /// this is the test that guards the assumption UNDERNEATH it instead.
    ///
    /// If a framework upgrade ever adds a fourth part kind, this reddens - which is the moment somebody
    /// needs to decide whether the normaliser handles it or refuses it. Without this, that upgrade would
    /// land silently and the first sign would be a startup failure on somebody's exotic route, or worse, a
    /// refusal that quietly did not cover it.
    /// </summary>
    [Fact]
    public void The_route_part_kinds_the_normaliser_handles_are_still_the_only_ones_that_exist()
    {
        var partKinds = typeof(RoutePatternPart).Assembly
            .GetTypes()
            .Where(t => t.IsSubclassOf(typeof(RoutePatternPart)))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "RoutePatternLiteralPart", "RoutePatternParameterPart", "RoutePatternSeparatorPart" },
            partKinds);
    }

    /// <summary>
    /// The shape key is what the DUPLICATE check compares, and this is the case text comparison misses:
    /// two patterns differing only in a parameter NAME are different strings and the same route. Mapping
    /// both produces endpoints that tie, and the tie surfaces at request time on the denied route.
    /// </summary>
    [Theory]
    [InlineData("/x/{id}", "/x/{name}", true)]              // same route, different spelling
    [InlineData("/x/{id:int}", "/x/{name:alpha}", true)]    // policies are not part of the shape either
    [InlineData("/x/{id}", "/x/{id?}", false)]              // optional does not compete with standard
    [InlineData("/x/{id}", "/x/{**id}", false)]             // nor does a catch-all
    [InlineData("/x/{id}", "/y/{id}", false)]               // different literal
    [InlineData("/x/{id}", "/x/{id}/y", false)]             // different length
    public void The_shape_key_sees_past_names_and_policies_but_not_past_kind_or_literals(
        string left, string right, bool sameShape)
    {
        var leftKey = HostedRefusalPattern.ShapeKey(HostedRefusalPattern.WithoutPolicies(left, "probe"));
        var rightKey = HostedRefusalPattern.ShapeKey(HostedRefusalPattern.WithoutPolicies(right, "probe"));

        Assert.Equal(sameShape, leftKey == rightKey);
    }

    private static System.Collections.Generic.IEnumerable<RoutePatternParameterPart> AllParameters(RoutePattern pattern)
        => pattern.PathSegments.SelectMany(s => s.Parts).OfType<RoutePatternParameterPart>();

    private static string LiteralText(RoutePattern pattern)
        => string.Join("/", pattern.PathSegments
            .SelectMany(s => s.Parts)
            .OfType<RoutePatternLiteralPart>()
            .Select(p => p.Content));

}

/// <summary>
/// The FINALISED route space check: the conflicts that only exist once everything has been mapped, and
/// which the per-group duplicate check cannot see because it can only see its own group.
///
/// Two conflicts, in opposite directions, and only one of them is a leak:
///   - refusal against refusal - a denied route answers 500 instead of refusing;
///   - refusal against a LIVE route - a route nobody denied stops serving. That is an OUTAGE, and no
///     leak-shaped test in this repository would ever notice it.
///
/// What is NOT a conflict is the ordinary case, and getting that wrong would refuse to start on every
/// correct arrangement: refusal patterns are deliberately WIDE, so they overlap neighbours constantly, and
/// route precedence resolves the overlap. Only an exact tie is rejected.
/// </summary>
public sealed class HostedRefusalRouteSpaceTests
{
    private static readonly HostedDenial Denial = new(
        family: "probe",
        message: "not available on the hosted gateway",
        reason: "the probe family exists to prove the route-space check",
        unDenyInstruction: "nothing to un-deny: a test fixture");

    private static readonly HostedDenial OtherDenial = new(
        family: "other-probe",
        message: "also not available on the hosted gateway",
        reason: "a second family, to prove refusal-versus-refusal detection",
        unDenyInstruction: "nothing to un-deny: a test fixture");

    [Fact]
    public void Two_refusals_on_the_same_route_shape_fail_the_start_rather_than_the_request()
    {
        // Different spelling, same route. This is the pair a text comparison lets through.
        var endpoints = new[]
        {
            RefusalEndpoint("/x/{id}", Denial),
            RefusalEndpoint("/x/{name}", OtherDenial),
        };

        var error = Assert.Throws<InvalidOperationException>(() => HostedRefusalRouteSpace.Validate(endpoints));

        Assert.Contains("probe", error.Message);
        Assert.Contains("other-probe", error.Message);
    }

    [Fact]
    public void A_refusal_tying_with_a_live_route_fails_the_start_because_that_is_an_outage()
    {
        var endpoints = new[]
        {
            RefusalEndpoint("/x/{id}", Denial),
            LiveEndpoint("/x/{slug}"),
        };

        var error = Assert.Throws<InvalidOperationException>(() => HostedRefusalRouteSpace.Validate(endpoints));

        Assert.Contains("nobody denied", error.Message);
    }

    /// <summary>
    /// The case that must NOT fail: a refusal is deliberately wider than the route it replaces, so it
    /// overlaps live neighbours all the time. Precedence resolves it - a literal outranks a parameter - and
    /// a validator that rejected overlap rather than ties would refuse to start on the normal arrangement.
    /// </summary>
    [Fact]
    public void A_refusal_overlapping_a_more_specific_live_route_is_allowed()
    {
        var endpoints = new[]
        {
            RefusalEndpoint("/x/{id}", Denial),
            LiveEndpoint("/x/summary"),      // literal beats the refusal's parameter
            LiveEndpoint("/x/{id}/detail"),  // different length
            LiveEndpoint("/y/{id}"),         // different literal
        };

        HostedRefusalRouteSpace.Validate(endpoints);
    }

    [Fact]
    public void A_route_space_with_no_refusals_is_inert()
        => HostedRefusalRouteSpace.Validate(new[] { LiveEndpoint("/x/{id}"), LiveEndpoint("/y") });

    private static RouteEndpoint RefusalEndpoint(string pattern, HostedDenial denial)
        => new(
            _ => Task.CompletedTask,
            HostedRefusalPattern.WithoutPolicies(pattern, denial.Family),
            order: 0,
            new EndpointMetadataCollection(new HostedRefusalMarker(denial, pattern)),
            displayName: pattern);

    private static RouteEndpoint LiveEndpoint(string pattern)
        => new(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(pattern),
            order: 0,
            EndpointMetadataCollection.Empty,
            displayName: pattern);
}
