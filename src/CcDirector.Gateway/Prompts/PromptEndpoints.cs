using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Prompts;

/// <summary>
/// The Gateway's prompt-log front door (issue #1551).
///
/// POST /prompts - a Director pushes what it captured. The Director keeps no copy; this is the single
/// copy, which is why the write is acknowledged with a real count rather than fire-and-forget.
///
/// GET /prompts  - anyone asking for history asks here. That is the point of the log living on the
/// Gateway: it already has the whole fleet's record, so nothing has to go hunting across machines.
/// </summary>
public static class PromptEndpoints
{
    public static void Map(IEndpointRouteBuilder app, GatewayPromptLog? log = null)
    {
        var store = log ?? GatewayPromptLog.Shared;

        app.MapPost("/prompts", (PromptIngestRequest? request) =>
        {
            if (request?.Records is null || request.Records.Count == 0)
                return Results.BadRequest(new { error = "records is required and must not be empty" });

            var written = store.Append(request.Records);
            FileLog.Write($"[PromptEndpoints] POST /prompts: received {request.Records.Count}, wrote {written}");
            return Results.Ok(new PromptIngestResponse { Written = written });
        });

        app.MapGet("/prompts", (string? from, string? to) =>
        {
            // Default to today so a bare GET /prompts is useful rather than an error.
            var fromUtc = ParseDay(from) ?? DateTime.UtcNow.Date;
            var toUtc = ParseDay(to) ?? DateTime.UtcNow.Date;
            if (toUtc < fromUtc)
                return Results.BadRequest(new { error = "'to' is earlier than 'from'" });

            var records = store.Read(fromUtc, toUtc);
            return Results.Ok(new { count = records.Count, records });
        });
    }

    /// <summary>Parse a yyyy-MM-dd day, or null when absent/unparseable.</summary>
    private static DateTime? ParseDay(string? value)
        => DateTime.TryParse(value, null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.Date
            : null;
}
