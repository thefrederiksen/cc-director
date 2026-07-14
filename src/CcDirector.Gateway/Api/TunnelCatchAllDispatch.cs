using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Cleanup mission (the cut): dispatch the SESSION-scoped verbs that flow through the
/// <c>/sessions/{sid}/{**rest}</c> catch-all onto THE TUNNEL. This verb TABLE is the explicit set the catch-all
/// route serves - it is NOT a generic HTTP passthrough (the old HTTP reverse-proxy leg was deleted at the cut).
/// The caller resolves the owner via <see cref="Streaming.PushedSessionStore.TryLocate"/> (a located owner is
/// always tunnel-connected); a request whose (method, rest-path) is not a mapped verb returns false so the
/// caller answers 404. There is NO HTTP fallback.
///
/// The catch-all carries the session verbs that do NOT have their own literal Gateway route: the reads (turns,
/// buffer-html, usage, context, history, github-urls, queue-read) and the writes (resize, clear-context,
/// history-picker, mobile-mode, voice-mode, wingman-enabled, relink, execute-action, and the voice queue's
/// add/update/remove/move/clear/send). The verb's request DTO and core are the SAME the Director REST route
/// used (Phase 0), so the reply body is identical; the Gateway only marshals method + path + body into the verb
/// payload and maps <see cref="DirectorCommandResult"/> back to the HTTP response.
///
/// Marshaling:
///  - reads and the no-body writes carry no payload (the target session is the command's SessionId);
///  - the body-shaped writes pass the raw request body straight through as PayloadJson (the REST route and the
///    tunnel verb share one DTO+core, so the bytes are identical);
///  - the queue path-parameterised verbs FOLD the {itemId} path segment into the payload (Architect note): the
///    request body (a JSON object or empty) gets an "itemId" field overlaid before it is sent.
/// </summary>
internal sealed class TunnelCatchAllDispatch
{
    private readonly DirectorCommandRouter.SendDirectorCommandAsync _sendCommand;

    public TunnelCatchAllDispatch(DirectorCommandRouter.SendDirectorCommandAsync sendCommand) =>
        _sendCommand = sendCommand ?? throw new ArgumentNullException(nameof(sendCommand));

    private enum Payload { None, Body }

    private sealed record Plan(string Verb, Payload Payload, string? ItemId = null);

    // Flat GET reads (session id only, JSON DTO body).
    private static readonly Dictionary<string, string> GetReads = new(StringComparer.OrdinalIgnoreCase)
    {
        ["turns"] = "turns",
        ["buffer/html"] = "buffer-html",
        ["usage"] = "usage",
        ["context"] = "context",
        ["history"] = "history",
        ["github-urls"] = "github-urls",
        ["queue"] = "queue-read",
    };

    // Flat writes keyed by "METHOD rest". Payload is the raw request body (empty for the no-body writes).
    private static readonly Dictionary<string, string> BodyWrites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["POST resize"] = "resize",
        ["POST clear-context"] = "clear-context",
        ["POST history-picker"] = "history-picker",
        ["POST mobile-mode"] = "mobile-mode",
        ["POST voice-mode"] = "voice-mode",
        ["POST wingman-enabled"] = "wingman-enabled",
        ["POST relink"] = "relink",
        ["POST execute-action"] = "execute-action",
        ["POST queue"] = "queue-add",
        ["DELETE queue"] = "queue-clear",
    };

    /// <summary>
    /// Try to serve this catch-all request over the tunnel. Returns true when handled (the response is written),
    /// false when the (method, rest-path) is not a mapped verb (the caller answers 404). There is no HTTP fallback.
    /// </summary>
    public async Task<bool> TryDispatchAsync(HttpContext ctx, string sid, string directorId, string? rest)
    {
        var plan = Resolve(ctx.Request.Method, rest);
        if (plan is null) return false;

        string payloadJson = "";
        if (plan.Payload == Payload.Body || plan.ItemId is not null)
        {
            // Buffer the request body so an HTTP fallback (no active stream) can still re-forward it.
            ctx.Request.EnableBuffering();
            var body = await ReadBodyAsync(ctx);
            ctx.Request.Body.Position = 0;
            payloadJson = plan.ItemId is not null ? FoldItemId(body, plan.ItemId) : body;
        }

        var command = new DirectorCommand
        {
            CommandId = Guid.NewGuid().ToString("N"),
            Verb = plan.Verb,
            SessionId = sid,
            PayloadJson = payloadJson,
        };

        var result = await _sendCommand(directorId, command, ctx.RequestAborted);
        FileLog.Write($"[TunnelCatchAllDispatch] {ctx.Request.Method} {rest} -> verb={plan.Verb} sid={sid}: {(result is null ? "owner not tunnel-connected" : result.Status.ToString())}");
        if (result is null) return false; // owner dropped the tunnel between locate and dispatch (rare) -> caller answers

        await WriteResultAsync(ctx, result);
        return true;
    }

    private static Plan? Resolve(string method, string? rest)
    {
        if (rest is null) return null;

        if (HttpMethods.IsGet(method) && GetReads.TryGetValue(rest, out var readVerb))
            return new Plan(readVerb, Payload.None);

        if (BodyWrites.TryGetValue($"{method} {rest}", out var writeVerb))
            return new Plan(writeVerb, Payload.Body);

        // Queue path-parameterised verbs: queue/{itemId}[/move-up|/move-down|/send].
        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && string.Equals(segments[0], "queue", StringComparison.OrdinalIgnoreCase))
        {
            var itemId = segments[1];
            if (segments.Length == 2)
            {
                if (HttpMethods.IsDelete(method)) return new Plan("queue-remove", Payload.Body, itemId);
                if (HttpMethods.IsPatch(method)) return new Plan("queue-update", Payload.Body, itemId);
            }
            else if (segments.Length == 3 && HttpMethods.IsPost(method))
            {
                return segments[2].ToLowerInvariant() switch
                {
                    "move-up" => new Plan("queue-move-up", Payload.Body, itemId),
                    "move-down" => new Plan("queue-move-down", Payload.Body, itemId),
                    "send" => new Plan("queue-send", Payload.Body, itemId),
                    _ => null,
                };
            }
        }

        return null;
    }

    private static async Task<string> ReadBodyAsync(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    // Overlay the path {itemId} onto the request body's JSON object (empty body -> a fresh object), so the
    // verb payload carries both the id from the path and any fields from the body (queue-update's text).
    private static string FoldItemId(string body, string itemId)
    {
        JsonObject obj;
        try
        {
            obj = string.IsNullOrWhiteSpace(body) ? new JsonObject() : (JsonNode.Parse(body)?.AsObject() ?? new JsonObject());
        }
        catch (System.Text.Json.JsonException)
        {
            obj = new JsonObject();
        }
        obj["itemId"] = itemId;
        return obj.ToJsonString();
    }

    // Map a DirectorCommandResult back to the HTTP response the browser expects - the same shape the Director
    // REST route returned (200 + application/json for Ok; the matching typed error code otherwise). BodyJson is
    // already the serialized DTO, so it is written verbatim.
    private static async Task WriteResultAsync(HttpContext ctx, DirectorCommandResult result)
    {
        if (result.Ok)
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            if (!string.IsNullOrEmpty(result.BodyJson))
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(result.BodyJson);
            }
            return;
        }

        ctx.Response.StatusCode = result.Status switch
        {
            DirectorCommandStatus.BadRequest => StatusCodes.Status400BadRequest,
            DirectorCommandStatus.NotFound => StatusCodes.Status404NotFound,
            DirectorCommandStatus.Conflict => StatusCodes.Status409Conflict,
            DirectorCommandStatus.Locked => StatusCodes.Status423Locked,
            // Stable Release (v1.3.0), Tier 1 item 1: the Gateway gave up waiting, or the tunnel died in flight.
            // Neither is an internal server error - the collapse below would say the Gateway itself broke.
            DirectorCommandStatus.Timeout => StatusCodes.Status504GatewayTimeout,
            DirectorCommandStatus.TunnelDropped => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status500InternalServerError,
        };
        await ctx.Response.WriteAsJsonAsync(new { error = result.Error ?? $"director returned {result.Status}" });
    }
}
