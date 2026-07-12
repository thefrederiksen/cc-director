using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Cleanup mission, Phase 2: dispatch the SESSION-scoped verbs that flow through the
/// <c>/sessions/{sid}/{**rest}</c> catch-all onto the tunnel, instead of dialing the owning Director over HTTP.
/// Used only when stream mode is on and the owner is stream-connected (the caller resolves that via
/// <see cref="Streaming.PushedSessionStore.TryLocate"/>); a request whose (method, rest-path) is not a mapped
/// verb, or a Director with no active stream, returns false so the caller keeps the existing HTTP proxy path -
/// byte-identical when stream mode is off.
///
/// The catch-all carries the session verbs that do NOT have their own literal Gateway route (turns, buffer-html,
/// usage, context, history, github-urls, and the session writes/queue in a later increment). The verb's request
/// DTO and core are the SAME the Director REST route used, so the reply body is identical; the Gateway marshals
/// the request into the verb payload and maps <see cref="DirectorCommandResult"/> back to the HTTP response the
/// browser expects.
///
/// This increment (PR B1) handles the catch-all READS - all of them take only the session id (no payload) and
/// return a JSON DTO, so the mapping is uniform. Writes and the queue path-parameterised verbs come next, on the
/// same dispatch mechanism.
/// </summary>
internal sealed class TunnelCatchAllDispatch
{
    private readonly DirectorCommandRouter.SendDirectorCommandAsync _sendCommand;

    public TunnelCatchAllDispatch(DirectorCommandRouter.SendDirectorCommandAsync sendCommand) =>
        _sendCommand = sendCommand ?? throw new ArgumentNullException(nameof(sendCommand));

    // The session READ verbs that flow through the catch-all (no literal Gateway route). Each takes only the
    // session id and returns a JSON DTO body. buffer / summary / git / handover / recap / wingman-view /
    // snapshot have their own literal Gateway routes and are re-pointed there, NOT here.
    private static readonly Dictionary<string, string> GetVerbByRest = new(StringComparer.OrdinalIgnoreCase)
    {
        ["turns"] = "turns",
        ["buffer/html"] = "buffer-html",
        ["usage"] = "usage",
        ["context"] = "context",
        ["history"] = "history",
        ["github-urls"] = "github-urls",
    };

    /// <summary>
    /// Try to serve this catch-all request over the tunnel. Returns true when handled (the response is written),
    /// false to fall through to the caller's HTTP proxy path (an unmapped verb, or no active stream).
    /// </summary>
    public async Task<bool> TryDispatchAsync(HttpContext ctx, string sid, string directorId, string? rest)
    {
        var verb = ResolveVerb(ctx.Request.Method, rest);
        if (verb is null) return false;

        var result = await DirectorCommandRouter.TrySendAsync(_sendCommand, directorId, verb, sid, null, ctx.RequestAborted);
        if (result is null) return false; // no active stream -> HTTP fallback

        await WriteResultAsync(ctx, result);
        return true;
    }

    private static string? ResolveVerb(string method, string? rest)
    {
        if (rest is null) return null;
        if (HttpMethods.IsGet(method) && GetVerbByRest.TryGetValue(rest, out var verb)) return verb;
        return null;
    }

    // Map a DirectorCommandResult back to the HTTP response the browser expects - the same shape the Director
    // REST route returned (200 + application/json for Ok; the matching typed error code otherwise). BodyJson is
    // already the serialized resource DTO, so it is written verbatim.
    private static async Task WriteResultAsync(HttpContext ctx, DirectorCommandResult result)
    {
        if (result.Ok)
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(result.BodyJson ?? "");
            return;
        }

        ctx.Response.StatusCode = result.Status switch
        {
            DirectorCommandStatus.BadRequest => StatusCodes.Status400BadRequest,
            DirectorCommandStatus.NotFound => StatusCodes.Status404NotFound,
            DirectorCommandStatus.Conflict => StatusCodes.Status409Conflict,
            DirectorCommandStatus.Locked => StatusCodes.Status423Locked,
            _ => StatusCodes.Status500InternalServerError,
        };
        await ctx.Response.WriteAsJsonAsync(new { error = result.Error ?? $"director returned {result.Status}" });
    }
}
