using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Governance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The governance event-ledger surface (issue #1771, spine item 2). The ledger is append-only: a
/// transition can be recorded and read, never edited or removed - so there is a POST and a GET, and
/// deliberately no PUT/PATCH/DELETE.
///
///   POST  /gateway/governance/events         body AppendGovernanceEventRequest       -> 201 GovernanceEventDto | 400
///   POST  /gateway/governance/events/batch    body AppendGovernanceEventsBatchRequest -> 200 { written } | 400
///   GET   /gateway/governance/events          ?sessionId=&amp;runId=&amp;subjectKind=&amp;state=&amp;since=&amp;until=&amp;limit=
///                                             -> { events: [...] }
///
/// Inherits the host-wide token middleware, like every other Gateway route.
/// </summary>
internal static class GovernanceEventEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void Map(IEndpointRouteBuilder app, GovernanceEventLedger ledger)
    {
        app.MapPost("/gateway/governance/events", async (HttpContext ctx) =>
        {
            AppendGovernanceEventRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<AppendGovernanceEventRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[GovernanceEventEndpoints] POST bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            if (req is null)
                return Results.BadRequest(new { error = "an event body is required" });

            return Guard(() =>
            {
                var recorded = ledger.Append(req);
                return Results.Json(recorded, statusCode: StatusCodes.Status201Created);
            });
        });

        app.MapPost("/gateway/governance/events/batch", async (HttpContext ctx) =>
        {
            AppendGovernanceEventsBatchRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<AppendGovernanceEventsBatchRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[GovernanceEventEndpoints] POST batch bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            if (req is null)
                return Results.BadRequest(new { error = "a batch body is required" });

            return Guard(() =>
            {
                var written = ledger.AppendBatch(req.Events ?? new List<AppendGovernanceEventRequest>());
                return Results.Json(new { written });
            });
        });

        app.MapGet("/gateway/governance/events",
            (string? sessionId, Guid? runId, string? subjectKind, string? state,
             DateTime? since, DateTime? until, int? limit) =>
                Results.Json(new
                {
                    events = ledger.List(sessionId, runId, subjectKind, state, since, until, limit ?? 500),
                }));

        FileLog.Write("[GovernanceEventEndpoints] mapped /gateway/governance/events routes");
    }

    private static IResult Guard(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (GovernanceValidationException ex)
        {
            FileLog.Write($"[GovernanceEventEndpoints] rejected: {ex.Message}");
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
