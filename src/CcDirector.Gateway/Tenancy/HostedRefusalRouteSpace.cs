using System;
using System.Collections.Generic;
using System.Linq;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// Marks an endpoint as a hosted refusal, so the FINALISED route space can be checked once everything has
/// been mapped. Carries the family and the pattern the family actually asked for, which is what makes a
/// failure message name the two routes a human has to go and look at.
/// </summary>
internal sealed record HostedRefusalMarker(HostedDenial Denial, string SourcePattern);

/// <summary>
/// Validates the route space AFTER every endpoint has been mapped, and fails the Gateway at startup on a
/// conflict rather than letting it surface as a 500 on a denied route the first time somebody calls it.
///
/// WHY THIS EXISTS SEPARATELY FROM THE PER-GROUP CHECK. <see cref="HostedDenyGroup"/> can only see its own
/// group: it prevents one family from mapping two refusals that tie. It cannot see
///   - two DIFFERENT denied families whose refusal patterns land on the same route shape, or
///   - a refusal that ties with an UNDENIED neighbour mapped somewhere else entirely.
/// Both are decided by the whole route space, so both can only be checked once the whole route space
/// exists. The first is a refusal that answers 500 instead of refusing. The second is worse in a different
/// direction: a tie against a live route is an OUTAGE on something nobody denied.
///
/// WHY A TIE AND NOT AN OVERLAP. Refusal patterns are deliberately WIDE - policies are stripped, so
/// <c>/x/{id}</c> refuses where the real route wanted an integer. Widening is the point, and it overlaps
/// neighbours constantly and harmlessly, because route precedence resolves it: a literal segment outranks a
/// parameter segment, so a live <c>/x/summary</c> still wins over a refusing <c>/x/{id}</c>. What precedence
/// CANNOT resolve is an exact tie - the same shape in the same positions - and that is the only case this
/// validator rejects. Rejecting mere overlap would refuse to start on the normal, correct arrangement.
/// </summary>
internal static class HostedRefusalRouteSpace
{
    /// <summary>
    /// Reads every endpoint the application has mapped and throws on the first conflict. Call ONCE, after
    /// all mapping is done and before the host starts serving. A no-op when nothing is refused, so it is
    /// inert on self-host where no refusal endpoint exists.
    /// </summary>
    public static void Validate(IEnumerable<Endpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var routes = endpoints.OfType<RouteEndpoint>().ToList();

        var refusals = routes
            .Select(e => (Endpoint: e, Marker: e.Metadata.GetMetadata<HostedRefusalMarker>()))
            .Where(x => x.Marker is not null)
            .ToList();

        if (refusals.Count == 0)
        {
            FileLog.Write("[HostedRefusalRouteSpace] no hosted refusals mapped - nothing to validate (self-host)");
            return;
        }

        var seen = new Dictionary<string, (HostedRefusalMarker Marker, RouteEndpoint Endpoint)>(StringComparer.Ordinal);

        foreach (var (endpoint, marker) in refusals)
        {
            var key = HostedRefusalPattern.ShapeKey(endpoint.RoutePattern);

            // REFUSAL versus REFUSAL. Two families whose refusals land on one shape tie at request time, and
            // the caller gets a 500 from the route that was supposed to refuse them.
            if (seen.TryGetValue(key, out var prior))
            {
                throw new InvalidOperationException(
                    $"Two hosted refusals occupy the same route shape and would tie at request time: " +
                    $"family '{prior.Marker!.Denial.Family}' pattern '{prior.Marker.SourcePattern}' and " +
                    $"family '{marker!.Denial.Family}' pattern '{marker.SourcePattern}'. " +
                    "A denied route that answers 500 is not a refusal. Give one family the route, or narrow one pattern.");
            }

            seen[key] = (marker!, endpoint);

            // REFUSAL versus a LIVE NEIGHBOUR. A tie here is an outage on a route nobody denied - the
            // over-refusal direction, which no leak-shaped test would ever notice.
            foreach (var other in routes)
            {
                if (ReferenceEquals(other, endpoint)) continue;
                if (other.Metadata.GetMetadata<HostedRefusalMarker>() is not null) continue;
                if (!SharesShape(endpoint.RoutePattern, other.RoutePattern)) continue;

                throw new InvalidOperationException(
                    $"The hosted refusal for family '{marker!.Denial.Family}' (pattern '{marker.SourcePattern}') " +
                    $"occupies the same route shape as the live route '{other.RoutePattern.RawText}', so the two " +
                    "would tie at request time and a route nobody denied would stop serving. " +
                    "Refusing to start: an outage on an undenied route is as much a defect as a leak on a denied one.");
            }
        }

        FileLog.Write($"[HostedRefusalRouteSpace] validated {refusals.Count} hosted refusal route(s) against " +
                      $"{routes.Count} mapped route(s) - no ties");
    }

    /// <summary>
    /// True when two patterns occupy the SAME shape, which is the only relation route precedence cannot
    /// resolve. Compared through the shape key so parameter names and policies are ignored - a live
    /// <c>/x/{name:alpha}</c> ties with a refusing <c>/x/{id}</c>, and the differing name and policy are
    /// exactly the details that would hide it from a text comparison.
    /// </summary>
    private static bool SharesShape(RoutePattern left, RoutePattern right)
    {
        try
        {
            return string.Equals(HostedRefusalPattern.ShapeKey(left), HostedRefusalPattern.ShapeKey(right),
                StringComparison.Ordinal);
        }
        catch (NotSupportedException)
        {
            // A live route may legitimately contain a part shape the refusal normaliser does not model. It
            // cannot be a refusal (those are built by the normaliser), so it cannot be compared - and an
            // uncomparable route is not evidence of a tie.
            return false;
        }
    }
}
