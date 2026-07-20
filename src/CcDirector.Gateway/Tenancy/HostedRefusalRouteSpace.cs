using System;
using System.Collections.Generic;
using System.Linq;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

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
/// mistake: <b>the matcher's ambiguity relation is not reachable from public API, so any check for it is a
/// MODEL of framework semantics rather than the semantics themselves.</b> The first instance was a
/// hand-written scan standing in for the route parser; this was a hand-rolled key standing in for the
/// matcher. Both were correct on the cases their author thought of and wrong on cases the framework
/// already knew about.
///
/// So this no longer models ambiguity. It removes the conditions under which ambiguity can arise:
///
///   - an EXCLUSIVE-PREFIX family maps ONE catch-all refusal under a prefix nothing else may serve. One
///     refusal cannot tie with itself, and exclusivity is checked by simple PREFIX CONTAINMENT - a
///     comparison this code can actually perform correctly, unlike an ambiguity relation.
///   - a PER-ROUTE family maps a refusal per route it actually declares. Nothing is synthesised, so no
///     pattern exists that the family did not already have.
///
/// The residual case is stated rather than hidden: normalising a pattern WIDENS it, so two of a family's
/// own routes that differ only by a parameter policy normalise to the same refusal and are de-duplicated
/// by exact normalised text. Two routes that tie for some other reason would already tie in that family's
/// own production route table, before any deny existed.
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

        var refusals = routes.Count(e => e.Metadata.GetMetadata<HostedRefusalMarker>() is not null);

        if (refusals == 0 && exclusive.Count == 0)
        {
            FileLog.Write("[HostedRefusalRouteSpace] no hosted refusals mapped - nothing to validate (self-host)");
            return;
        }

        foreach (var (endpoint, marker) in exclusive)
        {
            var prefix = marker!.Prefix.TrimEnd('/');

            // EXCLUSIVITY, by prefix containment. Everything under this prefix belongs to the denied family;
            // a live route there would be swallowed by the catch-all refusal, which is an OUTAGE on a route
            // nobody denied - the over-refusal direction, and the one no leak-shaped test would notice.
            foreach (var other in routes)
            {
                if (ReferenceEquals(other, endpoint)) continue;
                if (other.Metadata.GetMetadata<HostedRefusalMarker>() is not null) continue;
                if (other.Metadata.GetMetadata<HostedExclusivePrefixMarker>() is not null) continue;

                var otherPath = "/" + (other.RoutePattern.RawText ?? string.Empty).TrimStart('/');
                if (!otherPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(otherPath, prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                throw new InvalidOperationException(
                    $"The hosted denial for family '{marker.Denial.Family}' claims the prefix '{marker.Prefix}' " +
                    $"EXCLUSIVELY, but the live route '{other.RoutePattern.RawText}' serves underneath it. The " +
                    "catch-all refusal would take that route off the air. Refusing to start: an outage on an " +
                    "undenied route is as much a defect as a leak on a denied one. Either that route belongs to " +
                    "the denied family, or this family cannot claim the prefix exclusively and owes per-route " +
                    "refusals instead.");
            }
        }

        FileLog.Write($"[HostedRefusalRouteSpace] validated {exclusive.Count} exclusive-prefix claim(s) and " +
                      $"{refusals} refusal route(s) against {routes.Count} mapped route(s)");
    }
}
