using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// The ONE hosted-refusal boundary every deny family adopts. It exists because a refusal attached as a
/// route-group endpoint filter does NOT answer uniformly across request shapes, and the shapes it misses
/// are not the ones an obvious test exercises. The mechanism, the shapes, and the measurements behind that
/// are recorded in the PRIVATE architecture record; this file carries what a maintainer needs in order not
/// to undo the design, and no more.
///
/// The consequence that matters to anyone writing a proof: a future-route probe on a deny family MUST
/// include a BODY-BOUND POST and not only a parameterless GET, because a parameterless GET is precisely the
/// shape through which this class of defect cannot be seen.
///
/// HOW THIS CLOSES IT. On hosted the family's handler is NEVER MAPPED. In its place goes a verb-less
/// refusal route on the same pattern. So there is no binding step to get ahead of, no body parameter, no
/// inferred media-type constraint, and no method constraint - which means no request shape can be answered
/// by the framework ahead of the refusal:
///
/// | Request shape           | Answered by |
/// |-------------------------|-------------|
/// | valid body              | the refusal |
/// | malformed body          | the refusal |
/// | wrong media type        | the refusal |
/// | a verb never mapped     | the refusal |
/// | a route added LATER     | the refusal |
///
/// The wrong-verb row is why this is a verb-less route rather than a convention that replaces the endpoint's
/// request delegate. That convention closes the three body shapes but still lets endpoint SELECTION answer
/// 405 for a verb the group never mapped - which discloses that a route exists on a Gateway whose refusal
/// says it does not. A wrong verb IS a request shape, and the standard being enforced is that the refusal is
/// uniform across shapes.
///
/// WHY A TYPED HANDLE. The pre-binding property is proven ONCE, here, for every adopting family: an adopter
/// cannot be attached AND post-binding, because attachment is what maps the refusal in place of the handler.
/// That puts the whole weight on ATTACHMENT, so attachment is made structurally impossible to get wrong
/// rather than merely conventional - routes are mapped through <see cref="HostedDenyGroup"/>, a distinct
/// type obtainable only from <see cref="Group"/>. A family maps into the guarded group or it does not
/// compile. The earlier shape kept a guarded and an unguarded builder in scope, differing only by variable
/// name, so one changed receiver silently opened one route on hosted; here that is a signature change rather
/// than a typo.
///
/// SELF-HOST IS UNTOUCHED, AND THAT IS THE CONTROL. Off hosted every <c>Map</c> below maps the family's real
/// handler on the group exactly as an unguarded builder would, and no refusal route is created at all.
///
/// FAIL DIRECTION. The refusal payload is validated when it is CONSTRUCTED, so a family supplying a blank
/// message fails the Gateway at STARTUP - loudly, before serving - rather than serving an empty refusal that
/// reads like a working route. The hosted decision is read from <see cref="GatewayHostedMode.IsHosted"/>
/// directly, never from an optional argument a caller can omit: a security branch that depends on an
/// argument fails OPEN the moment somebody forgets it.
/// </summary>
public static class HostedRouteDeny
{
    /// <summary>
    /// Opens a route group whose every route - including routes mapped into it later, by anyone, with no
    /// deny written for them - is refused on the hosted Gateway, on every request shape, without any
    /// argument binding taking place. Returns the typed handle the family must map through.
    /// </summary>
    /// <param name="outer">The builder the group hangs off.</param>
    /// <param name="prefix">The group prefix, or <c>""</c> to keep route paths written out in full.</param>
    /// <param name="denial">This family's refusal payload - the only per-family configuration.</param>
    public static HostedDenyGroup Group(IEndpointRouteBuilder outer, string prefix, HostedDenial denial)
    {
        ArgumentNullException.ThrowIfNull(outer);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(denial);

        FileLog.Write($"[HostedRouteDeny] group family={denial.Family} prefix='{prefix}' hosted={GatewayHostedMode.IsHosted}" +
                      " - on hosted EVERY route in this group is refused on EVERY request shape, with no argument binding");

        return new HostedDenyGroup(outer.MapGroup(prefix), denial);
    }
}

/// <summary>
/// A family's refusal payload - the ONLY thing that differs between adopting families. Validated on
/// construction so a malformed one fails the Gateway at startup rather than serving a refusal that says
/// nothing.
/// </summary>
public sealed record HostedDenial
{
    /// <summary>The family name, for the log line and for telling refusals apart in a proof run.</summary>
    public string Family { get; }

    /// <summary>
    /// The single error string the caller receives. The refusal body carries this and nothing else, so the
    /// adopting family's test can assert an EXACT property set rather than the absence of today's payload
    /// keys - an absence-only assertion passes on a framework error too, and therefore cannot fail for the
    /// right reason.
    /// </summary>
    public string Message { get; }

    /// <summary>Why this family has no per-tenant answer to serve. Logged with every refusal.</summary>
    public string Reason { get; }

    /// <summary>
    /// What must happen before this deny is ever lifted. A deny stops the READ but not necessarily the
    /// WRITE, so data can keep accumulating behind it and be there in full on the day the deny lifts.
    /// Un-denying therefore means REMOVE the deny PLUS purge or migrate whatever accumulated - and that
    /// instruction is written here, AT the deny, rather than held as a general principle somebody has to
    /// remember at the moment they are least likely to.
    /// </summary>
    public string UnDenyInstruction { get; }

    /// <summary>
    /// The refusal status. 404 where the route does not exist as a concept on hosted: 403 would imply some
    /// credential could reach it, and none can.
    /// </summary>
    public int StatusCode { get; }

    /// <summary>
    /// Fails LOUDLY at startup on a payload that would produce a meaningless refusal. This is the
    /// no-fallback rule applied to a security primitive: a blank message would serve a refusal a caller
    /// cannot act on and a proof cannot assert, which is worse than not booting.
    /// </summary>
    public HostedDenial(
        string family,
        string message,
        string reason,
        string unDenyInstruction,
        int statusCode = StatusCodes.Status404NotFound)
    {
        Family = family;
        Message = message;
        Reason = reason;
        UnDenyInstruction = unDenyInstruction;
        StatusCode = statusCode;

        if (string.IsNullOrWhiteSpace(Family))
            throw new ArgumentException("A hosted denial must name its family.", nameof(Family));
        if (string.IsNullOrWhiteSpace(Message))
            throw new ArgumentException($"The hosted denial for '{Family}' must carry a message; a blank refusal tells a caller nothing.", nameof(Message));
        if (string.IsNullOrWhiteSpace(Reason))
            throw new ArgumentException($"The hosted denial for '{Family}' must state why the family has no per-tenant answer.", nameof(Reason));
        if (string.IsNullOrWhiteSpace(UnDenyInstruction))
            throw new ArgumentException($"The hosted denial for '{Family}' must state what un-denying requires, including what to purge or migrate.", nameof(UnDenyInstruction));
        if (StatusCode is < 400 or > 599)
            throw new ArgumentException($"The hosted denial for '{Family}' must refuse with a 4xx or 5xx status, not {StatusCode}.", nameof(StatusCode));
    }
}

/// <summary>
/// The typed handle a denied family maps its routes through. Obtainable only from
/// <see cref="HostedRouteDeny.Group"/>, which is what makes attachment structurally impossible to get wrong:
/// a family's mapping method takes ONE of these and therefore cannot be handed an unguarded builder without
/// changing its signature.
///
/// On hosted every method here maps a verb-less refusal on the pattern and DISCARDS the handler - the
/// handler is never mapped, so nothing binds. Off hosted every method maps the handler exactly as an
/// unguarded builder would.
/// </summary>
public sealed class HostedDenyGroup
{
    private readonly RouteGroupBuilder _group;
    private readonly HostedDenial _denial;

    // On hosted, one refusal route per route SHAPE - not per pattern TEXT.
    //
    // A family that maps two verbs on one path (GET /x and PUT /x) would otherwise produce two verb-less
    // routes that tie, and the matcher throws at REQUEST time - a 500 in place of the refusal, on the denied
    // route, which is the deny failing in the one way nothing notices until a caller tries it.
    //
    // Text equality is not enough to prevent that. Two patterns differing only in a parameter NAME -
    // /x/{id} and /x/{name} - are different strings and the SAME ROUTE, so a text-keyed dictionary maps both
    // and produces exactly the tie it was meant to prevent. The key is therefore the route SHAPE (see
    // HostedRefusalPattern.ShapeKey), which is what the matcher actually competes on.
    private readonly Dictionary<string, RegisteredRefusal> _refusals = new(StringComparer.Ordinal);

    private sealed record RegisteredRefusal(string SourcePattern, IEndpointConventionBuilder Builder);

    internal HostedDenyGroup(RouteGroupBuilder group, HostedDenial denial)
    {
        _group = group;
        _denial = denial;
    }

    /// <summary>This family's refusal payload, so a test can assert against the same strings that are served.</summary>
    public HostedDenial Denial => _denial;

    public IEndpointConventionBuilder MapGet(string pattern, Delegate handler)
        => Map(pattern, handler, () => _group.MapGet(pattern, handler));

    public IEndpointConventionBuilder MapPost(string pattern, Delegate handler)
        => Map(pattern, handler, () => _group.MapPost(pattern, handler));

    public IEndpointConventionBuilder MapPut(string pattern, Delegate handler)
        => Map(pattern, handler, () => _group.MapPut(pattern, handler));

    public IEndpointConventionBuilder MapDelete(string pattern, Delegate handler)
        => Map(pattern, handler, () => _group.MapDelete(pattern, handler));

    public IEndpointConventionBuilder MapMethods(string pattern, IEnumerable<string> methods, Delegate handler)
        => Map(pattern, handler, () => _group.MapMethods(pattern, methods, handler));

    /// <summary>
    /// The one decision, in one place: on hosted map the refusal and never the handler; off hosted map the
    /// handler and never a refusal.
    /// </summary>
    private IEndpointConventionBuilder Map(string pattern, Delegate handler, Func<IEndpointConventionBuilder> mapHandler)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(handler);

        if (!GatewayHostedMode.IsHosted)
            return mapHandler();

        // The refusal is mapped on the family's pattern with its parameter POLICIES removed, rebuilt from the
        // parsed route MODEL rather than by editing the pattern text (see HostedRefusalPattern). Keeping a
        // policy would leave a measured hole: a segment that fails an inline constraint fails endpoint
        // SELECTION, so a refusal carrying that same constraint is never selected either and the framework
        // answers instead of the refusal. Everything else - literals, separators, parameter names, optionality,
        // catch-alls and defaults - is preserved exactly.
        //
        // A pattern containing a part the normaliser does not recognise THROWS here, at startup. That is
        // deliberate: passing it through would map a refusal that still carried a constraint while the
        // family's author believed the route was covered, which is the false coverage this boundary exists to
        // remove.
        var refusalPattern = HostedRefusalPattern.WithoutPolicies(pattern, _denial.Family);
        var shapeKey = HostedRefusalPattern.ShapeKey(refusalPattern);

        // Same route shape, already refused: a family mapping several verbs on one path needs exactly ONE
        // verb-less refusal, and mapping a second would tie with the first.
        if (_refusals.TryGetValue(shapeKey, out var existing))
            return existing.Builder;

        // Verb-less and handler-less: nothing constrains the match and nothing binds, so every request shape -
        // including a verb this family never mapped - meets the refusal below.
        var refusal = _group.Map(refusalPattern, context => WriteRefusalAsync(context, _denial));
        refusal.WithMetadata(new HostedRefusalMarker(_denial, pattern));

        _refusals[shapeKey] = new RegisteredRefusal(pattern, refusal);
        return refusal;
    }

    private static async Task WriteRefusalAsync(HttpContext context, HostedDenial denial)
    {
        FileLog.Write($"[HostedRouteDeny] DENIED on hosted: family={denial.Family} " +
                      $"method={context.Request.Method} path={context.Request.Path} reason={denial.Reason}");

        context.Response.StatusCode = denial.StatusCode;

        // The media type is set EXPLICITLY, with its charset parameter, because the proof asserts the whole
        // header value and not just the type: a refusal is a contract about what is served, and "close enough"
        // on a content type is how a caller ends up parsing something other than what it expected.
        context.Response.ContentType = "application/json; charset=utf-8";

        // A HEAD request is answered with the refusal's status and headers and no body - the framework
        // suppresses the body for HEAD. That is correct HTTP and it is stated here so the proof can assert it
        // deliberately rather than a reader assuming "every request shape" includes a body on HEAD.
        await context.Response.WriteAsJsonAsync(new HostedRefusalBody(denial.Message));
    }

    /// <summary>The refusal body: one property, so the assertion can be an exact property set.</summary>
    private sealed record HostedRefusalBody(string Error);
}
