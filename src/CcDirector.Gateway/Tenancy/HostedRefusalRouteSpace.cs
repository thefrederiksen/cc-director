using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// Marks an endpoint as a hosted refusal, so the FINALISED route space can be checked once everything has
/// been mapped. Carries the family and the pattern the family asked for, so a failure message names the
/// two routes a human has to go and look at.
/// </summary>
internal sealed record HostedRefusalMarker(HostedDenial Denial, string SourcePattern);

/// <summary>
/// Marks a group as claiming a prefix EXCLUSIVELY on hosted - nothing outside the denied family may serve
/// a path underneath it. Recorded as metadata so the claim is checked against the finalised route space
/// rather than trusted.
/// </summary>
internal sealed record HostedExclusivePrefixMarker(HostedDenial Denial, string Prefix);

/// <summary>
/// Validates the route space AFTER every endpoint has been mapped, and fails the Gateway at startup rather
/// than letting a conflict surface as a 500 on a denied route the first time somebody calls it.
///
/// WHAT THIS CHECKS, AND - MORE IMPORTANTLY - WHAT IT DELIBERATELY NO LONGER TRIES TO CHECK.
///
/// A previous version of this file tried to detect whether two route patterns would TIE in the matcher, by
/// comparing a hand-rolled "shape key". It was wrong, and it was wrong in the way that matters: three
/// independent route pairs escaped it and returned HTTP 500 at request time - a standard parameter against
/// an optional one on a present segment, literal case variants, and equal-precedence complex segments
/// differing only by their separator character.
///
/// The reason it was wrong is worth keeping, because it is the second time this primitive made the same
/// mistake: <b>the matcher's FULL ambiguity relation is not reachable from public API, so any check for the
/// WHOLE of it is a MODEL of framework semantics rather than the semantics themselves.</b> The first
/// instance was a hand-written scan standing in for the route parser; that one was a hand-rolled key
/// standing in for the matcher. Both were correct on the cases their author thought of and wrong on cases
/// the framework already knew about.
///
/// So this does not try to decide the whole ambiguity relation. It removes the conditions under which
/// ambiguity can arise, and it fails loud on the ONE class of tie that refusal substitution INTRODUCES -
/// which is a strictly smaller and decidable question than "would these two patterns ever tie":
///
///   - an EXCLUSIVE-PREFIX family maps ONE catch-all refusal under a prefix nothing else may serve, and
///     maps NO per-route refusal (the handle discards each handler). One refusal cannot tie with itself.
///     Exclusivity is checked by PREFIX CONTAINMENT against the finalised route patterns STRUCTURALLY - the
///     prefix is required to be literal at construction, and a live route's path is read from its parsed
///     segments, never from RawText, which a model-built pattern leaves null.
///   - a PER-ROUTE family maps a refusal per route it actually declares. Nothing is synthesised, so no
///     pattern exists that the family did not already have. But substitution DROPS the HTTP method and the
///     parameter policies, and folds literal case - so two routes the family held apart by METHOD, or two
///     families denying the same path under different verbs, become verb-less refusals that TIE where the
///     originals did not. That tie is introduced HERE, by this primitive, so it is caught HERE: any two
///     refusals whose finalised patterns share a case-insensitive structural SHAPE would compete, and any
///     refusal sharing a shape with a LIVE route would compete with it. Either fails the start.
///
/// What this still does NOT claim to catch is a tie that would ALREADY exist in a family's own production
/// route table before any deny - an optional parameter against a shorter path, say. That tie is not
/// introduced by substitution; it is the family's own, and it would surface off hosted too. Deciding it in
/// general is the unreachable relation. Deciding the introduced subset - identical structural shape among
/// verb-less endpoints - is not, and is what this checks.
/// </summary>
internal static class HostedRefusalRouteSpace
{
    /// <summary>
    /// Reads every endpoint the application has mapped and throws on the first violation. Call ONCE, after
    /// all mapping is done and before the host serves. Inert when nothing is refused, so it does nothing on
    /// self-host, where no refusal endpoint exists.
    /// </summary>
    public static void Validate(IEnumerable<Endpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var routes = endpoints.OfType<RouteEndpoint>().ToList();

        var exclusive = routes
            .Select(e => (Endpoint: e, Marker: e.Metadata.GetMetadata<HostedExclusivePrefixMarker>()))
            .Where(x => x.Marker is not null)
            .ToList();

        var refusalEndpoints = routes
            .Where(e => e.Metadata.GetMetadata<HostedRefusalMarker>() is not null)
            .ToList();

        if (refusalEndpoints.Count == 0 && exclusive.Count == 0)
        {
            FileLog.Write("[HostedRefusalRouteSpace] no hosted refusals mapped - nothing to validate (self-host)");
            return;
        }

        ValidateNoRefusalTies(routes, refusalEndpoints);
        ValidateExclusiveContainment(routes, exclusive);

        FileLog.Write($"[HostedRefusalRouteSpace] validated {exclusive.Count} exclusive-prefix claim(s) and " +
                      $"{refusalEndpoints.Count} refusal route(s) against {routes.Count} mapped route(s)");
    }

    /// <summary>
    /// Fails the start on a tie INTRODUCED by refusal substitution: two verb-less refusals whose finalised
    /// patterns share a case-insensitive structural shape, or a refusal that shares a shape with a live
    /// route. Both compete in the matcher and answer 500/ambiguous at request time - on a DENIED route, the
    /// one nobody exercises until a caller does. This is the introduced subset of the ambiguity relation, not
    /// the whole of it: two verb-less endpoints on the same structural shape always tie, so equal shape keys
    /// are a SOUND tie witness, never a false alarm.
    /// </summary>
    private static void ValidateNoRefusalTies(List<RouteEndpoint> routes, List<RouteEndpoint> refusalEndpoints)
    {
        // The live route space, keyed by structural shape. Refusals are excluded here - they are checked
        // against each other and against these lives below.
        var liveByShape = new Dictionary<string, RouteEndpoint>(StringComparer.Ordinal);
        foreach (var live in routes)
        {
            if (live.Metadata.GetMetadata<HostedRefusalMarker>() is not null) continue;
            liveByShape[HostedRefusalPattern.ShapeKey(live.RoutePattern)] = live;
        }

        var refusalByShape = new Dictionary<string, RouteEndpoint>(StringComparer.Ordinal);
        foreach (var refusal in refusalEndpoints)
        {
            var shape = HostedRefusalPattern.ShapeKey(refusal.RoutePattern);
            var family = refusal.Metadata.GetMetadata<HostedRefusalMarker>()!.Denial.Family;

            if (refusalByShape.TryGetValue(shape, out var prior) && !ReferenceEquals(prior, refusal))
            {
                var priorFamily = prior.Metadata.GetMetadata<HostedRefusalMarker>()!.Denial.Family;
                throw new InvalidOperationException(
                    $"Two hosted refusals compete for the same route shape: '{PatternText(prior)}' (family " +
                    $"'{priorFamily}') and '{PatternText(refusal)}' (family '{family}'). Refusal substitution " +
                    "drops the HTTP method and folds literal case, so two routes held apart by verb or case " +
                    "become verb-less refusals that TIE - a 500 on a denied route at request time. Refusing to " +
                    "start instead. These two denied shapes must be reconciled to a single refusal.");
            }

            refusalByShape[shape] = refusal;

            if (liveByShape.TryGetValue(shape, out var live))
                throw new InvalidOperationException(
                    $"The hosted refusal '{PatternText(refusal)}' (family '{family}') competes for the same route " +
                    $"shape as the live route '{PatternText(live)}'. The verb-less refusal matches every method, so " +
                    "it ties with the live route wherever their shapes coincide - a 500 on the denied route rather " +
                    "than a clean answer. Refusing to start: either that live route belongs to the denied family, " +
                    "or the family is denying a shape it does not own.");
        }
    }

    /// <summary>
    /// Fails the start when a live route serves BENEATH a prefix a family claimed exclusively - an OUTAGE on
    /// a route nobody denied, which no leak-shaped test would notice. Containment is computed from the
    /// finalised route patterns STRUCTURALLY: the exclusive prefix is literal (enforced at construction), and
    /// the live route's path is read from its parsed segments, so a model-built pattern with a null RawText is
    /// compared correctly rather than being read as the root "/".
    /// </summary>
    private static void ValidateExclusiveContainment(
        List<RouteEndpoint> routes,
        List<(RouteEndpoint Endpoint, HostedExclusivePrefixMarker? Marker)> exclusive)
    {
        foreach (var (endpoint, marker) in exclusive)
        {
            var prefix = marker!.Prefix.TrimEnd('/').ToLowerInvariant();

            foreach (var other in routes)
            {
                if (ReferenceEquals(other, endpoint)) continue;
                if (other.Metadata.GetMetadata<HostedRefusalMarker>() is not null) continue;
                if (other.Metadata.GetMetadata<HostedExclusivePrefixMarker>() is not null) continue;

                var otherPath = StructuralPath(other.RoutePattern);
                if (!otherPath.StartsWith(prefix + "/", StringComparison.Ordinal) &&
                    !string.Equals(otherPath, prefix, StringComparison.Ordinal))
                    continue;

                throw new InvalidOperationException(
                    $"The hosted denial for family '{marker.Denial.Family}' claims the prefix '{marker.Prefix}' " +
                    $"EXCLUSIVELY, but the live route '{PatternText(other)}' serves underneath it. The " +
                    "catch-all refusal would take that route off the air. Refusing to start: an outage on an " +
                    "undenied route is as much a defect as a leak on a denied one. Either that route belongs to " +
                    "the denied family, or this family cannot claim the prefix exclusively and owes per-route " +
                    "refusals instead.");
            }
        }
    }

    /// <summary>
    /// A route's path as a comparable, case-folded string, built from its PARSED SEGMENTS and never its
    /// RawText - a pattern rebuilt from the route model (as the refusal patterns are) has a null RawText, and
    /// reading that as the root "/" is precisely the containment miss this closes. Literal text is folded to
    /// lower case to match the matcher; a parameter part is emitted as a sentinel that cannot equal any
    /// literal prefix segment, so a parameter never satisfies containment against a literal prefix.
    /// </summary>
    private static string StructuralPath(RoutePattern pattern)
    {
        var sb = new StringBuilder();

        foreach (var segment in pattern.PathSegments)
        {
            sb.Append('/');
            foreach (var part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        sb.Append(literal.Content.ToLowerInvariant());
                        break;
                    case RoutePatternSeparatorPart separator:
                        sb.Append(separator.Content.ToLowerInvariant());
                        break;
                    case RoutePatternParameterPart:
                        // A sentinel a literal path segment cannot contain - route literals never carry a
                        // brace, and the exclusive prefix is required literal - so a parameter segment can
                        // never be read as a literal match for an exclusive prefix segment.
                        sb.Append("{}");
                        break;
                    default:
                        throw new NotSupportedException(
                            $"Cannot read a structural path for a route part of type '{part.GetType().Name}'.");
                }
            }
        }

        return sb.Length == 0 ? "/" : sb.ToString();
    }

    /// <summary>The pattern text for a human-facing message, falling back to the structural path when a
    /// model-built pattern has no RawText - the message never decides anything, so a readable fallback is
    /// fine here where it would be a bug in the containment check above.</summary>
    private static string PatternText(RouteEndpoint endpoint)
        => endpoint.RoutePattern.RawText ?? StructuralPath(endpoint.RoutePattern);
}
