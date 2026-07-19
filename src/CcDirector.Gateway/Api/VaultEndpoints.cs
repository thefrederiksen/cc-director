using System.Text.Json;
using CcDirector.Core;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The central key-vault REST surface (docs/architecture/gateway/GATEWAY_KEY_VAULT.md).
/// Keys are set once here - from the Cockpit Keys page - and Directors GET them on demand.
/// Auth is the Gateway's host-wide token middleware, so these routes inherit it; the tailnet
/// plus that token is the trust boundary. Values leave the Gateway only via the single-key
/// GET a Director calls; the list route exposes names only, never values.
///
///   GET    /vault/keys           -> { "names": [...] }            (names only)
///   GET    /vault/keys/{name}    -> { "name", "value" } | 404
///   PUT    /vault/keys/{name}    body { "value": "..." } -> { "name", "set": true }
///   DELETE /vault/keys/{name}    -> { "name", "deleted": bool }
///
/// DENIED IN WHOLE ON HOSTED. Every route in this file is refused on the hosted Gateway - the reads,
/// the write, and the delete alike.
///
/// The store behind these routes is ONE global key vault file at the shared storage root. It carries no
/// tenant anywhere: not in the file format, not in the store, not in the routes. The routes sit behind
/// only the host-wide authentication gate, and that gate admits ANY enrolled device key from ANY account.
/// So on shared hosted infrastructure every subscriber could read every other subscriber's provider
/// credentials in cleartext (the single-key GET returns the raw value), overwrite them, and delete them.
/// That is credential THEFT and TAMPERING, not merely a disclosure, which is why the whole surface closes
/// rather than only the read.
///
/// It is a deny of the WHOLE GROUP rather than a guard on the value-returning read, because a caller who
/// can overwrite another account's key can redirect that account's paid usage, and a caller who can delete
/// it can silently disable someone else's service. A route-by-route fix also rots: the next route added to
/// this file would be open again by default and nobody would notice.
///
/// It is a deny rather than a per-tenant partition because there is nothing to partition by yet. The vault
/// file has no tenant column and no per-account namespace, so partitioning here would mean inventing
/// storage that does not exist - a half-partition, which is worse than an honest refusal. The debt is
/// written down on the pull request: when the vault is properly partitioned per account, these routes come
/// back one at a time.
///
/// Self-host is COMPLETELY unchanged, and that is the control. Self-host is single-tenant, the owner sets
/// his own keys here from the Cockpit and his own Director reads them back, and a deny scoped to the wrong
/// signal would break the shipped product to protect the unshipped one.
/// </summary>
internal static class VaultEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The hosted refusal for the whole key-vault group, or null on self-host where nothing changes.
    ///
    /// Gated on <see cref="GatewayHostedMode.IsHosted"/> - the INDEPENDENT deployment signal - and NOT on a
    /// boundary or tenant argument being passed in. A security branch that depends on an optional argument
    /// fails OPEN the moment a caller omits it, so asking hosted mode directly means this group cannot serve
    /// a key on hosted however the host happens to be wired.
    ///
    /// 404 rather than 403: on hosted there is no per-account key store, so this surface does not exist as a
    /// concept and "not here" is the truthful answer. 403 would imply some credential could reach it, and
    /// none can.
    ///
    /// It REFUSES; it never serves an empty name list or a null value. An empty list is a false statement
    /// about the vault, where an absent one is merely absent.
    /// </summary>
    private static IResult? DenyOnHosted()
    {
        if (!GatewayHostedMode.IsHosted) return null;

        FileLog.Write("[VaultEndpoints] DENIED on hosted: the key vault is one global store with no per-account partition");
        return Results.Json(
            new { error = "the key vault is not available on the hosted gateway" },
            statusCode: StatusCodes.Status404NotFound);
    }

    public static void Map(IEndpointRouteBuilder outer, KeyVault vault)
    {
        FileLog.Write($"[VaultEndpoints] mapping /vault/keys; hosted={GatewayHostedMode.IsHosted} - on hosted EVERY route in this group is refused");

        // The whole group behind ONE endpoint filter, rather than a guard line repeated in every handler.
        // A repeated guard is a thing to forget: the route added to this file next year would be open by
        // default. A group filter runs before EVERY route mapped below, including routes that do not exist
        // yet, so the refusal cannot rot as the group grows. The empty prefix keeps the route paths written
        // out in full, exactly as before, so the self-host surface is byte-identical.
        var app = outer.MapGroup("");
        app.AddEndpointFilter(async (ctx, next) =>
        {
            if (DenyOnHosted() is { } denied) return denied;
            return await next(ctx);
        });

        app.MapGet("/vault/keys", () => Results.Json(new { names = vault.ListNames() }));

        app.MapGet("/vault/keys/{name}", (string name) =>
        {
            var value = vault.Get(name);
            return value is null
                ? Results.NotFound(new { error = "no such key", name })
                : Results.Json(new { name, value });
        });

        app.MapPut("/vault/keys/{name}", async (string name, HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<VaultKeyBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body?.Value is null)
                    return Results.BadRequest(new { error = "body { \"value\": \"...\" } is required" });

                vault.Set(name, body.Value);
                return Results.Json(new { name, set = true });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[VaultEndpoints] PUT {name} bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        app.MapDelete("/vault/keys/{name}", (string name) =>
            Results.Json(new { name, deleted = vault.Delete(name) }));
    }

    private sealed record VaultKeyBody(string? Value);
}
