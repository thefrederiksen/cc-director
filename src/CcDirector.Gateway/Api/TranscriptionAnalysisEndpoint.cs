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
/// </summary>
internal static class TranscriptionAnalysisEndpoint
{
    private const int DefaultTurnLimit = 100;
    private const int MaxTurnLimit = 2000;
    private const int DefaultTermTop = 25;
    private const int DefaultWordTop = 50;

    public static void Map(IEndpointRouteBuilder app, TranscriptionTelemetryReader? reader = null)
    {
        var log = reader ?? new TranscriptionTelemetryReader();

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
