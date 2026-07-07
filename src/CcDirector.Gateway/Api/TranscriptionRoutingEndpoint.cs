using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Gateway as the single source of truth for the WHOLE transcription routing target
/// (issue #506). Before this, a Director on a Gateway only fetched the vault KEY and decided the
/// base URL + mode locally (compile-time constants). Now the Gateway composes the full pair
/// server-side and a Director asks for it in one call:
///
///   GET /transcription/routing -> { mode, baseUrl, model, key } | 404 (no key set)
///
/// The Gateway owns the keys (its vault), so it pairs URL+key here from the one pure
/// <see cref="TranscriptionEndpointResolver"/> (always the DevThrottle proxy). The <c>mode</c> and
/// <c>transport</c> fields are constant ("devthrottle" / "batch") and kept only for wire compatibility
/// with older Directors that still parse them. Inherits the host-wide token middleware like every other
/// Gateway route.
/// </summary>
internal static class TranscriptionRoutingEndpoint
{
    public static void Map(IEndpointRouteBuilder app, KeyVault vault)
    {
        app.MapGet("/transcription/routing", (HttpContext ctx) =>
        {
            // Stamp every response from THIS route (including the 404-no-key below) with a marker
            // header. It lets a Director tell apart "Gateway has this route but no key set yet"
            // (header present, 404) from "older Gateway that never mapped this route" (header
            // absent, framework 404) - so the Director can surface a clear "update your Gateway"
            // message instead of silently using a baked-in URL (issue #506, no-fallback rule).
            ctx.Response.Headers["X-Transcription-Routing"] = "1";

            // Resolve the DevThrottle routing -> (baseUrl, key, model) through the SINGLE transcription
            // owner (issue #839), the same resolve-and-key path every batch caller uses.
            var routing = new Transcription.GatewayTranscriptionService(vault).Resolve();
            var endpoint = routing.Endpoint;

            if (routing.Key is null)
            {
                // No silent default: the Gateway reachable but the DevThrottle key is not set yet.
                // The Director reports transcription unavailable (never a baked-in URL).
                FileLog.Write($"[TranscriptionRoutingEndpoint] GET /transcription/routing: no key for {endpoint.RequireKeyName()}");
                return Results.NotFound(new { error = "no DevThrottle key set", mode = "devthrottle" });
            }

            FileLog.Write($"[TranscriptionRoutingEndpoint] GET /transcription/routing: baseUrl={endpoint.BaseUrl}, model={endpoint.Model}");
            return Results.Json(new
            {
                mode = "devthrottle",
                transport = "batch",
                baseUrl = endpoint.BaseUrl,
                model = endpoint.Model,
                key = routing.Key,
            });
        });
    }
}
