using System.Net;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CcDirector.Launcher;

/// <summary>
/// Hosts the Launcher's loopback REST API on 127.0.0.1:<see cref="Port"/>.
///
/// Endpoints (all require Bearer token except /healthz):
///   GET  /healthz         -> {ok, version, pid, uptimeS}
///   GET  /status          -> launcher info + director status + launched pids
///   GET  /apps            -> ?q=&amp;limit= -> the installed application catalogue
///   GET  /files           -> ?q=&amp;limit=&amp;timeoutMilliseconds= -> filename search across this machine
///   POST /launch          -> {path|app, args?, cwd?, headless?} -> {ok, pid}
///   POST /director/start  -> start installed Director
///   POST /director/stop   -> stop installed Director
///   POST /director/restart -> restart installed Director
///   POST /shutdown        -> quit the launcher
///
/// Discovery: writes {port, token, pid} to
///   %LOCALAPPDATA%/cc-director/config/launcher/launcher.json
/// on startup so an agent/CLI can find it.
/// </summary>
public sealed class LauncherHost : IAsyncDisposable
{
    private readonly int _port;
    private readonly LaunchService _launchService;
    private readonly DirectorSupervisor _directorSupervisor;
    private readonly AppCatalog _appCatalog;
    private readonly FileSearchService _fileSearch;
    private readonly Func<Task> _requestShutdownAsync;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly string _version;
    private readonly string _userInterfaceState;

    private WebApplication? _app;
    private string? _token;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public int Port => _port;

    public LauncherHost(int port, LaunchService launchService, DirectorSupervisor directorSupervisor, Func<Task> requestShutdownAsync, string version = "0.0.0",
        string userInterfaceState = "tray", AppCatalog? appCatalog = null, FileSearchService? fileSearch = null)
    {
        _port = port;
        _launchService = launchService ?? throw new ArgumentNullException(nameof(launchService));
        _directorSupervisor = directorSupervisor ?? throw new ArgumentNullException(nameof(directorSupervisor));
        // Both query services hold no state, so a caller that does not supply them gets its own. They are
        // constructor arguments at all so a test can substitute one rooted at a temporary directory instead of
        // searching the machine the test happens to run on.
        _appCatalog = appCatalog ?? new AppCatalog();
        _fileSearch = fileSearch ?? new FileSearchService();
        _requestShutdownAsync = requestShutdownAsync ?? throw new ArgumentNullException(nameof(requestShutdownAsync));
        _version = version;
        // "tray" (normal) or "degraded" (headless fallback: the user-interface platform could
        // not initialize - e.g. locked screen at startup on macOS - so there is no menu-bar
        // icon; everything else runs). Surfaced on /healthz and /status so the Gateway can
        // see a launcher running without its icon.
        _userInterfaceState = userInterfaceState;
    }

    /// <summary>Start Kestrel, load/generate the token, write the discovery file.</summary>
    public async Task StartAsync()
    {
        FileLog.Write($"[LauncherHost] StartAsync: port={_port}");

        _token = LauncherAuth.LoadOrCreateToken();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "CcDirector.Launcher",
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, _port));
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddRoutingCore();

        _app = builder.Build();

        // Access log + error envelope for mutating requests.
        _app.Use(async (ctx, next) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { await next(); }
            catch (Exception ex)
            {
                FileLog.Write($"[LauncherHost] pipeline exception: {ex}");
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    await ctx.Response.WriteAsync($"{{\"error\":\"{ex.Message}\"}}");
                }
            }
            finally
            {
                sw.Stop();
                var method = ctx.Request.Method;
                if (method is "POST" or "PUT" or "PATCH" or "DELETE" || ctx.Response.StatusCode >= 400)
                {
                    var client = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
                    FileLog.Write($"[LauncherHost] {method} {ctx.Request.Path} -> {ctx.Response.StatusCode} ({sw.ElapsedMilliseconds}ms) client={client}");
                }
            }
        });

        // Token auth middleware.
        var token = _token;
        _app.Use((ctx, next) => LauncherAuth.Run(ctx, token, next));

        _app.UseRouting();

        MapEndpoints(_app);

        await _app.StartAsync();

        WriteDiscoveryFile();

        FileLog.Write($"[LauncherHost] Kestrel listening on http://127.0.0.1:{_port} (loopback only)");
    }

    private void MapEndpoints(WebApplication app)
    {
        // GET /healthz - public, no auth.
        app.MapGet("/healthz", () =>
        {
            var uptimeS = (long)(DateTime.UtcNow - _startedAt).TotalSeconds;
            // autostartOk is part of health on purpose. A launcher that did not register itself is
            // running but NOT managed, and reporting plain "ok" for both states is what let an
            // unmanageable launcher pass for a healthy one on the machine where this was found.
            //
            // Read ONCE into locals: two reads of a mutable static can produce a response that
            // disagrees with itself (ok=true alongside a failure string).
            // Read Checked FIRST: RecordAutostartState writes failure and registered before setting it,
            // so a reader that sees Checked=true is guaranteed the other two are the same generation.
            var autostartChecked = LauncherCore.AutostartChecked;
            var autostartFailure = LauncherCore.AutostartFailure;
            var autostartRegistered = LauncherCore.AutostartRegistered;
            return Results.Json(new
            {
                ok = true,
                version = _version,
                pid = Environment.ProcessId,
                uptimeS,
                userInterface = _userInterfaceState,
                // Null, not true, until it has actually been decided - saying "ok" about a question
                // nobody has asked yet is the same class of lie this field exists to remove.
                // Registered is the fleet-visible fact: autostart turned off on purpose is not a
                // failure, but it is not "ok" either.
                autostartOk = autostartChecked ? autostartFailure is null && autostartRegistered : (bool?)null,
                autostartRegistered = autostartChecked ? autostartRegistered : (bool?)null,
                autostartFailure,
            }, JsonOpts);
        });

        // GET /status - launcher info + director running state + launched pids.
        app.MapGet("/status", () =>
        {
            var uptimeS = (long)(DateTime.UtcNow - _startedAt).TotalSeconds;
            var statusAutostartChecked = LauncherCore.AutostartChecked;
            var statusAutostartFailure = LauncherCore.AutostartFailure;
            var statusAutostartRegistered = LauncherCore.AutostartRegistered;
            return Results.Json(new
            {
                launcher = new
                {
                    pid = Environment.ProcessId,
                    port = _port,
                    version = _version,
                    uptimeS,
                    startedAtUtc = _startedAt,
                    userInterface = _userInterfaceState,
                    autostartOk = statusAutostartChecked
                        ? statusAutostartFailure is null && statusAutostartRegistered
                        : (bool?)null,
                    autostartRegistered = statusAutostartChecked ? statusAutostartRegistered : (bool?)null,
                    autostartFailure = statusAutostartFailure,
                },
                director = new
                {
                    running = _directorSupervisor.IsRunning,
                    exeExists = _directorSupervisor.DirectorExeExists,
                    exePath = _directorSupervisor.DirectorExePath,
                },
                launchedPids = _launchService.LaunchedPids,
            }, JsonOpts);
        });

        // POST /launch - launch arbitrary app with clean parentage.
        app.MapPost("/launch", async (HttpContext ctx) =>
        {
            LaunchRequestDto? dto;
            try
            {
                dto = await ctx.Request.ReadFromJsonAsync<LaunchRequestDto>(JsonOpts, ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync($"{{\"error\":\"invalid JSON: {ex.Message}\"}}");
                return;
            }

            // Resolve a path or an application name through the shared rule, so this route and the Gateway
            // command stream cannot disagree about what "start Chrome" means on this machine.
            var (resolvedPath, resolveError) = _appCatalog.ResolveLaunchPath(dto?.Path, dto?.App);
            if (resolveError is not null)
            {
                FileLog.Write($"[LauncherHost] POST /launch refused: {resolveError}");
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { error = resolveError }, JsonOpts);
                return;
            }

            var request = new LaunchRequest
            {
                Path = resolvedPath!,
                Args = dto!.Args,
                Cwd = dto.Cwd,
                Headless = dto.Headless,
            };

            var pid = _launchService.Launch(request, caller: $"POST /launch from {ctx.Connection.RemoteIpAddress}");
            await ctx.Response.WriteAsJsonAsync(new { ok = true, pid }, JsonOpts);
        });

        // GET /apps - the installed application catalogue, optionally filtered.
        app.MapGet("/apps", (HttpContext ctx) =>
        {
            var query = ctx.Request.Query["q"].ToString();
            _ = int.TryParse(ctx.Request.Query["limit"].ToString(), out var limit);
            FileLog.Write($"[LauncherHost] GET /apps: q={query}, limit={limit}");
            return Results.Json(_appCatalog.Search(query, limit), JsonOpts);
        });

        // GET /files - a filename search across this machine's drives.
        app.MapGet("/files", (HttpContext ctx) =>
        {
            var query = ctx.Request.Query["q"].ToString();
            _ = int.TryParse(ctx.Request.Query["limit"].ToString(), out var limit);
            _ = int.TryParse(ctx.Request.Query["timeoutMilliseconds"].ToString(), out var timeout);
            FileLog.Write($"[LauncherHost] GET /files: q={query}, limit={limit}, timeout={timeout}");

            // A search with no query would walk every drive to return an arbitrary first N files, which is a
            // long way to travel for a meaningless answer. Refuse it rather than serve it.
            if (string.IsNullOrWhiteSpace(query))
                return Results.Json(new { error = "q is required for a file search" }, JsonOpts, statusCode: 400);

            return Results.Json(_fileSearch.Search(query, limit, timeout, ctx.RequestAborted), JsonOpts);
        });

        // POST /director/start
        app.MapPost("/director/start", async (HttpContext ctx) =>
        {
            _directorSupervisor.Start();
            await ctx.Response.WriteAsJsonAsync(new { ok = true, action = "started" }, JsonOpts);
        });

        // POST /director/stop
        app.MapPost("/director/stop", async (HttpContext ctx) =>
        {
            await _directorSupervisor.StopAsync(ctx.RequestAborted);
            await ctx.Response.WriteAsJsonAsync(new { ok = true, action = "stopped" }, JsonOpts);
        });

        // POST /director/restart
        app.MapPost("/director/restart", async (HttpContext ctx) =>
        {
            await _directorSupervisor.RestartAsync(ctx.RequestAborted);
            await ctx.Response.WriteAsJsonAsync(new { ok = true, action = "restarted" }, JsonOpts);
        });

        // POST /shutdown - quit the launcher.
        app.MapPost("/shutdown", async (HttpContext ctx) =>
        {
            FileLog.Write("[LauncherHost] /shutdown requested");
            await ctx.Response.WriteAsJsonAsync(new { ok = true }, JsonOpts);
            _ = Task.Run(async () =>
            {
                await Task.Delay(100); // Let the response flush.
                await _requestShutdownAsync();
            });
        });
    }

    /// <summary>
    /// Write the discovery file so agents/CLIs can find the port and token.
    /// Path: %LOCALAPPDATA%/cc-director/config/launcher/launcher.json
    /// </summary>
    private void WriteDiscoveryFile()
    {
        try
        {
            var dir = CcStorage.ToolConfig("launcher");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "launcher.json");
            var json = JsonSerializer.Serialize(new
            {
                port = _port,
                token = _token,
                pid = Environment.ProcessId,
            }, JsonOpts);
            File.WriteAllText(path, json);
            FileLog.Write($"[LauncherHost] Discovery file written: {path}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherHost] WriteDiscoveryFile FAILED: {ex.Message}");
        }
    }

    /// <summary>Remove the discovery file on shutdown.</summary>
    private void DeleteDiscoveryFile()
    {
        try
        {
            var path = Path.Combine(CcStorage.ToolConfig("launcher"), "launcher.json");
            if (File.Exists(path)) File.Delete(path);
            FileLog.Write("[LauncherHost] Discovery file removed");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherHost] DeleteDiscoveryFile FAILED: {ex.Message}");
        }
    }

    /// <summary>Stop Kestrel and remove the discovery file. Safe to call multiple times.</summary>
    public async Task StopAsync()
    {
        if (_disposed) return;
        _disposed = true;
        FileLog.Write("[LauncherHost] StopAsync");

        DeleteDiscoveryFile();

        if (_app is not null)
        {
            try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await _app.StopAsync(cts.Token); }
            catch (Exception ex) { FileLog.Write($"[LauncherHost] StopAsync error: {ex.Message}"); }
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}

/// <summary>
/// The body for POST /launch. Exactly one of <see cref="Path"/> and <see cref="App"/> identifies what to
/// start: a path names it directly, an application name is resolved against the catalogue this machine
/// reports from GET /apps. A caller on another machine has no way to know local paths, which is the whole
/// reason the name form exists.
/// </summary>
internal sealed class LaunchRequestDto
{
    public string? Path { get; init; }

    /// <summary>An application display name from the catalogue, used when <see cref="Path"/> is absent.</summary>
    public string? App { get; init; }

    public string? Args { get; init; }
    public string? Cwd { get; init; }
    public bool Headless { get; init; }
}
