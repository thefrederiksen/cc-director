using CcDirector.Core.Utilities;
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
/// Self-host is COMPLETELY unchanged, and that is the control. Self-host has exactly one tenant, so the
/// shared log holds only the owner's own speech and these routes are exactly as correct as they ever were.
/// </summary>
internal static class TranscriptionAnalysisEndpoint
{
    private const int DefaultTurnLimit = 100;
    private const int MaxTurnLimit = 2000;
    private const int DefaultTermTop = 25;
    private const int DefaultWordTop = 50;

    /// <summary>
    /// The hosted refusal for the whole transcription-analysis group (issue #1897), or null on self-host
    /// where nothing changes.
    ///
    /// Gated on <see cref="GatewayHostedMode.IsHosted"/> - the INDEPENDENT deployment signal - and NOT on a
    /// boundary or tenant argument being passed in. A security branch that depends on an optional argument
    /// fails OPEN when a caller omits it, which is exactly how the hosted account-status fix nearly shipped
    /// a hole: omit the argument and a hosted Gateway silently takes the self-host path. Asking hosted mode
    /// directly means this group cannot serve the shared speech log on hosted however the host is wired.
    ///
    /// 404 rather than 403: on hosted this analysis surface does not exist as a concept - there is no
    /// per-tenant log for it to read - so "not here" is the truthful answer. 403 would imply the right
    /// credential could reach it, and none can.
    /// </summary>
    private static IResult? DenyOnHosted()
    {
        if (!GatewayHostedMode.IsHosted) return null;

        FileLog.Write("[TranscriptionAnalysisEndpoint] DENIED on hosted: the transcription telemetry log is one shared file with no tenant in it, so there is no per-tenant answer to serve");
        return Results.Json(
            new { error = "transcription analysis is not available on the hosted gateway" },
            statusCode: StatusCodes.Status404NotFound);
    }

    /// Returns the guarded route group. That return value exists SOLELY so a test can map a brand-new
    /// route onto the same group and prove it is refused on hosted with no deny written for it - the
    /// property that distinguishes a group filter from a per-route guard, and which is otherwise
    /// invisible to any test that only drives the routes existing today.
    public static RouteGroupBuilder Map(IEndpointRouteBuilder outer, TranscriptionTelemetryReader? reader = null)
    {
        var log = reader ?? new TranscriptionTelemetryReader();

        FileLog.Write($"[TranscriptionAnalysisEndpoint] mapping /transcription analysis; hosted={GatewayHostedMode.IsHosted} - on hosted EVERY route in this group is refused (issue #1897)");

        // The whole group behind ONE filter, rather than a guard line repeated in every handler.
        // A repeated guard is a thing to forget: the route added next year would be open by default and
        // nobody would notice. A group filter runs before EVERY route mapped below, including routes that
        // do not exist yet, so the refusal cannot rot as the group grows. The empty prefix keeps the route
        // paths written out in full, exactly as before, so the self-host surface is byte-identical.
        var app = outer.MapGroup("");
        app.AddEndpointFilter(async (ctx, next) =>
        {
            if (DenyOnHosted() is { } denied) return denied;
            return await next(ctx);
        });

        app.MapGet("/transcription/stats", (HttpContext ctx) =>
        {
            var since = ResolveSince(ctx);
            FileLog.Write($"[TranscriptionAnalysisEndpoint] GET /transcription/stats since={since:o}");
            return Results.Json(log.ComputeStats(since));
        });

        app.MapGet("/transcription/turns", (HttpContext ctx) =>
        {
            var since = ResolveSince(ctx);
            var limit = ClampInt(ctx.Request.Query["limit"], DefaultTurnLimit, 0, MaxTurnLimit);
            FileLog.Write($"[TranscriptionAnalysisEndpoint] GET /transcription/turns since={since:o} limit={limit}");
            return Results.Json(new { turns = log.Load(since, limit) });
        });

        app.MapGet("/transcription/terms", (HttpContext ctx) =>
        {
            var since = ResolveSince(ctx);
            var top = ClampInt(ctx.Request.Query["top"], DefaultTermTop, 1, 1000);
            return Results.Json(new { terms = log.TopCorrections(top, since) });
        });

        app.MapGet("/transcription/words", (HttpContext ctx) =>
        {
            var since = ResolveSince(ctx);
            var top = ClampInt(ctx.Request.Query["top"], DefaultWordTop, 1, 5000);
            return Results.Json(new { words = log.TopWords(top, since) });
        });

        return app;
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
