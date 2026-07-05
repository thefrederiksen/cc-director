using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace CcDirector.Gateway.Cockpit;

/// <summary>
/// Serves the React desktop Cockpit at <c>/c</c> (epic #967, the rebuild of the Blazor Server
/// Cockpit). The build output (Vite, copied into <c>wwwroot/c</c> by the release-gated MSBuild
/// target <c>BuildCockpitApp</c> on CcDirector.Gateway.csproj) is served as static files, with any
/// unknown path under <c>/c</c> falling back to <c>index.html</c> so a hard navigation to a
/// client-side route (<c>/c/fleet</c>) still renders the shell for the React router to resolve.
///
/// This is the direct desktop analog of how the mobile app is served at <c>/m</c>
/// (<see cref="Mobile.MobileApp"/>), and it is mapped BEFORE the fallback Cockpit proxy
/// (<see cref="CockpitProxy"/>) so these explicit <c>/c</c> routes win while every other path keeps
/// falling through to the live Blazor Cockpit unchanged. That is the coexistence the migration needs:
/// the React and Blazor Cockpits run side by side and a path flips from one to the other when it is
/// ready.
///
/// Unlike <c>/m</c>, the shell carries NO credential and needs no token injection: the browser talks
/// only to the Gateway through root-relative paths, and the terminal WebSocket token rides as the
/// <c>cc-gateway-token</c> cookie the client-core startup sets - so <c>index.html</c> is served
/// verbatim.
/// </summary>
public static class CockpitReactApp
{
    private const string IndexFile = "index.html";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>
    /// The directory the built React Cockpit is served from: <c>wwwroot/c</c> beside the running
    /// executable. The release-gated MSBuild target populates it; on a routine (Debug) build it does
    /// not exist and <c>/c</c> answers 404 (the React Cockpit ships only in release builds, exactly
    /// like <c>/m</c>).
    /// </summary>
    public static string WebRoot => Path.Combine(AppContext.BaseDirectory, "wwwroot", "c");

    /// <summary>
    /// Map the <c>/c</c> routes. Call BEFORE the fallback Cockpit proxy so these explicit routes win
    /// over the Blazor Cockpit catch-all.
    /// </summary>
    public static void Map(WebApplication app)
    {
        FileLog.Write($"[CockpitReactApp] serving /c from {WebRoot} (exists={Directory.Exists(WebRoot)})");

        app.MapGet("/c", (HttpContext ctx) => ServeAsync(ctx, ""));
        app.MapGet("/c/{*path}", (HttpContext ctx, string? path) => ServeAsync(ctx, path ?? ""));
    }

    /// <summary>
    /// Serve one request under <c>/c</c>: a real static asset when the path resolves to a file in the
    /// web root, otherwise <c>index.html</c> (the single-page-app shell and client-route fallback).
    /// Answers 404 only when the React Cockpit is not built into this host. Writes the response
    /// directly (the handler is a RequestDelegate), so it returns a non-generic Task.
    /// </summary>
    private static async Task ServeAsync(HttpContext ctx, string relativePath)
    {
        var webRoot = WebRoot;
        if (!Directory.Exists(webRoot))
        {
            FileLog.Write("[CockpitReactApp] /c requested but the React Cockpit is not built into this host (no wwwroot/c)");
            await WriteNotFoundAsync(ctx, "React Cockpit not built into this Gateway (release build only).");
            return;
        }

        // A request for a concrete asset (has a path and is not index.html) is served as a file when
        // it resolves safely inside the web root; everything else falls back to the shell.
        if (!string.IsNullOrEmpty(relativePath)
            && !string.Equals(relativePath, IndexFile, StringComparison.OrdinalIgnoreCase)
            && TryResolveFile(webRoot, relativePath, out var fullPath))
        {
            await ServeStaticFileAsync(ctx, fullPath, relativePath);
            return;
        }

        await ServeIndexAsync(ctx, webRoot);
    }

    /// <summary>
    /// Resolve a request path to a real file strictly inside the web root, defeating path traversal.
    /// Returns false when the file does not exist (the caller then serves the shell).
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
        // index.html and any non-hashed asset must revalidate so an updated app is picked up.
        ctx.Response.Headers.CacheControl = relativePath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
            ? "public, max-age=31536000, immutable"
            : "no-cache";
        ctx.Response.ContentType = contentType;
        await ctx.Response.SendFileAsync(fullPath);
    }

    private static async Task ServeIndexAsync(HttpContext ctx, string webRoot)
    {
        var indexPath = Path.Combine(webRoot, IndexFile);
        if (!File.Exists(indexPath))
        {
            FileLog.Write($"[CockpitReactApp] index.html missing under {webRoot}");
            await WriteNotFoundAsync(ctx, "React Cockpit index.html missing from this Gateway build.");
            return;
        }

        // The shell carries no secret, but it still must revalidate so an updated app is picked up.
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.SendFileAsync(indexPath);
    }

    private static async Task WriteNotFoundAsync(HttpContext ctx, string message)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        await ctx.Response.WriteAsync(message);
    }
}
