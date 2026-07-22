using CcDirector.Core.Utilities;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Gateway's turn-brief surface (issues #185/#187) - THE brief API now that the
/// Director-side pipeline is deleted:
///   GET  /sessions/{sid}/turnbriefs          - all stored briefs, newest first
///   GET  /sessions/{sid}/turnbriefs/latest   - the most recent brief (404 when none)
///   POST /sessions/{sid}/turnbriefs/feedback - vote/reason feedback (#207), stored as a
///                                              replayable labeled example
///   GET  /turnbriefs/feedback                 - recent feedback corpus records
///   POST /sessions/{sid}/explain              - "I am lost - explain" deep dive (#217);
///                                              DISABLED - answers 503, see below
///   GET  /sessions/{sid}/explain/latest       - the newest explain report (404 when none)
/// Serves the GATEWAY's append-only store; never proxies to a Director. Consumers render
/// the stored briefs verbatim - interpretation happened once, in the warm brain.
///
/// THE DEEP DIVE IS OFF AT THE COMPOSITION ROOT. The Gateway host maps this surface with
/// <c>requestExplainAsync: null</c>, so POST /sessions/{sid}/explain answers 503 "briefing pipeline
/// disabled" and never runs. The host also supplies a <c>briefingStateFor</c> that can return only
/// "Briefed" or "None". This header used to advertise <c>202 + state "Explaining" while it runs</c>:
/// no caller has ever received that, because neither the state nor the 202 is producible. The roster's
/// matching orange rule has been deleted for the same reason - see the tombstone in
/// <c>SessionOrdering.EffectiveColor</c>. Re-enabling the deep dive is a feature, not a repair.
///
/// DENIED IN WHOLE ON HOSTED (MTR audit gap H5). Every route in this file is refused on the hosted
/// Gateway. The store behind them (<see cref="GatewayTurnBriefStore"/>) addresses briefs, explain
/// reports, packages and feedback by BARE session id under one shared directory, with no tenant in any
/// path, file name, or record. On a hosted box those legacy files hold whatever any account produced
/// while the writer still existed, mixed together with nothing to tell them apart - and none of these
/// read routes resolves the request tenant or proves the requested session belongs to it, so tenant A
/// calling <c>/sessions/{S}/turnbriefs</c>, <c>/latest</c>, <c>/explain/latest</c>, or listing
/// <c>/turnbriefs/feedback</c> receives tenant B's material. <c>POST .../feedback</c> is worse: it takes
/// a caller-supplied feedback id, turns it straight into the shared filename, and overwrites an existing
/// record's vote/reason without proving ownership.
///
/// IT IS A DENY, NOT A PARTITION, because there is nothing left to partition INTO. Issue #549 retired the
/// only writer (GatewayTurnBriefAgent), so no new brief is ever produced - the store is read-only-serving
/// legacy data. Records written with no tenant on them cannot be attributed after the fact, so a
/// per-tenant answer would have to be INVENTED rather than read; that is a half-partition, which reads
/// like isolation while being a guess. The honest move is to refuse and quarantine, exactly as the
/// transcription-analysis deny (#1897) and the recording deny did for their untenanted shared stores.
///
/// IT REFUSES rather than returning an empty result: an empty brief list is a FALSE statement (it says
/// "no brief was ever produced for this session", which need not be true), whereas a refusal is merely an
/// absent answer. The two internal readers of this same store - the Interrupted-list rail-line enrichment
/// and the restore continuation-prompt history - are separately quarantined on hosted at their wiring in
/// <c>GatewayHost</c> (they return nothing on hosted), so no foreign brief text is embedded into a
/// tenant's Interrupted list or a new continuation prompt.
///
/// HOW THE DENY IS EXPRESSED - THE SHARED REFUSAL PRIMITIVE. This group is denied through
/// <see cref="HostedRouteDeny.Group"/> (PER-ROUTE), the ONE hosted-refusal boundary every deny family on
/// this Gateway adopts (reference adoption: the transcription-analysis deny in
/// <see cref="TranscriptionAnalysisEndpoint"/>). Per-route, NOT <see cref="HostedRouteDeny.ExclusiveGroup"/>,
/// because these paths sit under <c>/sessions/{sid}/...</c> and <c>/turnbriefs</c> - prefixes that carry
/// many LIVE routes which must keep serving on hosted, so an exclusive catch-all would take them off the
/// air. On hosted the six handlers are NEVER MAPPED; in their place a verb-less refusal is mapped on each
/// route shape, so EVERY request shape (a valid request, a wrong media type, a verb the group never
/// mapped, and a route added LATER through the returned handle) meets the refusal rather than being
/// answered by the framework ahead of it. Off hosted the primitive maps the six real handlers exactly as
/// an unguarded builder would and creates no refusal at all, so self-host - one tenant, whose shared store
/// holds only the owner's own briefs - is byte-identical to before.
/// </summary>
internal static class TurnBriefGatewayEndpoints
{
    /// <summary>The single error string the hosted refusal serves. Held here so a test can assert against
    /// the exact string that is served rather than a copy that could drift.</summary>
    internal const string RefusalMessage = "turn briefs are not available on the hosted gateway";

    /// <summary>
    /// The hosted refusal payload for the whole turn-brief group (MTR gap H5). Validated on construction,
    /// so a blank field fails the Gateway at startup rather than serving a refusal a caller cannot act on.
    /// 404 rather than 403: on hosted this surface does not exist as a concept - there is no per-tenant
    /// brief store for it to read - so "not here" is the truthful answer; 403 would imply the right
    /// credential could reach it, and none can.
    /// </summary>
    private static HostedDenial Denial() => new(
        family: "turn-briefs",
        message: RefusalMessage,
        reason: "the turn-brief store addresses briefs, explain reports, packages and feedback by bare " +
                "session id under one shared directory with no tenant in any path, file name, or record, so " +
                "on a hosted box its legacy files hold what every account produced with nothing to tell them " +
                "apart - and no read route resolves the request tenant or proves session ownership, while the " +
                "feedback POST overwrites a caller-named record without proving ownership",
        unDenyInstruction: "do NOT simply remove this deny. The writer was already retired in #549 so nothing " +
                "new accumulates, but a shared, untenanted legacy store (gateway-turnbriefs briefs/explain/" +
                "packages plus the BriefFeedback corpus) predates it - so tenant-partition those stores, purge " +
                "or quarantine the pre-existing shared data (records written with no tenant cannot be " +
                "attributed afterwards - the choice is deletion or quarantine, never a later migration), and " +
                "only then restore a tenant-scoped read that also proves session ownership; the two internal " +
                "readers gated in GatewayHost (interruptedBriefFor / briefHistoryFor) must be un-gated in the " +
                "same pass",
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Maps the turn-brief routes and RETURNS the denied group they were mapped through, so the refusal can
    /// be proved to cover routes that do not exist yet: a test maps a NEW probe route onto the returned
    /// handle and finds it already refused on hosted with no deny written for it. Returning the handle is
    /// the only way to state that property from outside this file.
    /// </summary>
    public static HostedDenyGroup Map(
        IEndpointRouteBuilder app,
        GatewayTurnBriefStore store,
        Func<string, string> briefingStateFor,
        Func<string, Task<(bool Ok, string Error)>>? requestExplainAsync = null)
    {
        FileLog.Write($"[TurnBriefGatewayEndpoints] mapping the turn-brief surface; hosted={GatewayHostedMode.IsHosted} - on hosted EVERY route in this group is refused via the shared refusal primitive (MTR gap H5)");

        // The whole group through ONE primitive-created handle, rather than a guard line repeated in every
        // handler. The empty prefix keeps every route path written out in full (the routes span two prefixes,
        // /sessions and /turnbriefs), so the self-host surface is byte-identical to before.
        var group = HostedRouteDeny.Group(app, "", Denial());
        MapRoutes(group, store, briefingStateFor, requestExplainAsync);
        return group;
    }

    /// <summary>
    /// The six turn-brief routes. Takes the denied GROUP HANDLE and nothing else: the ungrouped route
    /// builder is deliberately out of scope here so no route can be mapped around the hosted refusal.
    /// </summary>
    private static void MapRoutes(
        HostedDenyGroup app,
        GatewayTurnBriefStore store,
        Func<string, string> briefingStateFor,
        Func<string, Task<(bool Ok, string Error)>>? requestExplainAsync)
    {
        app.MapGet("/sessions/{sid}/turnbriefs", (string sid) =>
        {
            if (!Guid.TryParse(sid, out _))
                return Results.BadRequest(new { error = "invalid session id format" });

            return Results.Json(new TurnBriefsResponse
            {
                SessionId = sid,
                BriefingState = briefingStateFor(sid),
                Items = store.List(sid),
            });
        });

        app.MapGet("/sessions/{sid}/turnbriefs/latest", (string sid) =>
        {
            if (!Guid.TryParse(sid, out _))
                return Results.BadRequest(new { error = "invalid session id format" });

            var latest = store.Latest(sid);
            if (latest is null)
                return Results.NotFound(new { error = "no brief yet", briefingState = briefingStateFor(sid) });

            return Results.Json(latest);
        });

        app.MapPost("/sessions/{sid}/turnbriefs/feedback", async (string sid, HttpContext ctx) =>
        {
            if (!Guid.TryParse(sid, out _))
                return Results.BadRequest(new { error = "invalid session id format" });

            var req = await ctx.Request.ReadFromJsonAsync<TurnBriefFeedbackRequest>(ctx.RequestAborted);
            if (req is null)
                return Results.BadRequest(new { error = "feedback body is required" });

            var vote = string.IsNullOrWhiteSpace(req.Vote) ? "down" : req.Vote.Trim().ToLowerInvariant();
            if (vote is not ("down" or "up" or "thumbs_down" or "thumbs_up" or "negative" or "positive"))
                return Results.BadRequest(new { error = "vote must be 'down' or 'up'" });

            var briefs = store.List(sid);
            var brief = req.TurnNumber > 0
                ? briefs.FirstOrDefault(b => b.TurnNumber == req.TurnNumber)
                : briefs.FirstOrDefault();
            if (brief is null)
                return Results.NotFound(new { error = "no such brief" });

            var result = store.SaveFeedback(sid, brief, vote, req.Note, req.FeedbackId);
            return Results.Json(result);
        });

        app.MapGet("/turnbriefs/feedback", (int? count) =>
        {
            var take = count.GetValueOrDefault(50);
            return Results.Json(new TurnBriefFeedbackListResponse { Items = store.ListFeedback(take) });
        });

        app.MapPost("/sessions/{sid}/explain", async (string sid) =>
        {
            if (!Guid.TryParse(sid, out _))
                return Results.BadRequest(new { error = "invalid session id format" });
            if (requestExplainAsync is null)
                return Results.Json(new { error = "briefing pipeline disabled (CC_TURNBRIEFS=0)" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var (ok, error) = await requestExplainAsync(sid);
            if (!ok)
                return Results.NotFound(new { error });

            return Results.Json(
                new ExplainAcceptedResponse { Accepted = true, State = briefingStateFor(sid) },
                statusCode: StatusCodes.Status202Accepted);
        });

        app.MapGet("/sessions/{sid}/explain/latest", (string sid) =>
        {
            if (!Guid.TryParse(sid, out _))
                return Results.BadRequest(new { error = "invalid session id format" });

            var latest = store.LatestExplain(sid);
            if (latest is null)
                return Results.NotFound(new { error = "no explain report yet", briefingState = briefingStateFor(sid) });

            return Results.Json(latest);
        });
    }
}
