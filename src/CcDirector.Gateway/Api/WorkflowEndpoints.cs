using CcDirector.Core.Utilities;
using CcDirector.Gateway.Workflows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The workflow catalog (issue #1617): the shapes of work this fleet knows how to run.
///
///   GET /gateway/workflows        -> { workflows: [ ... ] }
///   GET /gateway/workflows/{id}   -> { ... } | 404
///
/// The routes sit under /gateway (the same convention as /gateway/ai/*) and NOT at a bare /workflows,
/// because the Cockpit's Workflows PAGE owns the /workflows path. The Gateway serves the single-page
/// app at "/" and falls unknown page paths back to index.html, so an API mapped at /workflows would win
/// that path and a hard navigation to the page would render raw JSON instead of the Cockpit.
///
/// The Gateway is the HOME for workflows. It serves them and every Director asks it, rather than each
/// machine carrying its own private copy of how the team works. That is what makes an organisation-wide
/// rollout possible later: an administrator defines the workflows once on their Gateway and every
/// Director picks them up.
///
/// The catalog is PERSISTED (Workflows mission, phase 1): reads come from the WorkflowStore (EF data
/// layer), where the built-ins are seeded at startup and user-defined workflows will live beside
/// them. The legacy read shape is frozen - fields are only ever ADDED. Authoring routes are the next
/// phase. Inherits the host-wide token middleware, like every other Gateway route.
/// </summary>
internal static class WorkflowEndpoints
{
    public static void Map(IEndpointRouteBuilder app, WorkflowStore store)
    {
        app.MapGet("/gateway/workflows", () =>
        {
            var workflows = store.ListPublished();
            FileLog.Write($"[WorkflowEndpoints] list workflows: count={workflows.Count}");
            return Results.Json(new { workflows });
        });

        app.MapGet("/gateway/workflows/{id}", (string id) =>
        {
            var workflow = store.GetPublished(id);
            if (workflow is null)
            {
                FileLog.Write($"[WorkflowEndpoints] get workflow: id={id}, result=not found");
                return Results.Json(new { error = $"no workflow with id '{id}'" },
                    statusCode: StatusCodes.Status404NotFound);
            }

            FileLog.Write($"[WorkflowEndpoints] get workflow: id={id}, result=found");
            return Results.Json(workflow);
        });
    }
}
