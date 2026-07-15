using System.Net;
using CcDirector.Core.Configuration;
using CcDirector.Core.Fleet;
using CcDirector.Core.Sessions;
using CcDirector.Core.Wingman;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CcDirector.ControlApi;

/// <summary>
/// Hosts the Director's HTTP Control API on a stable, predictable port so the
/// URL is bookmarkable across restarts and reachable from Tailscale clients.
///
/// Binding:
///   - Listens on loopback (127.0.0.1) ONLY. The raw port is never on the LAN or the
///     Tailscale interface; remote access is exclusively via Tailscale Serve (HTTPS),
///     auto-provisioned per Director by the Gateway's TailscaleServeProvisioner.
///
/// Lifecycle:
///   - StartAsync() -> picks port via PortAllocator, starts Kestrel, writes instances/{guid}.json
///   - StopAsync()  -> deletes registration file, releases port state, stops Kestrel
/// </summary>
public sealed class ControlApiHost : IAsyncDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly RepositoryRegistry? _repositoryRegistry;
    private readonly string _version;
    private readonly Func<Task> _requestShutdownAsync;
    private readonly bool _useEphemeralPort;
    // Set at StartAsync when the fixed range [7879..7898] is genuinely exhausted and the production
    // loopback path falls back to an ephemeral loopback port (issue #697). Distinct from
    // _useEphemeralPort (the test/LAN seam): a fallback host still self-provisions Tailscale Serve.
    private bool _fellBackToEphemeral;
    // Not readonly: LAN addressing mode (issue #457) auto-enables auth at StartAsync, because
    // binding the Control API to the LAN without auth would expose it to the whole network.
    private bool _authEnabled;

    public string DirectorId { get; }
    public int Port { get; private set; }
    public bool AuthEnabled => _authEnabled;

    /// <summary>
    /// True once Kestrel has bound and <see cref="StartAsync"/> has completed successfully.
    /// False while starting AND after a start failure (e.g. all ports in [7879..7898] busy).
    /// The session-state services (badge tracking) run regardless -- see
    /// <see cref="StartSessionStateServices"/> -- so this specifically means "the REST/Control
    /// API and remote (Gateway/Cockpit/phone) access are up".
    /// </summary>
    public bool IsListening { get; private set; }

    /// <summary>
    /// Null while healthy; set to the failure reason when <see cref="StartAsync"/> could not
    /// bind the Control API (reported by the boundary that catches the exception via
    /// <see cref="ReportStartupFailure"/>). The desktop surfaces this as a loud sidebar
    /// indicator so a port-exhausted Director is never silently degraded.
    /// </summary>
    public string? StartupError { get; private set; }

    /// <summary>
    /// Fires whenever <see cref="IsListening"/> / <see cref="StartupError"/> change, so the UI
    /// can repaint its Control-API indicator. May fire on a background thread.
    /// </summary>
    public event Action? StartupStatusChanged;

    /// <summary>
    /// Record that the Control API failed to start. Called by the boundary that catches the
    /// <see cref="StartAsync"/> exception (App startup) -- StartAsync re-throws, so the host
    /// itself cannot set this from a success-returning path. Raises
    /// <see cref="StartupStatusChanged"/> so the UI surfaces the degraded state.
    /// </summary>
    public void ReportStartupFailure(string error)
    {
        FileLog.Write($"[ControlApiHost] ReportStartupFailure: {error}");
        IsListening = false;
        StartupError = error;
        StartupStatusChanged?.Invoke();
    }

    /// <summary>
    /// Per-session persistent JSONL log. Exposed so the Avalonia UI can persist
    /// rendered agent-view widgets to <c>agent-view.jsonl</c> alongside the raw
    /// stream and turn summaries we already write. Null until <see cref="StartAsync"/>.
    /// </summary>
    public Core.Storage.SessionLogManager? SessionLogManager => _sessionLogManager;

    private WebApplication? _app;
    private InstanceRegistration? _registration;
    private GatewayClient? _gatewayClient;
    // Issue #1176 (Phase 1a): the outbound push-stream client, running alongside _gatewayClient when
    // gateway.streamMode is on. Null when stream mode is off, so the Director behaves exactly as today.
    private GatewayStreamClient? _streamClient;
    private readonly SemaphoreSlim _gatewayReapplyLock = new(1, 1);

    /// <summary>
    /// Issue #335 test seam: pin the tailnet identity resolution for the session DTO
    /// mapper so unit tests can assert identity fields without requiring a live Tailscale
    /// daemon. Must be set before <see cref="StartAsync"/> if used; the resolver is
    /// captured at start time. Null (default) uses the real detection ladder.
    /// </summary>
    internal Func<CcDirector.Core.Network.TailnetEndpointResolution>? TailnetEndpointResolverOverride { get; set; }

    /// <summary>
    /// Issue #697 test seam: override the production fixed-range port allocation. Returns the port
    /// to bind on loopback, or null to simulate a genuinely exhausted range (so the host exercises
    /// the ephemeral fallback). Null (default) uses the real <see cref="PortAllocator"/>. Only
    /// consulted on the production loopback path; ignored for ephemeral/LAN hosts.
    /// </summary>
    internal Func<string, int?>? PortAllocationOverride { get; set; }

    /// <summary>
    /// Issue #697 test seam: when true, skip Tailscale Serve self-provisioning at start so a unit
    /// test exercising the production fallback path does not mutate the host machine's real serve
    /// table. Defaults to false (production self-provisions, per issue #197).
    /// </summary>
    internal bool SuppressServeProvisioning { get; set; }

    /// <summary>
    /// The one home of this Director's Gateway-connection truth. Host-owned so it survives
    /// GatewayClient replacement on settings changes; the desktop indicator subscribes to its
    /// Changed event and the Gateway tunnel marks itself connected/reconnecting in it.
    /// </summary>
    public GatewayConnectionMonitor GatewayMonitor { get; } = new();

    /// <summary>
    /// Snooze Length mission (Phase 3): the seam the desktop drives to record/clear a Gateway-owned
    /// snooze THROUGH the Gateway (so a desktop snooze gets the same timer the phone/cockpit get),
    /// instead of setting <c>Session.OnHold</c> in-process. Backed by the live <see cref="GatewayClient"/>
    /// (which already holds the resolved Gateway address + fleet token), so it reuses the Director's
    /// existing Gateway connection. Null while the Gateway is not configured (no client) - the desktop
    /// gates the Snooze button on <see cref="GatewayMonitor"/> being Connected anyway.
    /// </summary>
    public IGatewayHold? GatewayHold => _gatewayClient;

    /// <summary>
    /// Fetch the latest Gateway turn brief for a session - the desktop Wingman tab's source.
    /// Null when no Gateway is configured/connected or none stamped yet; the caller then shows
    /// the local explain instead.
    /// </summary>
    public Task<Gateway.Contracts.TurnBriefDto?> GetLatestTurnBriefAsync(string sessionId, CancellationToken ct = default)
        => _gatewayClient?.GetLatestTurnBriefAsync(sessionId, ct) ?? Task.FromResult<Gateway.Contracts.TurnBriefDto?>(null);

    /// <summary>
    /// Issue #1627: the FLEET-WIDE session roster - every session on every machine - as the desktop fleet
    /// map's source. Backed by the live <see cref="GatewayClient"/>, which already holds the resolved
    /// Gateway address and fleet token, so it reuses the Director's existing outbound Gateway connection.
    ///
    /// This is an outbound HTTP GET, NOT a tunnel call, and that is deliberate: the tunnel is push-only
    /// (the Director pushes its own sessions up; there is no verb on DirectorHub that returns a roster).
    /// "Tunnel-only" means the Gateway never DIALS the Director - it does not mean the Director stopped
    /// calling out. GatewayClient survives for exactly these on-demand outbound operations.
    ///
    /// The returned sessions arrive with the Gateway's own answers already stamped - SessionRole,
    /// EffectiveColor, StateLabel - because the Gateway folds them across the whole fleet before
    /// responding. The desktop READS those; it must not recompute them (only the Gateway can see a
    /// controller that lives on another machine).
    ///
    /// Null when no Gateway is configured - the caller shows "not connected" rather than an empty fleet,
    /// which would be a lie.
    /// </summary>
    public Task<List<Gateway.Contracts.SessionDto>>? ListFleetSessionsAsync(CancellationToken ct = default)
        => _gatewayClient?.ListFleetSessionsAsync(ct);

    private TurnSummaryCache? _turnSummaryCache;
    // Mission records (mission-as-first-class-unit-of-work): a durable, file-backed store with no runtime
    // dependencies, so it is ready from construction (unlike the caches wired up in StartAsync).
    private readonly Core.Sessions.MissionStore _missionStore = new();
    private SessionStatusWingman? _statusWingman;
    private ProactiveExplainService? _proactiveExplain;
    private TerminalStateDetector? _terminalStateDetector;
    private TransientErrorAutoResume? _transientErrorAutoResume;
    private TerminalSessionRecorder? _sessionRecorder;
    private Core.Storage.TurnReviewLogger? _turnReviewLogger;
    private Core.Sessions.SessionCurrentModelWatcher? _currentModelWatcher;
    private Core.Storage.ConversationIngestor? _conversationIngestor;
    private Core.Storage.SessionLogManager? _sessionLogManager;
    // Resolved lazily at request time: the scheduler is created AFTER the Control API host
    // (StartControlApi runs before StartScheduler), so we capture an accessor, not the instance.
    private readonly Func<Core.Scheduler.SchedulerService?>? _schedulerAccessor;
    private readonly string? _instancesDirectory;
    private bool _stopped;
    private bool _stateServicesStarted;

    /// <summary>
    /// Construct a Director Control API host.
    /// </summary>
    /// <param name="useEphemeralPort">
    /// If true, Kestrel picks a free port and we bind only to loopback (intended for tests).
    /// If false (production), PortAllocator picks a stable port in [7879..7898] and we bind to loopback (Tailscale Serve fronts it).
    /// </param>
    /// <param name="authEnabled">
    /// If true, bearer-token or cookie auth is required for all routes except /healthz/login/logout.
    /// If false (default), the Director is completely open. The Tailscale tailnet is the trust boundary.
    /// </param>
    public ControlApiHost(SessionManager sessionManager, string version, Func<Task> requestShutdownAsync, bool useEphemeralPort = false, bool authEnabled = false, RepositoryRegistry? repositoryRegistry = null, string? directorId = null, Func<Core.Scheduler.SchedulerService?>? schedulerAccessor = null, string? instancesDirectory = null)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _version = version ?? "0.0.0";
        _requestShutdownAsync = requestShutdownAsync ?? throw new ArgumentNullException(nameof(requestShutdownAsync));
        _useEphemeralPort = useEphemeralPort;
        _authEnabled = authEnabled;
        _repositoryRegistry = repositoryRegistry;
        _schedulerAccessor = schedulerAccessor;
        // Tests pass an isolated instances directory so test Directors never appear in a real
        // Gateway's discovery (and a real Director never appears in a test Gateway's).
        _instancesDirectory = instancesDirectory;

        // Production: persisted id (same across restarts so the Gateway recognizes us).
        // Tests: inject a fresh id per fixture so parallel runs don't collide on the
        // single instances/{id}.json file.
        DirectorId = directorId ?? (useEphemeralPort
            ? Guid.NewGuid().ToString()
            : DirectorIdStore.LoadOrCreate());
    }

    /// <summary>Start Kestrel and write the instance registration file. Returns the chosen port.</summary>
    public async Task<int> StartAsync()
    {
        FileLog.Write($"[ControlApiHost] StartAsync: directorId={DirectorId}, ephemeral={_useEphemeralPort}");

        // Start the session-state services FIRST, before any port allocation or Kestrel work.
        // They observe the SessionManager + terminal buffers only -- never the bound port -- so
        // they must come up even when the Control API fails to bind (e.g. every port in
        // [7879..7898] is taken by other Directors). Before this was hoisted out, PortAllocator
        // throwing aborted StartAsync before these started, freezing every session on its last
        // badge colour: a silent session could never flip to the red "needs you" state.
        StartSessionStateServices();

        // Issue #846: one-time session-number backfill at Director startup. By this point every
        // session the manager already tracks - sessions restored from persistence or carried over
        // from a pre-#820 build - is in place, so a single pass numbers any that still lack a
        // three-digit number (sessions created from now on are numbered at creation by
        // RaiseSessionCreated -> AssignSessionNumber). Like the state services above, this runs
        // BEFORE the port bind, so a Director whose Control API fails to bind still numbers its
        // existing sessions. The method itself logs per-session; the count is logged here.
        var backfilledAtStartup = _sessionManager.BackfillNumbers();
        FileLog.Write($"[ControlApiHost] Startup session-number backfill assigned {backfilledAtStartup} number(s)");

        // Load the gateway config FIRST: the addressing mode (issue #457) decides the bind
        // interface below, and it is reused for the GatewayClient + session DTO mapper.
        var gatewayConfig = Core.Configuration.GatewayConfig.Load();
        var addressingMode = gatewayConfig.AddressingMode;

        // LAN mode auto-enables auth. This began (issue #457) because LAN mode put the Control API on a
        // routable interface, which MUST be authenticated. The bind is now loopback in every mode, so this
        // no longer guards a routable interface - it is kept because a LAN-mode Director has required the
        // fleet token since #457, and quietly dropping auth on upgrade would WEAKEN a running install. It
        // costs a local caller nothing it was not already paying.
        if (addressingMode == Core.Configuration.AddressingMode.Lan && !_authEnabled)
        {
            _authEnabled = true;
            FileLog.Write("[ControlApiHost] LAN addressing mode: auth auto-enabled (Control API will require the fleet token)");
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "CcDirector.ControlApi",
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");

        if (_useEphemeralPort)
        {
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        }
        else
        {
            // Loopback ONLY, in EVERY addressing mode. The tunnel-only cut made the Director dial
            // OUT to the Gateway and be reached only down that stream, so no caller anywhere needs
            // this port from off-machine: the Gateway creates sessions and drains work lists over
            // the tunnel (SessionVerbClient / DirectorImplSessionDriver carry no HTTP client), and
            // the two-way verify handshake that used to dial back was deleted with its route.
            // Binding a routable interface would therefore open an inbound port with no user at
            // all - pure attack surface - and break the cut's invariant that the inbound port stays
            // CLOSED on every client machine. LAN addressing mode used to bind IPAddress.Any here
            // (issue #457) for a Gateway->Director dial that no longer exists; it no longer changes
            // the bind interface.
            int? allocated = PortAllocationOverride is not null
                ? PortAllocationOverride(DirectorId)
                : (PortAllocator.TryAllocate(DirectorId, out var p) ? p : (int?)null);

            if (allocated is int fixedPort)
            {
                Port = fixedPort;
                builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, fixedPort));
            }
            else
            {
                // Issue #697: the fixed range [7879..7898] is genuinely full. Rather than disable
                // the Control API (Remote/Gateway access off, no REST surface), fall back to an
                // ephemeral loopback port. The actual port is read back after Kestrel starts and is
                // advertised through the same channels (instances/{guid}.json, Gateway registration,
                // Tailscale Serve), so remote access keeps working - just on a non-fixed port.
                _fellBackToEphemeral = true;
                FileLog.Write($"[ControlApiHost] Fixed range {PortAllocator.PortRangeStart}..{PortAllocator.PortRangeEnd} exhausted; falling back to an ephemeral loopback port");
                builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
            }
        }

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddRoutingCore();

        _app = builder.Build();

        // Global exception envelope + access log (issue #212 L2). The Director Control API
        // previously logged only what each endpoint happened to mention, so many requests -
        // including state-changing ones - left no trace; the 2026-06-06 post-mortem had to
        // reconstruct who-called-what from indirect evidence. We now log every MUTATING
        // request (POST/PUT/PATCH/DELETE) and any request that errors (>=400), with method,
        // path, status, elapsed, and client. Successful GET/HEAD are skipped because the
        // Director is polled hard (GET /sessions every 2s per viewer) and would flood the log.
        _app.Use(async (ctx, next) =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { await next(); }
            catch (Exception ex)
            {
                FileLog.Write($"[ControlApiHost] pipeline exception: {ex}");
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    ctx.Response.ContentType = "text/plain; charset=utf-8";
                    await ctx.Response.WriteAsync($"{ex.GetType().Name}: {ex.Message}");
                }
            }
            finally
            {
                sw.Stop();
                var method = ctx.Request.Method;
                var isMutation = method is "POST" or "PUT" or "PATCH" or "DELETE";
                if (isMutation || ctx.Response.StatusCode >= 400)
                {
                    var client = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
                    FileLog.Write($"[ControlApiHost] {method} {ctx.Request.Path}{ctx.Request.QueryString} " +
                        $"-> {ctx.Response.StatusCode} ({sw.ElapsedMilliseconds}ms) client={client}");
                }
            }
        });

        if (_authEnabled)
        {
            // Accept the shared fleet token (gateway.token) when attached to a Gateway, so the
            // Gateway authenticates across machines in LAN mode (issue #457); else the local token.
            var token = DirectorAuth.ResolveAcceptedToken(gatewayConfig.Token);
            _app.Use((ctx, next) => DirectorAuth.Run(ctx, token, next));
        }

        // Enable WebSocket support for /dictate and any future streaming endpoints.
        _app.UseWebSockets(new Microsoft.AspNetCore.Builder.WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });
        _app.UseRouting();

        // NOTE: the per-session state-tracking services (status wingman, terminal-state
        // detector, recorders, loggers) are NOT started here. They are started up front by
        // StartSessionStateServices(), before any port allocation, so they survive a
        // Control-API bind failure. See that method for the rationale.

        // Turn briefing left the Director (issue #187, the Gateway Wingman end state):
        // the GATEWAY's warm-brain agent observes turn ends (doorbell/heartbeat, #186),
        // generates briefs, stores them, and stamps BriefingState/RailLine onto the
        // aggregated session view. The Director is dumb metal here.

        // gatewayConfig was loaded up front (the addressing mode set the bind interface);
        // reuse it for the served HTML's "Gateway" nav button and the GatewayClient.
        var gatewayUrl = gatewayConfig.IsEnabled ? gatewayConfig.Url : null;

        // Issue #335: tailnet identity resolver for session DTO population. The resolver is
        // captured once at start time and shared with the per-session Map helper (runs on
        // every /sessions request). Production uses the real detection ladder; tests can pin
        // a fixed endpoint via TailnetEndpointResolverOverride before calling StartAsync.
        Func<CcDirector.Core.Network.TailnetEndpointResolution> resolveTailnetEndpoint;
        if (TailnetEndpointResolverOverride is not null)
        {
            resolveTailnetEndpoint = TailnetEndpointResolverOverride;
        }
        else if (addressingMode == Core.Configuration.AddressingMode.Lan)
        {
            // LAN mode (issue #457): the session DTO's routable endpoint is this machine's LAN IP.
            var lanResolver = new CcDirector.Core.Network.LanIdentityResolver();
            resolveTailnetEndpoint = () => lanResolver.ResolveEndpoint(Port, gatewayConfig.TailnetEndpoint);
        }
        else
        {
            var identityResolver = new CcDirector.Core.Network.TailnetIdentityResolver();
            resolveTailnetEndpoint = () => identityResolver.ResolveEndpoint(Port, gatewayConfig.TailnetEndpoint);
        }

        // The fleet-relay endpoints (issue #705) read the _gatewayClient FIELD lazily via this
        // provider, so they always use the current client even after a settings-change rebuild
        // (the field is replaced, not this lambda). The client is built later in this method.
        // The fleet-message steward (messaging.steward) - one instance per Director, guarding this
        // Director's sessions' OUTGOING /fleet/* messages. Built from the Director's options, so it is
        // default-on-generous and config-tunable, and inert (Allow) when disabled.
        var messageSteward = new MessageSteward(_sessionManager.Options.MessageSteward);

        // Issue #1181, Task 3b: the desktop-side enforced dictation lock. Project the durable PENDING
        // delivery marker (issue #1188) from the shared dictation-uploads store into Session's send
        // path, so a human typing on the desktop - or hitting this Director's control API without the
        // authenticated delivery exemption - is refused while a dictation is inbound to that session.
        // Same rule the Gateway front door enforces, now also closed on the Director's own in-process
        // and control-API send paths. Idempotent to set on each start.
        Core.Sessions.Session.DictationLockCheck = id => Core.Sessions.DictationLockReader.IsSessionLocked(id);

        // Issue #1357: the signed-in DevThrottle user for a session's preamble. The account credential
        // lives on the Gateway (issue #651), so this reads GET /account/status (email + provider +
        // nickname) through a short-lived cache. GatewayConfig.Load is re-read each resolve so a settings
        // change is picked up without a restart. Standalone/no-Gateway resolves to null (line omitted).
        var signedInUserProvider = new Core.Account.SignedInUserProvider(Core.Configuration.GatewayConfig.Load);

        ControlEndpoints.Map(_app, _sessionManager, DirectorId, _version, _requestShutdownAsync, _authEnabled, _repositoryRegistry, _turnSummaryCache, gatewayUrl, _proactiveExplain, GatewayMonitor, resolveTailnetEndpoint, () => _gatewayClient, messageSteward, _missionStore, ct => signedInUserProvider.ResolveAsync(ct));

        // Gateway Cleanup mission: the Director floor's tunnel-bounce. An operator/launcher can force
        // this Director to re-establish its OUTBOUND tunnel without a full restart. Loopback floor route.
        _app.MapPost("/reconnect", async (HttpContext ctx) =>
        {
            var caller = Core.Network.LoopbackPeerResolver.Describe(ctx.Connection.RemotePort, ctx.Connection.LocalPort);
            FileLog.Write($"[ControlApiHost] POST reconnect requested caller={caller}");
            if (_streamClient is null)
                return Results.Json(new { accepted = false, reason = "tunnel not enabled" });
            await _streamClient.ReconnectAsync();
            return Results.Json(new { accepted = true });
        });
        // Gateway Cleanup mission (the cut): the browser-facing session reads, the terminal stream, and
        // dictation no longer register their own Director routes here - they ride the tunnel exclusively.
        // The usage/context/history/facts reads dispatch through the shared executors over the tunnel; the
        // live terminal producer moved to the up-stream (open-terminal-stream); dictation is now
        // client->Gateway audio; claude-transcripts and dispatch are dropped legacy. Only the Phase-4
        // config surface below (settings/agents/tools/workspaces/scheduler) stays on the loopback floor for
        // LOCAL access (the desktop app + cc-settings-api call it same-machine); remote config editing moves
        // to proper tunnel verbs in Phase 4.
        SettingsEndpoint.Map(_app, ReapplyGatewayAsync, () => Port);
        // /settings/agents (issue #584): full Settings-dialog Agents-tab parity over REST -
        // library CRUD/reorder/enable plus Detect, Quick check, resolved command line, and the
        // catalog, reusing the same Core services the Agents tab uses (one implementation).
        AgentsEndpoint.Map(_app, _sessionManager.Options);
        ToolsEndpoint.Map(_app);
        WorkspacesEndpoint.Map(_app);
        SchedulerEndpoint.Map(_app, _schedulerAccessor);
        await _app.StartAsync();

        if (_useEphemeralPort || _fellBackToEphemeral)
        {
            // The OS assigned the port (Listen(..., 0)); read it back from Kestrel's bound address.
            Port = ReadAssignedPort(_app)
                ?? throw new InvalidOperationException("Kestrel started but did not expose a bound address.");
        }

        // Issue #725: confirm the Control API actually ANSWERS on the bound port before claiming we
        // are listening. A Windows-excluded / http.sys-reserved port lets the bind appear to succeed
        // while the System process shadows the socket and 404s every request - the Director then
        // "looks up" but is silently dead. The PortAllocator now skips excluded ranges, so this is a
        // belt-and-braces guard: if the self-probe fails we say so LOUDLY (never a misleading
        // "listening" line) and release the reservation so a restart picks a different port.
        if (await SelfProbeControlApiAsync(Port))
        {
            FileLog.Write($"[ControlApiHost] Kestrel listening on http://127.0.0.1:{Port} (loopback only; the inbound port is closed - remote access is the outbound tunnel to the Gateway)");
        }
        else
        {
            FileLog.Write($"[ControlApiHost] SELF-PROBE FAILED: bound port {Port} does NOT answer its own /healthz. " +
                "The port is shadowed or reserved (a Windows TCP excluded range or an http.sys reservation), so the " +
                "Control API is unreachable on it. Releasing the reservation so a restart picks another port. " +
                "Diagnose with: netsh int ipv4 show excludedportrange protocol=tcp");
            try { PortAllocator.Release(DirectorId); } catch { /* best-effort */ }
        }

        // Let the SessionManager stamp CC_DIRECTOR_API / CC_DIRECTOR_ID into every session
        // it spawns from now on, so agents inside a session can call this Control API
        // (e.g. GET $CC_DIRECTOR_API/sessions/$CC_SESSION_ID to find themselves).
        _sessionManager.ControlApiBaseUrl = $"http://127.0.0.1:{Port}";
        _sessionManager.DirectorId = DirectorId;

        // Issue #1357: let the (synchronous, non-blocking) Pi launch path name the signed-in user from
        // the provider's cached snapshot. Warm the cache once now so the first Pi session started right
        // after boot already has it; failures inside ResolveAsync are swallowed (best-effort context).
        _sessionManager.SignedInUserAccessor = () => signedInUserProvider.CurrentSnapshot;
        _ = Task.Run(async () =>
        {
            try { await signedInUserProvider.ResolveAsync(CancellationToken.None); }
            catch (Exception ex) { FileLog.Write($"[ControlApiHost] signed-in user warm-up failed (best-effort): {ex.Message}"); }
        });

        // Issue #1292: the Gateway is the authority for the fleet-unique session number. Wire the
        // SessionManager to ask the CURRENT GatewayClient (read the FIELD lazily so a settings change
        // that replaces the client via ReapplyGatewayAsync is picked up without re-wiring). A null /
        // failed answer (Gateway disabled or unreachable) makes the Director fall back to a local
        // offline number. Release rides the same client when a session ends.
        _sessionManager.FleetNumberSource = (sessionId, ct) =>
            _gatewayClient?.AllocateSessionNumberAsync(sessionId.ToString(), ct) ?? Task.FromResult<int?>(null);
        _sessionManager.FleetNumberRelease = sessionId =>
            _gatewayClient?.ReleaseSessionNumber(sessionId.ToString());
        // Only ask the Gateway (asynchronously) when one is configured; otherwise number locally and
        // synchronously. Kept in step with the config in ReapplyGatewayAsync.
        _sessionManager.FleetNumberingActive = gatewayConfig.IsEnabled;

        _registration = new InstanceRegistration(DirectorId, Port, _version, _instancesDirectory);
        _registration.Register();

        // Gateway Cleanup mission (tunnel-only): the Director NO LONGER opens an inbound Tailscale Serve
        // front door on its control port. It dials OUT to the Gateway over the tunnel and is reached ONLY
        // down that stream, so there is nothing inbound to publish - the whole point of the cut is that the
        // inbound port stays CLOSED on every client machine. Any Serve mapping a previous build left for this
        // port is proactively torn down so an upgraded Director self-heals to closed. This runs in EVERY
        // addressing mode: a Director that was on tailscale mode when an older build published a mapping, and
        // has since been switched to LAN mode, must still have that stale inbound mapping torn down. Only
        // ephemeral-port hosts (tests, hosted agents) are skipped - they never published one to begin with,
        // and must not churn the serve table (the #179 lesson).
        if (!_useEphemeralPort && !SuppressServeProvisioning)
        {
            var portToClose = Port;
            _ = Task.Run(() =>
            {
                try { using var p = new TailscaleServeSelfProvisioner(portToClose); p.RemoveOwnMapping(); }
                catch (Exception ex) { FileLog.Write($"[ControlApiHost] tunnel-only Serve teardown failed (best-effort): {ex.Message}"); }
            });
        }

        // Phase 1: if gateway.url is configured, register with the Gateway over HTTP and
        // start the heartbeat. Disabled (no-op) when local-only. Reuses the config
        // loaded above for the HTML nav button.
        _gatewayClient = BuildGatewayClient(gatewayConfig);
        _gatewayClient.Start();
        // Issue #1176 (Phase 1a): additive push stream, alongside the heartbeat/doorbell floor.
        _streamClient = BuildStreamClient(gatewayConfig);
        _streamClient?.Start();
        WireDoorbellPush();

        IsListening = true;
        StartupError = null;
        StartupStatusChanged?.Invoke();
        return Port;
    }

    /// <summary>
    /// Start the per-session state-tracking services. Every service here observes the
    /// <see cref="SessionManager"/> and its sessions' terminal buffers only -- none of them
    /// touch Kestrel or the bound port -- so they run independently of whether the Control API
    /// binds.
    ///
    /// This is deliberately decoupled from the port bind. The desktop "needs you" badge is
    /// <see cref="Session.StatusColor"/>, written by <see cref="SessionStatusWingman"/> from the
    /// <see cref="ActivityState"/> that <see cref="TerminalStateDetector"/> drives (byte -> Working;
    /// <see cref="TerminalStateDetector.QuietThreshold"/> of silence -> WaitingForInput = red).
    /// Before these were hoisted out of the post-bind section of <see cref="StartAsync"/>, a
    /// port-allocation failure (e.g. every port in [7879..7898] busy from other Directors)
    /// aborted StartAsync before they started -- leaving every session frozen on its last colour,
    /// so a silent session could sit forever and never flip to red. Idempotent: safe to call
    /// again (StartAsync calls it once up front).
    /// </summary>
    internal void StartSessionStateServices()
    {
        if (_stateServicesStarted) return;
        _stateServicesStarted = true;

        // Phase 5: persistent JSONL log per session. Must start FIRST so brand-new
        // sessions have a writer attached before any events fire.
        _sessionLogManager = new Core.Storage.SessionLogManager(_sessionManager);
        _sessionLogManager.Start();

        // Phase 3: the SessionStatusWingman is the sole writer of each Session's
        // StatusColor. Must start BEFORE TurnSummaryCache so brand-new sessions are
        // already "green/session created" by the time anything else observes them.
        _statusWingman = new SessionStatusWingman(_sessionManager);
        _statusWingman.Start();

        // Start the Wingman's per-turn summary cache before mapping endpoints so
        // /sessions/{sid}/turn-summaries returns whatever is already cached. Summaries are
        // generated on demand (the voice/mobile views call GenerateForLatestTurnAsync).
        _turnSummaryCache = new TurnSummaryCache(_sessionManager, _sessionManager.Options);
        _turnSummaryCache.Start();

        // Proactive explain: for Wingman-enabled sessions, regenerate + cache the Opus briefing
        // at each decision-point turn-end so the phone reads it instantly on open. TEXT ONLY --
        // no auto-narration. The phone's voice mode invokes /tts on demand against the cached
        // briefing's spoken-version field.
        _proactiveExplain = new ProactiveExplainService(_sessionManager, _sessionManager.Options.ClaudePath, _turnSummaryCache);
        _proactiveExplain.Start();

        // Terminal-driven state: the detector's only rule is byte -> working, plus the idle
        // clock (time since the last ConPTY character). No footer/grid/LLM guesswork, and no
        // Claude Code hooks - the detector is the single authority for session state.
        _terminalStateDetector = new TerminalStateDetector(_sessionManager, driveState: true);
        _terminalStateDetector.Start();
        FileLog.Write("[ControlApiHost] Session state source: terminal (byte->working)");

        // Transient-error auto-resume (issue #476): content-aware detection of a TRANSIENT
        // Anthropic API server error in a Claude Code session, with an opt-in auto-continue loop.
        // Gated behind config.json "auto_resume.enabled" which DEFAULTS OFF, so this is inert
        // unless the user has explicitly turned it on (human decision on assumption A-3). Always
        // wired so the toggle takes effect without a Director restart; the scheduler re-reads the
        // live config each cycle.
        _transientErrorAutoResume = new TransientErrorAutoResume(_sessionManager);
        _transientErrorAutoResume.Start();

        // Always-on terminal recorder: logs every session's resolved grid (on change, with the
        // activity state) to build the ground-truth corpus for offline analysis/learning.
        // Turn detection itself is the trigger + LLM judge in TerminalStateDetector above - no
        // regex screen parsing. Observe-only, capped per session. On by default; set
        // CC_DIRECTOR_RECORD_SESSIONS=0 to disable. See docs/wingman/WINGMAN.md.
        if (Environment.GetEnvironmentVariable("CC_DIRECTOR_RECORD_SESSIONS") != "0")
        {
            _sessionRecorder = new TerminalSessionRecorder(_sessionManager);
            _sessionRecorder.Start();
        }

        // Per-turn review log: one record each time a session flips Working -> needs-you
        // (our detector's transition, no hooks). Terminal + what the Wingman said/did, 7-day
        // retention. See CcStorage.TurnReviewLogs().
        _turnReviewLogger = new Core.Storage.TurnReviewLogger(_sessionManager);
        _turnReviewLogger.Start();

        // The model producer (issue #1637): on the same turn-end trigger, ask each session's driver
        // what model the agent is currently using and stamp Session.CurrentModel, which rides the
        // snapshot/delta path to the Gateway (SessionDto.CurrentModel) for the model-usage
        // statistics. Turn-end-driven so the records read never runs per roster poll.
        _currentModelWatcher = new Core.Sessions.SessionCurrentModelWatcher(_sessionManager);
        _currentModelWatcher.Start();

        // The prompt record (issue #1551): on the same turn-end trigger, read each session's
        // conversation out of the agent's own transcript, join on where each prompt came from, and PUSH
        // it to the Gateway's log. The Director captures because it is the only thing that sees a prompt
        // or knows its origin; the Gateway stores because it is what the whole fleet reports to and what
        // moves to the server. The Director keeps no copy.
        _conversationIngestor = new Core.Storage.ConversationIngestor(
            _sessionManager, new GatewayPromptSink(() => _gatewayClient));
        _conversationIngestor.Start();
    }

    /// <summary>
    /// Construct the Gateway client. Tunnel-only: the Director dials OUT and is reached only down that
    /// stream, so there is no inbound endpoint to verify-before-advertise anymore. The old issue #197
    /// own-port probe was already dead once the Director stopped self-provisioning a serve front door
    /// (the wiring here was gated on a serve provisioner this Director never creates), so it is removed.
    /// </summary>
    private GatewayClient BuildGatewayClient(GatewayConfig gatewayConfig)
        => new(gatewayConfig, DirectorId, Port, _version, SnapshotSessionStates, GatewayMonitor);

    /// <summary>
    /// Issue #1176 (Phase 1a): build the push-stream client, or null when stream mode is off / no Gateway
    /// is configured. Its snapshot source is <see cref="SnapshotFullSessions"/>, which uses the SAME
    /// mapper as the local /sessions endpoint (review #6) so a pushed row equals a pulled row.
    /// </summary>
    private GatewayStreamClient? BuildStreamClient(GatewayConfig gatewayConfig)
    {
        // Gateway Cleanup mission (tunnel-only): the streamMode gate is GONE. If a Gateway is configured,
        // the Director dials the tunnel - it is the ONLY connection. Null only when local-only (no gateway.url).
        if (!gatewayConfig.IsEnabled) return null;
        // Issue #1177 (Phase 1): the stream client's down-channel dispatcher reuses the SAME in-process
        // SessionCommandExecutor the Control API endpoints call, so a command executed over the stream is
        // byte-for-byte identical to the same command over HTTP.
        return new GatewayStreamClient(gatewayConfig, DirectorId, _version, SnapshotFullSessions,
            // The services are read per-command (inside the lambda) so they reflect the fields once
            // StartAsync has initialized them - BuildStreamClient runs before _proactiveExplain /
            // _turnSummaryCache are set. Additive: verbs that need no service ignore it (issue #1177 inc 6).
            // Gateway Cleanup mission, Phase 0 (wave 3): also carry the Director version and the repository
            // registry so the director-level reads that stamp/read them (facts, handover, repos-list) serve the
            // same value over the tunnel that their REST route served.
            cmd => DispatchTunnelCommandAsync(cmd),
            // Gateway Cleanup mission, Phase 0 (up-stream): pass the SessionManager so the four connection-bound
            // stream verbs work - their terminal/file producers read session and file state from it and stream
            // frames up this same connection.
            sessionManager: _sessionManager,
            // Gateway Cleanup mission (tunnel-only): the tunnel drives the desktop connectivity light directly -
            // connected = green (a live stream IS the proven two-way link), reconnecting = yellow.
            monitor: GatewayMonitor);
    }

    /// <summary>
    /// The Director-side dispatcher for a command that arrived over the tunnel. Almost every verb runs through
    /// the shared <see cref="SessionCommandExecutor"/> so it is byte-identical to its HTTP route. The one
    /// exception is <c>shutdown</c> (Gateway Cleanup mission): stopping the whole Director is a HOST concern,
    /// not a session-command concern - it has no place in a session executor and needs the host's shutdown hook -
    /// so it is handled here. <c>POST /shutdown</c> stays on the loopback floor for the local launcher; this is
    /// the Gateway-initiated REMOTE stop (<c>DELETE /directors/{id}</c>) taking the same in-process self-shutdown
    /// path, fired-and-forgotten (like the REST route) so the Ok result flushes before Kestrel and the stream tear down.
    /// </summary>
    private Task<DirectorCommandResult> DispatchTunnelCommandAsync(DirectorCommand cmd)
    {
        if (string.Equals(cmd.Verb, "shutdown", StringComparison.Ordinal))
        {
            FileLog.Write("[ControlApiHost] tunnel 'shutdown' command received; self-shutting-down");
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                try { await _requestShutdownAsync(); }
                catch (Exception ex) { FileLog.Write($"[ControlApiHost] tunnel shutdown FAILED: {ex.Message}"); }
            });
            return Task.FromResult(DirectorCommandResult.Success());
        }

        return SessionCommandExecutor.DispatchAsync(_sessionManager, DirectorId, cmd,
            new SessionCommandServices { ProactiveExplain = _proactiveExplain, TurnSummaryCache = _turnSummaryCache, MissionStore = _missionStore, DirectorVersion = _version, Repositories = _repositoryRegistry, ReapplyGatewayAsync = ReapplyGatewayAsync });
    }

    /// <summary>
    /// Full per-session snapshot for the stream, built through the SAME <see cref="ControlEndpoints.Map"/>
    /// the local /sessions endpoint uses (issue #1176, review #6), so a pushed snapshot row is identical
    /// to what a pull would have returned for the same session.
    /// </summary>
    private List<SessionDto> SnapshotFullSessions()
        => _sessionManager.ListSessions().Select(s => ControlEndpoints.Map(s, DirectorId)).ToList();

    /// <summary>Per-session mechanical-state snapshot for the heartbeat body (issue #186).</summary>
    private List<SessionStateSnapshot> SnapshotSessionStates()
        => _sessionManager.ListSessions()
            .Select(s => new SessionStateSnapshot
            {
                SessionId = s.Id.ToString(),
                ActivityState = s.ActivityState.ToString(),
            })
            .ToList();

    /// <summary>
    /// Sessions whose session-exited event has already been announced (issue #330): a
    /// session can hit the exit moment twice - the process dying (ActivityState -> Exited)
    /// and the roster removal (OnSessionRemoved, e.g. a user closing an active session) -
    /// and the Gateway must hear session-exited exactly ONCE per session.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _exitAnnounced = new();

    /// <summary>
    /// Subscribe every session's activity-state change to the Gateway doorbell (issue #186).
    /// Issue #330 widens the same channel with the event vocabulary: session-created on
    /// roster add, session-exited once per session (state -> Exited or roster removal,
    /// whichever happens first), and prompt-detected on the detector's transition into a
    /// detected input-prompt state (WaitingForInput / WaitingForPerm - the flagged
    /// assumption on the issue: the existing detector signal, no prompt understanding).
    /// Subscribed ONCE per host; the handlers read the _gatewayClient FIELD so a settings
    /// change that replaces the client (ReapplyGatewayAsync) is picked up without
    /// re-subscribing. NotifySessionState is a no-op while disabled/unregistered.
    /// </summary>
    private void WireDoorbellPush()
    {
        void Attach(Core.Sessions.Session session)
        {
            session.OnActivityStateChanged += (_, newState) =>
            {
                var eventName = newState switch
                {
                    Core.Sessions.ActivityState.Exited when _exitAnnounced.TryAdd(session.Id, 0)
                        => DoorbellEvents.SessionExited,
                    Core.Sessions.ActivityState.WaitingForInput or Core.Sessions.ActivityState.WaitingForPerm
                        => DoorbellEvents.PromptDetected,
                    _ => null,
                };
                _gatewayClient?.NotifySessionState(session.Id.ToString(), newState.ToString(), eventName);
                // Issue #1176 (Phase 1a): push the changed session up the stream as a delta.
                _streamClient?.NotifyDelta(ControlEndpoints.Map(session, DirectorId));
            };
            // A hold change is a state change like any other, so it pushes like any other. Without this the
            // Director set the flag, answered the caller, and told the Gateway NOTHING: a hold toggle that
            // rode no activity change stayed invisible to every OTHER screen until the next 10-second
            // re-push - and because the Gateway derives each session's color and triage bucket from this
            // flag when it serves the roster, the session also sorted into the wrong bucket for that whole
            // window. Fires on every HoldState transition, including None <-> DeferredHold, which leaves
            // OnHold untouched but does change the label clients render.
            session.HoldStateChanged += _ =>
                _streamClient?.NotifyDelta(ControlEndpoints.Map(session, DirectorId));

            // Defect 14: the three colour inputs that were invisible to the Gateway until something ELSE
            // happened. The Gateway folds orange from IsTranscribing, orange from IsAutoExplaining, and
            // purple from IsBackgroundRunning - reading them off the pushed SessionDto - but nothing pushed
            // when they changed. Each raised its Director event to an empty room: OnIsBackgroundRunningChanged
            // and OnIsExplainingChanged had ZERO subscribers anywhere in the codebase, and
            // OnIsTranscribingChanged had exactly one (a desktop UI handler, which pushes nothing). So the
            // fact sat on the Session until some unrelated activity change happened to push a delta, or the
            // ten-second re-push came around - and those three colours lagged by up to that long.
            //
            // A colour input is a state change like any other, so it pushes like any other. This is the same
            // one-line shape as the hold push above, for the same reason.
            session.OnIsTranscribingChanged += _ =>
                _streamClient?.NotifyDelta(ControlEndpoints.Map(session, DirectorId));
            session.OnIsBackgroundRunningChanged += _ =>
                _streamClient?.NotifyDelta(ControlEndpoints.Map(session, DirectorId));
            session.OnIsExplainingChanged += _ =>
                _streamClient?.NotifyDelta(ControlEndpoints.Map(session, DirectorId));

            // THE GATE ON TWO OF THOSE THREE, and it was missing from the list above - which is the whole
            // trap in miniature. The comment right there says "the three colour inputs", and the fold reads
            // a FOURTH: yellow needs WingmanEnabled AND IsAutoExplaining, purple needs WingmanEnabled AND
            // IsBackgroundRunning (SessionOrdering.ResolveActivity). So a wingman-enabled=false command on a
            // session parked on its background task changes the right answer from purple "Background" to red
            // "Needs you" while NONE of the three above fire - nothing pushes, and the phone and Cockpit
            // keep the stale fold until the ten-second re-push.
            //
            // It hid because a gate is not the thing being rendered. Defect 14 went looking for "colour
            // inputs", found the flags, and did not count the condition guarding them. The rule that
            // actually holds: if the fold READS it, it pushes - no judgement about whether it feels like a
            // colour. Found by review of pull request 1598, after three earlier passes hunting exactly this.
            session.OnWingmanEnabledChanged += _ =>
                _streamClient?.NotifyDelta(ControlEndpoints.Map(session, DirectorId));
        }

        _sessionManager.OnSessionCreated += session =>
        {
            Attach(session);
            _gatewayClient?.NotifySessionState(session.Id.ToString(), session.ActivityState.ToString(),
                DoorbellEvents.SessionCreated);
            _streamClient?.NotifyDelta(ControlEndpoints.Map(session, DirectorId));
        };
        _sessionManager.OnSessionRemoved += session =>
        {
            if (_exitAnnounced.TryAdd(session.Id, 0))
                _gatewayClient?.NotifySessionState(session.Id.ToString(),
                    Core.Sessions.ActivityState.Exited.ToString(), DoorbellEvents.SessionExited);
            // Issue #1176 (Phase 1a): tombstone the removed session so the Gateway prunes it immediately.
            _streamClient?.NotifyRemove(session.Id.ToString());
            // The session is gone from the roster - drop the announce guard so the map
            // never grows past the live roster.
            _exitAnnounced.TryRemove(session.Id, out _);
        };
        foreach (var s in _sessionManager.ListSessions())
            Attach(s);
    }

    /// <summary>
    /// Re-read the gateway config from config.json and re-register the Director with the
    /// gateway, replacing the running <see cref="GatewayClient"/>. Called when PUT /settings
    /// (or the Settings UI) changes the gateway block, so a new gateway URL / advertised
    /// endpoint / token takes effect without restarting the app. Serialized so two concurrent
    /// settings writes can't leave two heartbeat timers running.
    /// </summary>
    public async Task ReapplyGatewayAsync()
    {
        await _gatewayReapplyLock.WaitAsync();
        try
        {
            FileLog.Write("[ControlApiHost] ReapplyGatewayAsync: reloading gateway config");
            if (_gatewayClient is not null)
            {
                // Stop the old heartbeat + unregister BEFORE building the new client, so we
                // never have two clients heartbeating for the same directorId.
                try { await _gatewayClient.StopAsync(); }
                catch (Exception ex) { FileLog.Write($"[ControlApiHost] ReapplyGateway stop error: {ex.Message}"); }
                _gatewayClient.Dispose();
                _gatewayClient = null;
            }
            // Issue #1176 (Phase 1a): tear down the old stream client before rebuilding, so a settings
            // change never leaves two streams pushing for the same directorId.
            if (_streamClient is not null)
            {
                try { await _streamClient.StopAsync(); }
                catch (Exception ex) { FileLog.Write($"[ControlApiHost] ReapplyGateway stream stop error: {ex.Message}"); }
                await _streamClient.DisposeAsync();
                _streamClient = null;
            }

            var gatewayConfig = GatewayConfig.Load();
            _gatewayClient = BuildGatewayClient(gatewayConfig);
            _gatewayClient.Start();
            _streamClient = BuildStreamClient(gatewayConfig);
            _streamClient?.Start();
            // Issue #1292: keep the async-vs-local numbering decision in step with the new config.
            _sessionManager.FleetNumberingActive = gatewayConfig.IsEnabled;
        }
        finally
        {
            _gatewayReapplyLock.Release();
        }
    }

    private static int? ReadAssignedPort(WebApplication app)
    {
        var server = app.Services.GetService<IServer>();
        var addresses = server?.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null) return null;
        foreach (var addr in addresses)
            if (Uri.TryCreate(addr, UriKind.Absolute, out var uri))
                return uri.Port;
        return null;
    }

    /// <summary>
    /// Issue #725: prove the Control API actually serves on the just-bound port by calling its own
    /// <c>/healthz</c> over loopback. Returns true only when /healthz answers 2xx AND the body
    /// identifies THIS Director (its <see cref="DirectorId"/>), so a foreign service shadowing the
    /// port - the symptom of a Windows-reserved port - cannot pass the check. Bounded and best
    /// effort: any failure means "not serving" and returns false (never throws into startup).
    /// </summary>
    private async Task<bool> SelfProbeControlApiAsync(int port)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = await http.GetAsync($"http://127.0.0.1:{port}/healthz");
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync();
            return body.Contains(DirectorId, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ControlApiHost] SelfProbeControlApiAsync({port}) failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Stop Kestrel and delete the registration file. Safe to call multiple times.</summary>
    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        FileLog.Write($"[ControlApiHost] StopAsync");

        if (_gatewayClient is not null)
        {
            try { await _gatewayClient.StopAsync(); }
            catch (Exception ex) { FileLog.Write($"[ControlApiHost] GatewayClient.StopAsync error: {ex.Message}"); }
            _gatewayClient.Dispose();
            _gatewayClient = null;
        }
        // Issue #1176 (Phase 1a): stop the push stream on host shutdown.
        if (_streamClient is not null)
        {
            try { await _streamClient.StopAsync(); }
            catch (Exception ex) { FileLog.Write($"[ControlApiHost] GatewayStreamClient.StopAsync error: {ex.Message}"); }
            await _streamClient.DisposeAsync();
            _streamClient = null;
        }

        _terminalStateDetector?.Dispose();
        _terminalStateDetector = null;
        _transientErrorAutoResume?.Dispose();
        _transientErrorAutoResume = null;
        _turnReviewLogger?.Dispose();
        _turnReviewLogger = null;
        _currentModelWatcher?.Dispose();
        _currentModelWatcher = null;
        _conversationIngestor?.Dispose();
        _conversationIngestor = null;
        _sessionRecorder?.Dispose();
        _sessionRecorder = null;
        _proactiveExplain?.Dispose();
        _proactiveExplain = null;
        _turnSummaryCache?.Dispose();
        _turnSummaryCache = null;
        _statusWingman?.Dispose();
        _statusWingman = null;
        _sessionLogManager?.Dispose();
        _sessionLogManager = null;

        _registration?.Unregister();

        // Release the persisted port file only if we used a real allocated port
        if (!_useEphemeralPort && Port > 0)
        {
            try { PortAllocator.Release(DirectorId); } catch { }
        }

        if (_app is not null)
        {
            try { await _app.StopAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { FileLog.Write($"[ControlApiHost] StopAsync error: {ex.Message}"); }
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
