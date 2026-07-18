using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Governance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The governance spend surface (issue #1771, spine item 3) - honest, driver-normalized spend.
///
/// Per-session token effort + billing-mode label (metered dollars stay null until the #1608 rate card):
///   POST  /gateway/governance/session-spend           body RecordSessionSpendRequest -> 200 SessionSpendDto | 400
///   GET   /gateway/governance/session-spend           ?agentKind=&amp;billingMode=&amp;since=&amp;until=&amp;limit=
///   GET   /gateway/governance/session-spend/{id}      -> SessionSpendDto | 404
///   GET   /gateway/governance/session-spend/coverage  ?since=&amp;until= -> SpendCoverageDto
///
/// Account-level hosted-AI service dollars from the credit-debit ledger (read-only here; the mirroring is a
/// Gateway-internal snapshot, never an external write):
///   GET   /gateway/governance/hosted-ai-spend         ?since=&amp;until=&amp;limit=
///   GET   /gateway/governance/hosted-ai-spend/summary ?since=&amp;until= -> AccountHostedAiSpendSummaryDto
///
/// Inherits the host-wide token middleware, like every other Gateway route.
/// </summary>
internal static class GovernanceSpendEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void Map(IEndpointRouteBuilder app, SessionSpendStore sessionSpend, AccountHostedAiSpendStore hostedAiSpend)
    {
        app.MapPost("/gateway/governance/session-spend", async (HttpContext ctx) =>
        {
            RecordSessionSpendRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<RecordSessionSpendRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[GovernanceSpendEndpoints] POST bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            if (req is null)
                return Results.BadRequest(new { error = "a spend body is required" });

            return Guard(() => Results.Json(sessionSpend.Record(req)));
        });

        app.MapGet("/gateway/governance/session-spend",
            (string? agentKind, string? billingMode, DateTime? since, DateTime? until, int? limit) =>
                Results.Json(new
                {
                    sessions = sessionSpend.List(agentKind, billingMode, since, until, limit ?? 500),
                }));

        app.MapGet("/gateway/governance/session-spend/coverage",
            (DateTime? since, DateTime? until) => Results.Json(sessionSpend.Coverage(since, until)));

        app.MapGet("/gateway/governance/session-spend/{sessionId}", (string sessionId) =>
        {
            var dto = sessionSpend.Get(sessionId);
            return dto is null
                ? Results.Json(new { error = $"no spend for session '{sessionId}'" },
                    statusCode: StatusCodes.Status404NotFound)
                : Results.Json(dto);
        });

        app.MapGet("/gateway/governance/hosted-ai-spend",
            (DateTime? since, DateTime? until, int? limit) =>
                Results.Json(new { debits = hostedAiSpend.List(since, until, limit ?? 500) }));

        app.MapGet("/gateway/governance/hosted-ai-spend/summary", (DateTime? since, DateTime? until) =>
        {
            // A summary needs a bounded window; default to the trailing 7 days when the caller omits it.
            var untilUtc = until ?? DateTime.UtcNow;
            var sinceUtc = since ?? untilUtc.AddDays(-7);
            return Results.Json(hostedAiSpend.SumOverWindow(sinceUtc, untilUtc));
        });

        FileLog.Write("[GovernanceSpendEndpoints] mapped /gateway/governance/session-spend + hosted-ai-spend routes");
    }

    private static IResult Guard(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (GovernanceValidationException ex)
        {
            FileLog.Write($"[GovernanceSpendEndpoints] rejected: {ex.Message}");
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
