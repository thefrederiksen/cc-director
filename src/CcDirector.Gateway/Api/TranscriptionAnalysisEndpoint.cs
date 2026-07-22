using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Transcription;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Read-only analysis API over the LOCAL transcription telemetry log (issue #839 made the Gateway the
/// one owner of transcription, so it is the one place that can answer these). Any agent can query the
/// Gateway to learn how fast and how good transcription is - latency percentiles, cleanup behaviour,
/// most-corrected terms, word frequencies - entirely from data on this machine.
///
///   GET /transcription/stats   [?days=N | ?since=ISO]            aggregate summary
///   GET /transcription/turns   [?days=N | ?since=ISO] [?limit=N] raw recorded turns, newest first
///   GET /transcription/terms   [?days=N | ?since=ISO] [?top=N]   most frequent find -> replace corrections
///   GET /transcription/words   [?days=N | ?since=ISO] [?top=N]   most frequent spoken words
///
/// <c>days</c> takes precedence over <c>since</c>; with neither, the whole log is used. Inherits the
/// host-wide token middleware like every other Gateway route.
///
/// DENIED IN WHOLE ON HOSTED (issue #1897). Every route in this file is refused on the hosted Gateway.
/// All four read the SAME transcription telemetry log, which is one daily file in one shared directory
/// with no tenant in its path, its file name, or its records. On a hosted box that log holds what every
/// account on the machine said out loud, mixed together with nothing to tell them apart.
///
/// GET /transcription/turns is the sharpest of the four: it returns up to 2000 records including the
/// full <c>rawText</c> and <c>cleanedText</c> of each turn, and it needs NO identifier of any kind - one
/// request with any valid device key returns everybody's speech. The other three are aggregates computed
/// over exactly the same unpartitioned records, so serving them would disclose the same content in
/// summary form: /words is literally a frequency table of the words other accounts spoke.
///
/// It is a DENY OF THE WHOLE GROUP rather than a guard on the one obviously-bad route, because the four
/// share one store and one shape - a fix aimed only at /turns would leave /words handing back the same
/// speech a word at a time - and because a route-by-route guard rots: the next analysis route added to
/// this file would be open again by default.
///
/// It is a deny rather than a per-tenant partition because the records were never written with a tenant.
/// The tenant is not missing from the query, it is missing from the DATA, so there is nothing to filter
/// by; inventing an attribution after the fact would be a guess presented as a boundary. Partitioning the
/// store is issue #1897's job, and un-denying is gated on it.
///
/// It REFUSES rather than returning an empty result. An empty stats block is a FALSE statement - it says
/// "no transcription happened", which is not true on a box that is transcribing - whereas a refusal is
/// merely an absent one.
///
/// HOW THE DENY IS EXPRESSED - THE SHARED REFUSAL PRIMITIVE, NOT A BESPOKE FILTER. This group is denied
/// through <see cref="HostedRouteDeny"/>, the ONE hosted-refusal boundary every deny family on this Gateway
/// adopts (reference implementation: the key-vault deny in pull request #1904; primitive at
/// <c>src/CcDirector.Gateway/Tenancy/HostedRouteDeny.cs</c>). An earlier revision rolled its own
/// <c>AddEndpointFilter</c> deny before the primitive existed; it has been replaced so the release ships ONE
/// refusal boundary, not one per family. On hosted the four handlers are NEVER MAPPED - in their place a
/// verb-less refusal is mapped on each of the four route shapes, so EVERY request shape (a valid query, a
/// wrong media type, a verb the group never mapped, and a route added LATER through the same group handle)
/// meets the refusal rather than being answered by the framework ahead of it.
///
/// IT USES <see cref="HostedRouteDeny.Group"/> (PER-ROUTE), NOT <see cref="HostedRouteDeny.ExclusiveGroup"/>,
/// and the reason is structural: the <c>/transcription</c> prefix is NOT owned exclusively by this analysis
/// group. Two LIVE routes serve beneath it and must keep serving on hosted - <c>POST /transcription</c> (the
/// batch transcribe endpoint) and <c>POST /transcription/cleanup</c> - so an exclusive catch-all under
/// <c>/transcription</c> would take those undenied routes off the air, which the startup
/// <see cref="HostedRefusalRouteSpace.ValidateBeforeStart"/> containment check would (correctly) refuse to
/// start over. Per-route mode maps one verb-less refusal on each of the FOUR analysis paths and nothing
/// else, leaving the two live writers untouched. The cost the primitive documents for this mode - a route
/// added to the family gets no refusal unless it is mapped through the group handle - is covered here
/// because every one of the four is mapped through that handle, and the future-route property is proven by a
/// probe route mapped onto the returned handle (see <c>HostedContentDenyGroupFilterTests</c>).
///
/// Self-host is COMPLETELY unchanged, and that is the control. Self-host has exactly one tenant, so the
/// shared log holds only the owner's own speech and these routes are exactly as correct as they ever were:
/// off hosted the primitive maps the four real handlers exactly as an unguarded builder would, with no
/// refusal created at all.
/// </summary>
internal static class TranscriptionAnalysisEndpoint
{
    private const int DefaultTurnLimit = 100;
    private const int MaxTurnLimit = 2000;
    private const int DefaultTermTop = 25;
    private const int DefaultWordTop = 50;

    /// <summary>The prefix this analysis group's routes sit under. NOT claimed exclusively - see the class
    /// note: <c>POST /transcription</c> and <c>POST /transcription/cleanup</c> are live routes beneath it.</summary>
    internal const string Prefix = "/transcription";

    /// <summary>The single error string the hosted refusal serves. Held here so a test can assert against the
    /// exact string that is served rather than a copy that could drift.</summary>
    internal const string RefusalMessage = "transcription analysis is not available on the hosted gateway";

    /// <summary>
    /// The hosted refusal payload for the whole transcription-analysis group (issue #1897). Validated on
    /// construction, so a blank field fails the Gateway at startup rather than serving a refusal a caller
    /// cannot act on. 404 rather than 403: on hosted this analysis surface does not exist as a concept -
    /// there is no per-tenant log for it to read - so "not here" is the truthful answer; 403 would imply the
    /// right credential could reach it, and none can. The refusal is driven off
    /// <see cref="GatewayHostedMode.IsHosted"/> inside the primitive - the INDEPENDENT deployment signal, not
    /// an optional argument a caller can omit and thereby fail OPEN.
    ///
    /// UN-DENY CONDITION - REMOVING THIS DENY REQUIRES ALSO PURGING OR PARTITIONING WHAT ACCUMULATED BEHIND
    /// IT. Two SEPARATE questions, and here the first one fails outright.
    ///
    /// (a) DOES ANYTHING STILL WRITE IT? NO LONGER, on hosted. <c>GatewayTranscriptionService.RecordTelemetry</c>
    /// calls <c>TranscriptionTelemetryLog.Record</c> on EVERY transcription, and that <c>Record</c> is
    /// otherwise UNCONDITIONAL - it has no enabled-check of any kind (the only flag near it is
    /// <c>TextEnabled</c>, which merely decides whether the spoken text is included and defaults to TRUE). An
    /// earlier revision left that writer running and denied the READ only, the same shape as the merged stats
    /// deny #1888. THIS PASS HOST-GATES THE WRITER TOO (defense in depth, deny-by-default, matching the
    /// key-vault deny #1904): <c>RecordTelemetry</c> now NO-OPS on hosted, so no new speech accumulates in the
    /// shared log while the deny stands. That gate is safe because the log's ONLY reader is this now-denied
    /// analysis group - verified nothing billing / usage-metering / quota consumes it, so gating the write
    /// undercounts nothing. #1888's read-only-deny decision predates this defense-in-depth pass; expanding it
    /// here is deliberate.
    ///
    /// (b) WHAT ALREADY EXISTS? A log of every account's speech from BEFORE the writer was gated. Gating the
    /// write is a statement about the future, not evidence about the past. Records written with no tenant on
    /// them cannot be attributed afterwards, so the choice is deletion or quarantine, never a later migration.
    /// Un-denying on a partitioned WRITER alone would expose the contaminated history underneath it.
    /// </summary>
    private static HostedDenial Denial() => new(
        family: "transcription-analysis",
        message: RefusalMessage,
        reason: "the transcription telemetry log is one shared daily file with no tenant in its path, its file " +
                "name, or its records, so on a hosted box it holds what every account said out loud with nothing " +
                "to tell them apart - and /turns returns the raw and cleaned text of up to 2000 of those records " +
                "for one request carrying no identifier at all",
        unDenyInstruction: "do NOT simply remove this deny: the writer is host-gated now (no new accumulation on " +
                "hosted) but a shared, untenanted log predates the gate - so tenant-partition the telemetry log, " +
                "purge or quarantine the pre-existing shared log (records written with no tenant cannot be " +
                "attributed afterwards - the choice is deletion or quarantine, never a later migration), THEN " +
                "un-gate the write, and only then restore a tenant-scoped read",
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Maps the transcription-analysis routes and RETURNS the denied group they were mapped through.
    ///
    /// The routes are mapped through the group HANDLE (<see cref="HostedDenyGroup"/>), never through the
    /// ungrouped builder: the handle is obtainable only from <see cref="HostedRouteDeny"/>, so a route mapped
    /// around the refusal is not expressible in <see cref="MapRoutes"/> without changing its signature - the
    /// bypass count is reduced by design, not by care. On hosted a per-route verb-less refusal replaces each
    /// handler; off hosted the handle maps each handler as an unguarded builder would.
    ///
    /// PER-ROUTE, NOT EXCLUSIVE. See the class note: <c>/transcription</c> carries two live routes, so this
    /// family cannot claim the prefix exclusively. The return value exists so the future-route property is
    /// statable from outside this file: a test maps a brand-new route through the returned handle and shows
    /// it is refused on hosted with no deny written for it.
    /// </summary>
    public static HostedDenyGroup Map(IEndpointRouteBuilder outer, TranscriptionTelemetryReader? reader = null)
    {
        var log = reader ?? new TranscriptionTelemetryReader();

        FileLog.Write($"[TranscriptionAnalysisEndpoint] mapping {Prefix} analysis; hosted={GatewayHostedMode.IsHosted} - on hosted EVERY route in this group is refused via the shared refusal primitive (issue #1897)");

        var group = HostedRouteDeny.Group(outer, Prefix, Denial());
        MapRoutes(group, log);
        return group;
    }

    /// <summary>
    /// The four transcription-analysis routes, mapped relative to the <see cref="Prefix"/> so the full paths
    /// are <c>/transcription/stats</c>, <c>/transcription/turns</c>, <c>/transcription/terms</c> and
    /// <c>/transcription/words</c> exactly as before. Takes the denied GROUP HANDLE and nothing else: the
    /// ungrouped route builder is deliberately out of scope here so no route can be mapped around the hosted
    /// refusal.
    /// </summary>
    private static void MapRoutes(HostedDenyGroup app, TranscriptionTelemetryReader log)
    {
        app.MapGet("/stats", (HttpContext ctx) =>
        {
            var since = ResolveSince(ctx);
            FileLog.Write($"[TranscriptionAnalysisEndpoint] GET /transcription/stats since={since:o}");
            return Results.Json(log.ComputeStats(since));
        });

        app.MapGet("/turns", (HttpContext ctx) =>
        {
            var since = ResolveSince(ctx);
            var limit = ClampInt(ctx.Request.Query["limit"], DefaultTurnLimit, 0, MaxTurnLimit);
            FileLog.Write($"[TranscriptionAnalysisEndpoint] GET /transcription/turns since={since:o} limit={limit}");
            return Results.Json(new { turns = log.Load(since, limit) });
        });

        app.MapGet("/terms", (HttpContext ctx) =>
        {
            var since = ResolveSince(ctx);
            var top = ClampInt(ctx.Request.Query["top"], DefaultTermTop, 1, 1000);
            return Results.Json(new { terms = log.TopCorrections(top, since) });
        });

        app.MapGet("/words", (HttpContext ctx) =>
        {
            var since = ResolveSince(ctx);
            var top = ClampInt(ctx.Request.Query["top"], DefaultWordTop, 1, 5000);
            return Results.Json(new { words = log.TopWords(top, since) });
        });
    }

    /// <summary>Resolve the time window: <c>days</c> (last N days) wins, else <c>since</c> (ISO), else null.</summary>
    private static DateTime? ResolveSince(HttpContext ctx)
    {
        var daysRaw = ctx.Request.Query["days"].ToString();
        if (!string.IsNullOrWhiteSpace(daysRaw) && double.TryParse(daysRaw, out var days) && days > 0)
            return DateTime.UtcNow.AddDays(-days);

        var sinceRaw = ctx.Request.Query["since"].ToString();
        if (!string.IsNullOrWhiteSpace(sinceRaw)
            && DateTime.TryParse(sinceRaw, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var since))
            return since;

        return null;
    }

    private static int ClampInt(string? raw, int fallback, int min, int max)
    {
        if (!int.TryParse(raw, out var v)) v = fallback;
        return Math.Clamp(v, min, max);
    }
}
