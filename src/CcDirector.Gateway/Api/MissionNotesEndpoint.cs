using CcDirector.Core.Utilities;
using CcDirector.Gateway.MissionNotes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The mission-WHY surface for the Mission Screen (Phase 1b, issue #1405): read all WHYs and set one.
/// Backed by <see cref="MissionNoteStore"/> (durable + shared, keyed by the mission's normalized name).
///
/// AUTH: these are device-authed CLIENT routes. They carry no per-route auth of their own - they sit
/// under the non-public "/gateway/..." prefix, so the host-wide device-key middleware
/// (<see cref="Util.AuthMiddleware"/>) gates them exactly like every other client data endpoint (an
/// unauthenticated caller gets the JSON 401). This is PROVEN, not assumed, by
/// MissionNotesEndpointTests (an unauthenticated GET and PUT both 401 over real HTTP).
///
/// This is a client-to-Gateway HTTP surface only; it does NOT touch Director-to-Gateway tunnel traffic
/// (the Gateway Cleanup mission), and it deliberately stays out of GatewayEndpoints.cs and off the bare
/// "/missions" prefix, which the Gateway-native mission store already owns.
/// </summary>
internal static class MissionNotesEndpoint
{
    public static void Map(IEndpointRouteBuilder app, MissionNoteStore store)
    {
        // Every set WHY, for the Mission Screen to show on each card. The Cockpit matches a note to a
        // mission by the normalized key its own groupByMission produces.
        app.MapGet("/gateway/missions/notes", () =>
            Results.Json(new { notes = store.All().Select(Project).ToList() }));

        // Set (or clear) one mission's WHY. An empty/whitespace why UNSETS it (the card shows its flag);
        // any other value stores the trimmed WHY.
        app.MapPut("/gateway/missions/notes", (MissionNoteBody? req) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Mission))
                return Results.Json(new { error = "mission is required" }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var note = store.Set(req.Mission, req.Why, DateTime.UtcNow);
                FileLog.Write($"[MissionNotesEndpoint] PUT mission='{req.Mission}', cleared={note is null}");
                return Results.Json(new { note = note is null ? null : Project(note), cleared = note is null });
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });
    }

    private static object Project(MissionNoteStore.MissionNote n) => new
    {
        key = n.Key,
        mission = n.Mission,
        why = n.Why,
        updatedAt = n.UpdatedAtUtc,
    };
}

/// <summary>Body of the set route: the mission display name and its WHY (empty/whitespace clears it).</summary>
public sealed class MissionNoteBody
{
    public string Mission { get; set; } = "";
    public string? Why { get; set; }
}
