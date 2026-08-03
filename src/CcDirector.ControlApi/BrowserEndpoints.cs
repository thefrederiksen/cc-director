using System.Text.Json.Nodes;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Loopback Control-API surface for DevThrottle's automation browsers (the drivable, signed-in-once
/// Chromium instances an agent attaches to via browser-harness).
///
/// Remove-the-network-port mission, phase 2: THIS IS NOW A THIN ADAPTER. The behaviour lives once, in
/// <see cref="BrowserExecutor"/>, which is also what the Gateway reaches over the tunnel at
/// <c>/directors/{id}/browsers/...</c>. These loopback routes remain only so command lines installed
/// BEFORE this phase keep working while the change is in flight; they are switched off with the rest of the
/// agent surface (<see cref="ControlApiHost"/>'s agent-routes switch) and deleted outright in phase 5. Every
/// route below therefore does exactly two things - build the payload the verb expects, and translate the
/// verb's typed result into the HTTP answer the old caller reads.
///
/// The machine-locality that made these loopback-only is UNCHANGED: a browser's debug port is loopback and
/// its profile directory is on this machine, so the Director on this machine is the only thing that can
/// drive it. Going through the Gateway does not change that - the Gateway carries the command here.
/// </summary>
internal static class BrowserEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // GET /browsers - every browser on THIS machine, each fully folded, plus whether browser-harness
        // itself is installed (the rail's dimmed "advertise" state keys off this).
        app.MapGet("/browsers", async (CancellationToken ct) =>
            await RunAsync("browsers-list", null, ct));

        // POST /browsers { name, browser } - register + provision a new browser (does not launch it).
        app.MapPost("/browsers", async (JsonObject? body, CancellationToken ct) =>
            await RunAsync("browsers-create", body, ct, successStatus: StatusCodes.Status201Created));

        // POST /browsers/{id}/start - launch if down (idempotent), wait until the port answers.
        app.MapPost("/browsers/{id}/start", async (string id, CancellationToken ct) =>
            await RunAsync("browsers-start", WithId(null, id), ct));

        // POST /browsers/{id}/stop - close the browser cleanly (CDP Browser.close, with the tracked-pid
        // kill as the wedge safety net). A browser that is already down is a no-op.
        app.MapPost("/browsers/{id}/stop", async (string id, CancellationToken ct) =>
            await RunAsync("browsers-stop", WithId(null, id), ct));

        // GET /browsers/{id}/attach - the BU_NAME / BU_CDP_URL the harness attaches with.
        app.MapGet("/browsers/{id}/attach", async (string id, CancellationToken ct) =>
            await RunAsync("browsers-attach", WithId(null, id), ct));

        // POST /browsers/{id}/signin { done } - done:true records the human finished; otherwise launch +
        // open the account page for the human to sign in by hand (credentials are NEVER automated).
        app.MapPost("/browsers/{id}/signin", async (string id, JsonObject? body, CancellationToken ct) =>
            await RunAsync("browsers-signin", WithId(body, id), ct));

        // POST /browsers/{id}/rename { name }
        app.MapPost("/browsers/{id}/rename", async (string id, JsonObject? body, CancellationToken ct) =>
            await RunAsync("browsers-rename", WithId(body, id), ct));

        // DELETE /browsers/{id} - stop it, delete its folder, drop the entry.
        app.MapDelete("/browsers/{id}", async (string id, CancellationToken ct) =>
            await RunAsync("browsers-delete", WithId(null, id), ct));
    }

    /// <summary>Fold the route's {id} segment into the request body, the way the tunnel verb receives it.</summary>
    private static JsonObject WithId(JsonObject? body, string id)
    {
        var obj = body is null ? new JsonObject() : (JsonObject)body.DeepClone();
        obj["id"] = id;
        return obj;
    }

    /// <summary>
    /// Run the verb and translate its typed result into the HTTP answer. The status mapping is the same one
    /// the Gateway applies to a verb result, so a caller sees the same code whichever door it came through.
    /// </summary>
    private static async Task<IResult> RunAsync(string verb, JsonObject? payload, CancellationToken ct,
        int successStatus = StatusCodes.Status200OK)
    {
        var result = await BrowserExecutor.ExecuteAsync(verb, payload?.ToJsonString(), ct);
        if (result.Ok)
            return string.IsNullOrEmpty(result.BodyJson)
                ? Results.StatusCode(successStatus)
                : Results.Content(result.BodyJson, "application/json", statusCode: successStatus);

        var status = result.Status switch
        {
            DirectorCommandStatus.BadRequest => StatusCodes.Status400BadRequest,
            DirectorCommandStatus.NotFound => StatusCodes.Status404NotFound,
            DirectorCommandStatus.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        };
        // The delete verb's conflict answer historically carried removed:false beside the message, and its
        // caller reads that field. Kept, so switching the implementation underneath does not change what an
        // installed command line sees.
        return status == StatusCodes.Status409Conflict
            ? Results.Json(new { removed = false, error = result.Error }, statusCode: status)
            : Results.Json(new { error = result.Error }, statusCode: status);
    }
}
