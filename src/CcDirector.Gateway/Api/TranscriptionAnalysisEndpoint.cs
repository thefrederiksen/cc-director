using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Transcription;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Transcription Health over minimized, bounded local history.
///
///   GET    /transcription/stats
///   GET    /transcription/turns
///   GET    /transcription/terms
///   DELETE /transcription/history
///
/// The history never includes transcript text or provider error bodies, is retained for 30 days, and
/// can be cleared by the owner. This feature is self-host only because its file store has no tenant
/// partition on a shared hosted Gateway.
/// </summary>
internal static class TranscriptionAnalysisEndpoint
{
    private const int DefaultTurnLimit = 100;
    private const int MaxTurnLimit = 2000;
    private const int DefaultTermTop = 25;

    internal const string Prefix = "/transcription";
    internal const string RefusalMessage = "transcription analysis is not available on the hosted gateway";

    private static HostedDenial Denial() => new(
        family: "transcription-analysis",
        message: RefusalMessage,
        reason: "the local transcription history is process-global and has no tenant partition",
        unDenyInstruction: "tenant-partition the history writer and reader before enabling this feature on hosted",
        statusCode: StatusCodes.Status404NotFound);

    public static HostedDenyGroup Map(
        IEndpointRouteBuilder outer,
        TranscriptionHistoryReader? reader = null,
        TranscriptionAudioArchive? audioArchive = null)
    {
        var history = reader ?? new TranscriptionHistoryReader();
        var audio = audioArchive ?? new TranscriptionAudioArchive();
        FileLog.Write($"[TranscriptionAnalysisEndpoint] mapping {Prefix} history; hosted={GatewayHostedMode.IsHosted}");
        var group = HostedRouteDeny.Group(outer, Prefix, Denial());
        MapRoutes(group, history, audio);
        return group;
    }

    private static void MapRoutes(
        HostedDenyGroup app,
        TranscriptionHistoryReader history,
        TranscriptionAudioArchive audioArchive)
    {
        app.MapGet("/stats", (HttpContext ctx) =>
            Results.Json(history.ComputeStats(ResolveSince(ctx))));

        app.MapGet("/turns", (HttpContext ctx) =>
        {
            var limit = ClampInt(ctx.Request.Query["limit"], DefaultTurnLimit, 0, MaxTurnLimit);
            return Results.Json(new { turns = history.Load(ResolveSince(ctx), limit) });
        });

        app.MapGet("/terms", (HttpContext ctx) =>
        {
            var top = ClampInt(ctx.Request.Query["top"], DefaultTermTop, 1, 1000);
            return Results.Json(new { terms = history.TopCorrections(top, ResolveSince(ctx)) });
        });

        app.MapDelete("/history", () => Results.Json(new
        {
            removedFiles = history.Clear(),
            removedAudioClips = audioArchive.Clear(),
        }));
    }

    private static DateTime? ResolveSince(HttpContext ctx)
    {
        var daysRaw = ctx.Request.Query["days"].ToString();
        if (!string.IsNullOrWhiteSpace(daysRaw) && double.TryParse(daysRaw, out var days) && days > 0)
            return DateTime.UtcNow.AddDays(-days);

        var sinceRaw = ctx.Request.Query["since"].ToString();
        if (!string.IsNullOrWhiteSpace(sinceRaw)
            && DateTime.TryParse(sinceRaw, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal |
                System.Globalization.DateTimeStyles.AssumeUniversal, out var since))
            return since;
        return null;
    }

    private static int ClampInt(string? raw, int fallback, int min, int max)
    {
        if (!int.TryParse(raw, out var value)) value = fallback;
        return Math.Clamp(value, min, max);
    }
}
