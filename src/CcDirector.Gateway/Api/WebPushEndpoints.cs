using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Push;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Web Push REST surface behind the mobile app-icon "needs you" dot. The phone fetches the
/// Gateway's VAPID public key, subscribes its browser to push, and registers that subscription here;
/// the background <see cref="Push.WebPushNeedsYouNotifier"/> then messages every subscription when a
/// session needs the user.
///
/// Auth is the Gateway's host-wide token middleware, so these routes inherit it (the mobile app
/// attaches the per-machine Bearer). The VAPID public key is not a secret; the private key never
/// leaves the Gateway.
///
///   GET    /push/vapid-public-key   -> { "publicKey": "..." }
///   POST   /push/subscribe          body a browser PushSubscription.toJSON() -> { "ok": true }
///   POST   /push/unsubscribe        body { "endpoint": "..." }               -> { "ok": true }
/// </summary>
internal static class WebPushEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <param name="onSubscribed">Invoked after a NEW subscription is added, so the notifier can push
    /// the current count to the fresh device promptly. Null when no notifier is wired.</param>
    public static void Map(
        IEndpointRouteBuilder app,
        string vapidPublicKey,
        PushSubscriptionStore store,
        Action? onSubscribed)
    {
        app.MapGet("/push/vapid-public-key", () => Results.Json(new { publicKey = vapidPublicKey }));

        app.MapPost("/push/subscribe", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<SubscribeBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);

                var endpoint = body?.Endpoint;
                var p256dh = body?.Keys?.P256dh;
                var auth = body?.Keys?.Auth;
                if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(p256dh) || string.IsNullOrWhiteSpace(auth))
                    return Results.BadRequest(new { error = "body must be a push subscription with endpoint and keys { p256dh, auth }" });

                var isNew = store.Add(endpoint, p256dh, auth);
                if (isNew) onSubscribed?.Invoke();
                return Results.Json(new { ok = true });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[WebPushEndpoints] POST /push/subscribe bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        app.MapPost("/push/unsubscribe", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<UnsubscribeBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (string.IsNullOrWhiteSpace(body?.Endpoint))
                    return Results.BadRequest(new { error = "body { \"endpoint\": \"...\" } is required" });

                store.Remove(body.Endpoint);
                return Results.Json(new { ok = true });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[WebPushEndpoints] POST /push/unsubscribe bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });
    }

    // Matches a browser PushSubscription.toJSON(): { endpoint, expirationTime, keys: { p256dh, auth } }.
    private sealed record SubscribeBody(
        [property: JsonPropertyName("endpoint")] string? Endpoint,
        [property: JsonPropertyName("keys")] SubscribeKeys? Keys);

    private sealed record SubscribeKeys(
        [property: JsonPropertyName("p256dh")] string? P256dh,
        [property: JsonPropertyName("auth")] string? Auth);

    private sealed record UnsubscribeBody(
        [property: JsonPropertyName("endpoint")] string? Endpoint);
}
