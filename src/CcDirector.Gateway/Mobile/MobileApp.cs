using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace CcDirector.Gateway.Mobile;

/// <summary>
/// Serves the React Progressive Web App at <c>/mobile</c> (docs/architecture/mobile/). The build
/// output (Vite, copied into <c>wwwroot/mobile</c> by the release-gated MSBuild target on
/// CcDirector.Gateway.csproj) is served as static files, EXCEPT <c>index.html</c>, which is
/// served with the per-machine Gateway token injected in place of the <c>__GATEWAY_TOKEN__</c>
/// placeholder - the same pattern the existing <c>/voice</c> page uses. The app reads
/// <c>window.__GW_TOKEN__</c> and sends it as a Bearer header on API calls, so it works whether
/// global Gateway auth is on or off.
///
/// The surface path is <c>/mobile</c> (owner ruling 2026-07-20: every Gateway surface is a path under
/// the one public base, so the phone URL is <c>{base}/mobile</c> exactly as the Cockpit is
/// <c>{base}/cockpit</c>). The app previously mounted at <c>/m</c>; <see cref="MapLegacyRedirect"/> keeps
/// that old path working with a 301 to the <c>/mobile</c> equivalent so installed phone PWAs and
/// bookmarks do not break.
///
/// A single catch-all handler owns serving so the token injection cannot be bypassed by a raw
/// request for <c>/mobile/index.html</c>, and so a hard navigation to a client-side route
/// (<c>/mobile/session/&lt;id&gt;</c>) falls back to the injected shell for the React router to resolve.
/// </summary>
public static class MobileApp
{
    private const string IndexFile = "index.html";
    private const string TokenPlaceholder = "__GATEWAY_TOKEN__";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>
    /// The directory the built mobile app is served from: <c>wwwroot/mobile</c> beside the running
    /// executable. The release-gated MSBuild target populates it; on a routine (Debug) build it
    /// does not exist and <c>/mobile</c> answers 404 (the mobile app ships only in release builds).
    /// </summary>
    public static string WebRoot => Path.Combine(AppContext.BaseDirectory, "wwwroot", "mobile");

    /// <summary>
    /// Map the <c>/mobile</c> routes. Call BEFORE the fallback Cockpit proxy so these explicit routes
    /// win. <paramref name="gatewayToken"/> is the per-machine token injected into index.html.
    /// </summary>
    public static void Map(WebApplication app, string gatewayToken)
    {
        FileLog.Write($"[MobileApp] serving /mobile from {WebRoot} (exists={Directory.Exists(WebRoot)})");

        app.MapGet("/mobile", (HttpContext ctx) => ServeAsync(ctx, gatewayToken, ""));
        app.MapGet("/mobile/{*path}", (HttpContext ctx, string? path) => ServeAsync(ctx, gatewayToken, path ?? ""));
    }

    /// <summary>
    /// Map the legacy <c>/m</c> mount as a permanent (301) redirect to the canonical <c>/mobile</c>
    /// surface, preserving the sub-path and query string. An installed phone PWA or a bookmark saved
    /// against the old <c>/m/...</c> URL keeps working: the browser follows the 301 to <c>/mobile/...</c>,
    /// and because the redirect <c>Location</c> carries no fragment, the browser re-attaches the original
    /// URL fragment to the target - so the sign-in callback (<c>/m/device-callback#access_token=...</c>,
    /// the path devthrottle.com still hands back to) lands on <c>/mobile/device-callback</c> with the
    /// fragment intact. Only GET/HEAD navigations are redirected here; <c>POST /m/enroll</c> is kept as a
    /// live back-compat route by <c>MobileEnrollmentEndpoint</c> (a 301 would drop the POST body).
    /// Call BEFORE the fallback Cockpit proxy so these explicit routes win.
    /// </summary>
    public static void MapLegacyRedirect(WebApplication app)
    {
        app.MapGet("/m", (HttpContext ctx) => RedirectToMobile(ctx, ""));
        app.MapGet("/m/{*path}", (HttpContext ctx, string? path) => RedirectToMobile(ctx, path ?? ""));
    }

    /// <summary>
    /// Write a 301 to the <c>/mobile</c> equivalent of a legacy <c>/m</c> request, carrying the sub-path
    /// and the original query string. Returns a completed task (the handler is a RequestDelegate).
    /// </summary>
    private static Task RedirectToMobile(HttpContext ctx, string relativePath)
    {
        var target = string.IsNullOrEmpty(relativePath) ? "/mobile/" : $"/mobile/{relativePath}";
        target += ctx.Request.QueryString.Value ?? "";
        FileLog.Write($"[MobileApp] legacy /m -> {target} (301)");
        ctx.Response.StatusCode = StatusCodes.Status301MovedPermanently;
        ctx.Response.Headers.Location = target;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Serve one request under <c>/mobile</c>: a real static asset when the path resolves to a file in
    /// the web root, otherwise the token-injected index.html (the SPA shell and client-route
    /// fallback). Answers 404 only when the mobile app is not built into this host. Writes the
    /// response directly (the handler is a RequestDelegate), so it returns a non-generic Task.
    /// </summary>
    private static async Task ServeAsync(HttpContext ctx, string gatewayToken, string relativePath)
    {
        var webRoot = WebRoot;
        if (!Directory.Exists(webRoot))
        {
            FileLog.Write("[MobileApp] /mobile requested but the mobile app is not built into this host (no wwwroot/mobile)");
            await WriteNotFoundAsync(ctx, "Mobile app not built into this Gateway (release build only).");
            return;
        }

        // A request for a concrete asset (has a path and is not index.html) is served as a file
        // when it resolves safely inside the web root; everything else falls back to the shell.
        if (!string.IsNullOrEmpty(relativePath)
            && !string.Equals(relativePath, IndexFile, StringComparison.OrdinalIgnoreCase)
            && TryResolveFile(webRoot, relativePath, out var fullPath))
        {
            await ServeStaticFileAsync(ctx, fullPath, relativePath);
            return;
        }

        await ServeIndexAsync(ctx, webRoot, gatewayToken);
    }

    /// <summary>
    /// Resolve a request path to a real file strictly inside the web root, defeating path
    /// traversal. Returns false when the file does not exist (the caller then serves the shell).
    /// </summary>
    private static bool TryResolveFile(string webRoot, string relativePath, out string fullPath)
    {
        fullPath = "";
        var combined = Path.GetFullPath(Path.Combine(webRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSep = webRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!File.Exists(combined))
            return false;
        fullPath = combined;
        return true;
    }

    private static async Task ServeStaticFileAsync(HttpContext ctx, string fullPath, string relativePath)
    {
        var contentType = ContentTypes.TryGetContentType(fullPath, out var ct) ? ct : "application/octet-stream";
        // Vite emits content-hashed asset names under assets/, so they are safe to cache hard.
        // The service worker and manifest must revalidate so an updated app is picked up.
        ctx.Response.Headers.CacheControl = relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
            ? "public, max-age=31536000, immutable"
            : "no-cache";
        ctx.Response.ContentType = contentType;
        await ctx.Response.SendFileAsync(fullPath);
    }

    private static async Task ServeIndexAsync(HttpContext ctx, string webRoot, string gatewayToken)
    {
        var indexPath = Path.Combine(webRoot, IndexFile);
        if (!File.Exists(indexPath))
        {
            FileLog.Write($"[MobileApp] index.html missing under {webRoot}");
            await WriteNotFoundAsync(ctx, "Mobile app index.html missing from this Gateway build.");
            return;
        }

        // Issue #908: the shell is served AS-IS - the per-machine master token is NO LONGER injected.
        // The app has no credential until the phone signs in on devthrottle.com and enrolls (POST
        // /mobile/enroll), which returns a per-device key the app stores and sends itself. So reaching
        // /mobile hands out nothing; the gatewayToken parameter is retained only for backward compatibility with
        // the Map() signature and is deliberately unused. A defensive replace still strips the old
        // placeholder to an empty string in case a stale index.html (from a prior build) still carries it,
        // so a literal "__GATEWAY_TOKEN__" never reaches the browser.
        _ = gatewayToken;
        var template = await File.ReadAllTextAsync(indexPath);
        var html = template.Replace(TokenPlaceholder, string.Empty);
        // The shell no longer carries a secret, but it still must revalidate so an updated app is picked up.
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.WriteAsync(html);
    }

    private static async Task WriteNotFoundAsync(HttpContext ctx, string message)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync(message);
    }
}
