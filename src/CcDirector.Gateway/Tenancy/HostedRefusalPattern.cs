using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.AspNetCore.Routing.Patterns;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// Turns a family's real route pattern into the pattern its REFUSAL is mapped on, by rebuilding the parsed
/// route MODEL rather than by editing the pattern text.
///
/// WHY THE MODEL AND NOT THE TEXT. The refusal has to match a request that the real route would REJECT -
/// a segment that fails an inline constraint fails endpoint selection, so a refusal carrying that same
/// constraint is never selected either and the framework answers instead of the refusal. Removing the
/// constraint is therefore load-bearing. Doing that by editing the pattern string means hand-writing a
/// parser for a grammar that is much richer than it first appears: optional parameters, default values,
/// catch-alls with two sigils, escaped braces, and policy arguments that themselves contain the very
/// delimiters the edit is scanning for. A hand-written scan is correct on the simple shapes it was tested
/// against and silently wrong elsewhere - and silently wrong here means a denied route that answers with
/// something other than the refusal, which is exactly the defect the whole primitive exists to remove.
///
/// So the pattern is PARSED by the framework's own parser, the parameter parts are rebuilt WITHOUT their
/// policies and with everything else preserved, and the result is handed back as a
/// <see cref="RoutePattern"/> - never re-serialised to text.
///
/// WHAT IT PRESERVES, exactly:
///   - literal segments and literal parts, unchanged;
///   - segment separators inside a complex segment, unchanged;
///   - parameter NAME, unchanged;
///   - parameter KIND - standard, optional, or catch-all - unchanged, so optionality and catch-all
///     matching semantics are the real route's;
///   - parameter DEFAULT VALUE, unchanged.
/// The ONLY thing removed is the parameter's policies, which is the only thing that can make selection
/// reject a request the refusal must answer.
///
/// WHAT IT REFUSES. A part kind this code does not recognise is a pattern it cannot claim to normalise, so
/// it THROWS at startup rather than passing the pattern through. Passing through would map a refusal that
/// still carried a constraint - the exact hole - while the family's author believed the route was covered.
/// A loud failure at startup is recoverable; a refusal that silently does not cover its route is the
/// false coverage this primitive was built to eliminate.
/// </summary>
internal static class HostedRefusalPattern
{
    /// <summary>
    /// Parses <paramref name="pattern"/> and returns the equivalent pattern with every parameter policy
    /// removed. Throws <see cref="NotSupportedException"/> on a pattern containing a part this code does
    /// not recognise, and lets the framework's own parser throw on a malformed pattern.
    /// </summary>
    public static RoutePattern WithoutPolicies(string pattern, string family)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        // The framework's parser, not ours. A malformed pattern throws here, with its message.
        var parsed = RoutePatternFactory.Parse(pattern);
        return WithoutPolicies(parsed, family);
    }

    /// <summary>The model-level rebuild, separated so a test can drive it from a parsed pattern.</summary>
    public static RoutePattern WithoutPolicies(RoutePattern parsed, string family)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        var segments = new List<RoutePatternPathSegment>(parsed.PathSegments.Count);

        foreach (var segment in parsed.PathSegments)
        {
            var parts = new List<RoutePatternPart>(segment.Parts.Count);

            foreach (var part in segment.Parts)
            {
                parts.Add(part switch
                {
                    RoutePatternLiteralPart literal => RoutePatternFactory.LiteralPart(literal.Content),

                    RoutePatternSeparatorPart separator => RoutePatternFactory.SeparatorPart(separator.Content),

                    // The one transformation: same name, same kind, same default, NO policies.
                    RoutePatternParameterPart parameter => RoutePatternFactory.ParameterPart(
                        parameter.Name,
                        parameter.Default,
                        parameter.ParameterKind),

                    _ => throw new NotSupportedException(
                        $"The hosted denial for '{family}' cannot normalise the route pattern '{parsed.RawText}': " +
                        $"it contains a route part of type '{part.GetType().Name}', which this boundary does not " +
                        "recognise. Refusing at startup rather than mapping a refusal that might not cover the " +
                        "route: a refusal that silently fails to cover its own route is worse than a Gateway " +
                        "that does not start."),
                });
            }

            segments.Add(RoutePatternFactory.Segment(parts));
        }

        return RoutePatternFactory.Pattern(segments);
    }

    /// <summary>
    /// The SHAPE KEY of a pattern: what the route matcher actually competes on, with parameter names and
    /// policies discarded.
    ///
    /// Two refusal patterns that differ only in the NAME of a parameter - <c>/x/{id}</c> and
    /// <c>/x/{name}</c> - are different strings and the same route. Mapping both produces two endpoints
    /// that tie, and the matcher throws at REQUEST time, on the denied route, which is the deny failing in
    /// the one way nothing notices until a caller tries it. Text equality cannot see that; this key can.
    ///
    /// The key encodes, per segment: a literal's text, a separator's text, and for a parameter its KIND
    /// only. Kind is included because an optional or catch-all parameter does not compete with a standard
    /// one in the same position.
    /// </summary>
    public static string ShapeKey(RoutePattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        var key = new StringBuilder();

        foreach (var segment in pattern.PathSegments)
        {
            key.Append('/');

            foreach (var part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        key.Append("L:").Append(literal.Content);
                        break;
                    case RoutePatternSeparatorPart separator:
                        key.Append("S:").Append(separator.Content);
                        break;
                    case RoutePatternParameterPart parameter:
                        key.Append("P:").Append(parameter.ParameterKind);
                        break;
                    default:
                        // Unreachable for a pattern that came through WithoutPolicies, which throws first.
                        throw new NotSupportedException(
                            $"Cannot compute a route shape key for a part of type '{part.GetType().Name}'.");
                }

                key.Append('|');
            }
        }

        return key.ToString();
    }
}
