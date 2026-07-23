using CcDirector.Core.Browsers;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Loopback Control-API surface for DevThrottle's automation browsers (the drivable, signed-in-once
/// Chromium instances an agent attaches to via browser-harness). The Python <c>cc-devthrottle browser</c>
/// verbs call these, and later the desktop rail will read the SAME endpoints - so the fold (status label,
/// account, attach environment) is computed HERE, once, and every client renders it verbatim.
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
        // GET /browsers - every browser on THIS machine, each with its folded status + account + attach env.
        app.MapGet("/browsers", async (CancellationToken ct) =>
        {
            var browsers = AutomationBrowserRegistry.Load();
            var dtos = new List<BrowserDto>(browsers.Count);
            foreach (var b in browsers)
                dtos.Add(await FoldAsync(b, ct).ConfigureAwait(false));
            return Results.Json(new { browsers = dtos });
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
                var dto = await FoldAsync(created, ct).ConfigureAwait(false);
                return Results.Json(dto, statusCode: StatusCodes.Status201Created);
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
                return Results.Json(await FoldAsync(launched, ct).ConfigureAwait(false));
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
                    return Results.Json(await FoldAsync(confirmed, ct).ConfigureAwait(false));
                }

                var opened = await AutomationBrowserService.SignInAsync(b.Id, ct).ConfigureAwait(false);
                return Results.Json(await FoldAsync(opened, ct).ConfigureAwait(false));
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
                    return Results.Json(await FoldAsync(renamed, ct).ConfigureAwait(false));
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

    // --- folding + helpers ---

    /// <summary>Compute the finished view of a browser: live status, human status label, and the account
    /// it is signed in as (read from its own Local State). Clients render these verbatim.</summary>
    private static async Task<BrowserDto> FoldAsync(AutomationBrowser b, CancellationToken ct)
    {
        var status = await AutomationBrowserService.StatusAsync(b, ct).ConfigureAwait(false);
        var attach = AutomationBrowserRegistry.AttachInfoFor(b);
        string? account = null;
        try { account = AutomationBrowserService.ReadAccount(b); }
        catch (Exception ex) { FileLog.Write($"[BrowserEndpoints] ReadAccount id={b.Id} failed (non-fatal): {ex.Message}"); }

        return new BrowserDto
        {
            Id = b.Id,
            Name = b.Name,
            Browser = b.Kind.ToString(),
            Port = b.Port,
            Status = status.ToString(),
            StatusLabel = StatusLabel(status),
            Account = account,
            BuName = attach.BuName,
            BuCdpUrl = attach.BuCdpUrl,
            UserDataDir = b.UserDataDir,
            CreatedUtc = b.CreatedUtc,
            LastSignedInUtc = b.LastSignedInUtc,
        };
    }

    private static string StatusLabel(AutomationBrowserStatus status) => status switch
    {
        AutomationBrowserStatus.Stopped => "Stopped",
        AutomationBrowserStatus.NeedsSignIn => "Needs sign-in",
        AutomationBrowserStatus.Ready => "Ready",
        _ => status.ToString(),
    };

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

    private sealed class BrowserDto
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Browser { get; init; } = "";
        public int Port { get; init; }
        public string Status { get; init; } = "";
        public string StatusLabel { get; init; } = "";
        public string? Account { get; init; }
        public string BuName { get; init; } = "";
        public string BuCdpUrl { get; init; } = "";
        public string UserDataDir { get; init; } = "";
        public DateTime CreatedUtc { get; init; }
        public DateTime? LastSignedInUtc { get; init; }
    }
}
