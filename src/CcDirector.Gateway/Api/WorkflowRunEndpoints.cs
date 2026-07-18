using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The workflow-run surface (Workflows mission, phase 4 - the governance outcome spine, issue #1771).
/// A run is one execution of a workflow definition, pinned to the exact published version that
/// governed it. Lifecycle and acceptance are separate fields on purpose.
///
///   POST  /gateway/workflow-runs        body NewWorkflowRunRequest -> 201 WorkflowRunDto | 400
///   GET   /gateway/workflow-runs        ?workflowId=&amp;status=&amp;missionId= -> { runs: [...] }
///   GET   /gateway/workflow-runs/{id}   -> WorkflowRunDto | 404
///   PATCH /gateway/workflow-runs/{id}   body PatchWorkflowRunRequest -> 200 | 400 | 404
///
/// Inherits the host-wide token middleware, like every other Gateway route.
/// </summary>
internal static class WorkflowRunEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static void Map(IEndpointRouteBuilder app, WorkflowRunStore store)
    {
        app.MapPost("/gateway/workflow-runs", async (HttpContext ctx) =>
        {
            NewWorkflowRunRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<NewWorkflowRunRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[WorkflowRunEndpoints] POST bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            if (req is null)
                return Results.BadRequest(new { error = "a run body is required" });

            return Guard(() =>
            {
                var run = store.Create(req.WorkflowId ?? "", req.Name ?? "", req.MissionId,
                    req.ParentRunId, req.RepoPath);
                FileLog.Write($"[WorkflowRunEndpoints] created run {run.Id} of {run.WorkflowId} v{run.WorkflowVersion}");
                return Results.Json(run, statusCode: StatusCodes.Status201Created);
            });
        });

        app.MapGet("/gateway/workflow-runs", (string? workflowId, string? status, Guid? missionId, int? limit) =>
            Results.Json(new { runs = store.List(workflowId, status, missionId, limit ?? 200) }));

        app.MapGet("/gateway/workflow-runs/{id:guid}", (Guid id) =>
        {
            var run = store.Get(id);
            return run is null
                ? Results.Json(new { error = $"no workflow run '{id}'" },
                    statusCode: StatusCodes.Status404NotFound)
                : Results.Json(run);
        });

        app.MapPatch("/gateway/workflow-runs/{id:guid}", async (Guid id, HttpContext ctx) =>
        {
            PatchWorkflowRunRequest? patch;
            try
            {
                patch = await JsonSerializer.DeserializeAsync<PatchWorkflowRunRequest>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[WorkflowRunEndpoints] PATCH bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
            if (patch is null)
                return Results.BadRequest(new { error = "a patch body is required" });

            return Guard(() =>
            {
                var run = store.Patch(id, patch);
                return run is null
                    ? Results.Json(new { error = $"no workflow run '{id}'" },
                        statusCode: StatusCodes.Status404NotFound)
                    : Results.Json(run);
            });
        });

        FileLog.Write("[WorkflowRunEndpoints] mapped /gateway/workflow-runs routes");
    }

    private static IResult Guard(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (WorkflowValidationException ex)
        {
            FileLog.Write($"[WorkflowRunEndpoints] rejected: {ex.Message}");
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
