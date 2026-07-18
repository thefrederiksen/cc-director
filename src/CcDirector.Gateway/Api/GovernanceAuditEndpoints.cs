using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Governance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The governance audit surface (issue #1771, spine item 4) - structured intervention and permission/sandbox
/// events. Append-only: an audit fact can be recorded and read, never edited or removed, so there is a POST
/// and a GET and deliberately no PUT/PATCH/DELETE.
///
///   POST  /gateway/governance/audit-events        body AppendGovernanceAuditEventRequest       -> 201 | 400
///   POST  /gateway/governance/audit-events/batch    body AppendGovernanceAuditEventsBatchRequest -> 200 { written } | 400
///   GET   /gateway/governance/audit-events          ?sessionId=&amp;runId=&amp;category=&amp;eventType=&amp;since=&amp;until=&amp;limit=
///                                                   -> { events: [...] }
///
/// Inherits the host-wide token middleware, like every other Gateway route.
/// </summary>
internal static class GovernanceAuditEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void Map(IEndpointRouteBuilder app, GovernanceAuditLog log)
    {
        app.MapPost("/gateway/governance/audit-events", async (HttpContext ctx) =>
        {
            AppendGovernanceAuditEventRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<AppendGovernanceAuditEventRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[GovernanceAuditEndpoints] POST bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            if (req is null)
                return Results.BadRequest(new { error = "an audit event body is required" });

            return Guard(() =>
            {
                var recorded = log.Append(req);
                return Results.Json(recorded, statusCode: StatusCodes.Status201Created);
            });
        });

        app.MapPost("/gateway/governance/audit-events/batch", async (HttpContext ctx) =>
        {
            AppendGovernanceAuditEventsBatchRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<AppendGovernanceAuditEventsBatchRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[GovernanceAuditEndpoints] POST batch bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            if (req is null)
                return Results.BadRequest(new { error = "a batch body is required" });

            return Guard(() =>
            {
                var written = log.AppendBatch(req.Events ?? new List<AppendGovernanceAuditEventRequest>());
                return Results.Json(new { written });
            });
        });

        app.MapGet("/gateway/governance/audit-events",
            (string? sessionId, Guid? runId, string? category, string? eventType,
             DateTime? since, DateTime? until, int? limit) =>
                Results.Json(new
                {
                    events = log.List(sessionId, runId, category, eventType, since, until, limit ?? 500),
                }));

        FileLog.Write("[GovernanceAuditEndpoints] mapped /gateway/governance/audit-events routes");
    }

    private static IResult Guard(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (GovernanceValidationException ex)
        {
            FileLog.Write($"[GovernanceAuditEndpoints] rejected: {ex.Message}");
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
