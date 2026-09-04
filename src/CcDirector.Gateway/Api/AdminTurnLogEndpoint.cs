using System.Text.Json.Serialization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.TurnLog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The administrator turn-log surface - the switch that decides whose turn ends are recorded:
///
///   GET  /gateway/admin/turn-log  -> { switches: [ { account, machine, enabled, actor, reason, recorded_at_utc } ] }
///   POST /gateway/admin/turn-log  -> { outcome }
///
/// The website's admin screen calls this. It is the ONLY way capture is switched on or off, and with no
/// decision on record nothing is captured for anybody.
///
/// AUTHORIZATION IS THE SAME ADMIN SERVICE TOKEN THE TRIAL SURFACE USES, and that is a considered choice
/// rather than a shortcut. This is the same caller (the website's admin API, a SERVER holding no device
/// credential), the same screen, and the same class of authority: an administrator acting deliberately on an
/// account that is not their own. Two secrets guarding one screen would not be a separation of authority,
/// only more key material to deploy and rotate. The separation that DOES matter is from the read-only report
/// token, and that separation is enforced for us: the gate is <see cref="AdminTrialEndpoint.ServiceTokenDenial"/>
/// itself, called rather than copied, so there is one definition of who may act as an administrator here and
/// it cannot drift out of step with the other surface that uses it.
///
/// EVERY WRITE NAMES A PERSON AND A REASON, and both are required rather than encouraged. Switching capture
/// on for an account that is not ours copies that account's terminal into our corpus, so the reason field is
/// where the permission for that is written down. A row with a blank reason answers no question anybody will
/// actually ask, which is: who agreed to this?
///
/// IT NEVER READS OR RETURNS CAPTURED CONTENT. This endpoint moves a switch and lists decisions. The corpus
/// itself is a file on the Gateway's disk pulled by a person with access to that machine - deliberately not
/// something any web request can dredge terminals out of.
/// </summary>
internal static class AdminTurnLogEndpoint
{
    /// <summary>The route. Exact-match public in <c>AuthMiddleware</c>; the endpoint carries its own gate.</summary>
    public const string Path = "/gateway/admin/turn-log";

    /// <summary>What the caller sends. Every wire name is spelled out: the host binds property names
    /// case-insensitively, which is NOT punctuation-insensitively, so <c>recorded_at_utc</c> does not bind
    /// to <c>RecordedAtUtc</c> by that rule - it binds to nothing and arrives null.</summary>
    internal sealed record SetRequest(
        [property: JsonPropertyName("account")] string? Account,
        [property: JsonPropertyName("machine")] string? Machine,
        [property: JsonPropertyName("enabled")] bool? Enabled,
        [property: JsonPropertyName("actor")] string? Actor,
        [property: JsonPropertyName("reason")] string? Reason);

    internal const string OutcomeRecorded = "recorded";
    internal const string OutcomeUnknown = "unknown";

    public static void Map(IEndpointRouteBuilder app, TurnLogSwitchStore switches)
    {
        ArgumentNullException.ThrowIfNull(switches);

        app.MapGet(Path, (HttpContext ctx) =>
        {
            try
            {
                if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } gate) return gate;
                var rows = switches.All().Select(r => new
                {
                    account = r.Account,
                    machine = r.Machine,
                    enabled = r.Enabled,
                    actor = r.Actor,
                    reason = r.Reason,
                    recorded_at_utc = r.RecordedUtc,
                });
                return Results.Json(new { switches = rows });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[AdminTurnLogEndpoint] GET {Path} FAILED ({ex.GetType().Name}): {ex.Message}");
                return Results.Json(new { outcome = OutcomeUnknown }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapPost(Path, async (HttpContext ctx) =>
        {
            try
            {
                // THE GATE COMES FIRST, BEFORE THE BODY IS READ, for the same reason it does on the trial
                // surface: until this line runs the request is an anonymous one off the internet, and a
                // stranger must not be able to make this Gateway deserialize what it sent, nor learn
                // anything about its own input from a route whose only correct answer to it is "who are
                // you?".
                if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } gate) return gate;

                SetRequest? body;
                try
                {
                    body = await ctx.Request.ReadFromJsonAsync<SetRequest>(ctx.RequestAborted).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[AdminTurnLogEndpoint] rejected: the request body is not readable JSON ({ex.GetType().Name})");
                    return Results.BadRequest(new { error = "the request body is not readable JSON" });
                }

                return Handle(ctx, body, switches);
            }
            catch (Exception ex)
            {
                // UNKNOWN, not a refusal. We genuinely do not know whether the row landed, and an
                // administrator told "denied" would switch it on a second time.
                FileLog.Write($"[AdminTurnLogEndpoint] POST {Path} FAILED ({ex.GetType().Name}): {ex.Message} - answering UNKNOWN");
                return Results.Json(new { outcome = OutcomeUnknown }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        FileLog.Write($"[AdminTurnLogEndpoint] mapped {Path} (service-token authorized)");
    }

    /// <summary>Internal so every refusal can be tested directly, without standing a host up per case.</summary>
    internal static IResult Handle(HttpContext ctx, SetRequest? body, TurnLogSwitchStore switches)
    {
        if (AdminTrialEndpoint.ServiceTokenDenial(ctx) is { } denial) return denial;

        if (body is null)
            return Results.BadRequest(new { error = "a request body is required" });

        // A BLANK SCOPE IS NOT A WILDCARD. The wildcard is the literal "*", written on purpose; a caller
        // that forgot to send an account must never have that read as "every account on the fleet".
        if (string.IsNullOrWhiteSpace(body.Account))
            return Results.BadRequest(new { error = $"an account is required: an identifier, or \"{TurnLogSwitchEntity.Any}\" for every account" });
        if (string.IsNullOrWhiteSpace(body.Machine))
            return Results.BadRequest(new { error = $"a machine is required: a computer's identifier, or \"{TurnLogSwitchEntity.Any}\" for every machine" });
        if (body.Enabled is not { } enabled)
            return Results.BadRequest(new { error = "enabled is required: true to record turn ends for this scope, false to stop" });
        if (string.IsNullOrWhiteSpace(body.Actor))
            return Results.BadRequest(new { error = "an actor is required: a capture decision must record who made it" });
        if (string.IsNullOrWhiteSpace(body.Reason))
            return Results.BadRequest(new { error = "a reason is required: for an account that is not yours, this is where the permission is recorded" });

        switches.Set(body.Account, body.Machine, enabled, body.Actor, body.Reason);
        return Results.Json(new { outcome = OutcomeRecorded, enabled });
    }
}
