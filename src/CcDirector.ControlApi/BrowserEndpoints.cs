using CcDirector.Core.Browsers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Loopback Control-API surface for DevThrottle's automation browsers (the drivable, signed-in-once
/// Chromium instances an agent attaches to via browser-harness). The Python <c>cc-devthrottle browser</c>
/// verbs call these, and the Director's own rail and Settings tab render the SAME fold in-process - the
/// finished view (status label, dot color, subtitle, action, attach command) is computed once by
/// <see cref="AutomationBrowserViewFold"/> and every client renders it verbatim.
///
/// Machine-locality is enforced by construction: a browser's debug port is loopback and its data
/// directory is on THIS machine, so only the LOCAL Director (the one the CLI talks to over its own
/// CC_DIRECTOR_API) can start or drive it. There is deliberately no relay of these verbs to another
/// Director - a browser cannot be driven through a tunnel.
/// </summary>
internal static class BrowserEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // GET /browsers - every browser on THIS machine, each fully folded, plus whether
        // browser-harness itself is installed (the rail's dimmed "advertise" state keys off this).
        app.MapGet("/browsers", async (CancellationToken ct) =>
        {
            var views = await AutomationBrowserViewFold.ListAsync(ct).ConfigureAwait(false);
            return Results.Json(new
            {
                browsers = views,
                harnessInstalled = AutomationBrowserViewFold.IsHarnessInstalled(),
                harnessInstallUrl = AutomationBrowserViewFold.HarnessInstallUrl,
            });
        });

        // POST /browsers { name, browser } - register + provision a new browser (does not launch it).
        app.MapPost("/browsers", async (CreateBrowserRequest req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "name is required" });
            if (!TryParseKind(req.Browser, out var kind))
                return Results.BadRequest(new { error = $"unknown browser \"{req.Browser}\" - use chrome or edge" });

            try
            {
                var created = AutomationBrowserService.Create(req.Name, kind);
                var view = await AutomationBrowserViewFold.FoldAsync(created, ct).ConfigureAwait(false);
                return Results.Json(view, statusCode: StatusCodes.Status201Created);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST /browsers/{id}/start - launch if down (idempotent), wait until the port answers.
        app.MapPost("/browsers/{id}/start", async (string id, CancellationToken ct) =>
        {
            return await GuardedAsync(id, async b =>
            {
                var launched = await AutomationBrowserService.LaunchAsync(b.Id, ct).ConfigureAwait(false);
                return Results.Json(await AutomationBrowserViewFold.FoldAsync(launched, ct).ConfigureAwait(false));
            });
        });

        // POST /browsers/{id}/stop - close the browser cleanly (CDP Browser.close, with the tracked-pid
        // kill as the wedge safety net). A browser that is already down is a no-op.
        app.MapPost("/browsers/{id}/stop", async (string id, CancellationToken ct) =>
        {
            return await GuardedAsync(id, async b =>
            {
                await AutomationBrowserService.StopAsync(b.Id, ct).ConfigureAwait(false);
                return Results.Json(await AutomationBrowserViewFold.FoldAsync(b, ct).ConfigureAwait(false));
            });
        });

        // GET /browsers/{id}/attach - the BU_NAME / BU_CDP_URL the harness attaches with.
        app.MapGet("/browsers/{id}/attach", (string id) =>
        {
            return Guarded(id, b =>
            {
                var attach = AutomationBrowserRegistry.AttachInfoFor(b);
                return Results.Json(new { buName = attach.BuName, buCdpUrl = attach.BuCdpUrl });
            });
        });

        // POST /browsers/{id}/signin { done } - done:true records the human finished; otherwise launch +
        // open the account page for the human to sign in by hand (credentials are NEVER automated).
        app.MapPost("/browsers/{id}/signin", async (string id, SignInRequest? req, CancellationToken ct) =>
        {
            return await GuardedAsync(id, async b =>
            {
                if (req?.Done == true)
                {
                    var confirmed = AutomationBrowserService.MarkSignedIn(b.Id);
                    return Results.Json(await AutomationBrowserViewFold.FoldAsync(confirmed, ct).ConfigureAwait(false));
                }

                var opened = await AutomationBrowserService.SignInAsync(b.Id, ct).ConfigureAwait(false);
                return Results.Json(await AutomationBrowserViewFold.FoldAsync(opened, ct).ConfigureAwait(false));
            });
        });

        // POST /browsers/{id}/rename { name }
        app.MapPost("/browsers/{id}/rename", async (string id, RenameBrowserRequest req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "name is required" });

            return await GuardedAsync(id, async b =>
            {
                try
                {
                    var renamed = AutomationBrowserService.Rename(b.Id, req.Name);
                    return Results.Json(await AutomationBrowserViewFold.FoldAsync(renamed, ct).ConfigureAwait(false));
                }
                catch (InvalidOperationException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });
        });

        // DELETE /browsers/{id} - stop it, delete its folder, drop the entry.
        app.MapDelete("/browsers/{id}", async (string id, CancellationToken ct) =>
        {
            return await GuardedAsync(id, async b =>
            {
                try
                {
                    await AutomationBrowserService.RemoveAsync(b.Id, ct).ConfigureAwait(false);
                    return Results.Json(new { removed = true, id = b.Id, name = b.Name });
                }
                catch (IOException ex)
                {
                    return Results.Json(new { removed = false, error = ex.Message }, statusCode: StatusCodes.Status409Conflict);
                }
            });
        });
    }

    // --- helpers ---

    private static bool TryParseKind(string? value, out BrowserKind kind)
        => Enum.TryParse(value?.Trim(), ignoreCase: true, out kind);

    /// <summary>Resolve the browser or return a 404 naming it; run <paramref name="body"/> otherwise (sync).</summary>
    private static IResult Guarded(string id, Func<AutomationBrowser, IResult> body)
    {
        var browser = AutomationBrowserRegistry.Find(id);
        if (browser is null)
            return Results.Json(new { error = $"No automation browser \"{id}\" on this machine." }, statusCode: StatusCodes.Status404NotFound);
        return body(browser);
    }

    /// <summary>Async form of <see cref="Guarded"/>.</summary>
    private static async Task<IResult> GuardedAsync(string id, Func<AutomationBrowser, Task<IResult>> body)
    {
        var browser = AutomationBrowserRegistry.Find(id);
        if (browser is null)
            return Results.Json(new { error = $"No automation browser \"{id}\" on this machine." }, statusCode: StatusCodes.Status404NotFound);
        return await body(browser).ConfigureAwait(false);
    }

    private sealed record CreateBrowserRequest(string Name, string Browser);
    private sealed record RenameBrowserRequest(string Name);
    private sealed record SignInRequest(bool Done);
}
