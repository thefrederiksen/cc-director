using System.Diagnostics;
using System.Net;
using System.Text.Json;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Tailscale;
using CcDirector.Gateway.Util;
using CcDirector.HostedAgent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR; // Issue #1176: ClientProxyExtensions.InvokeAsync (client results) for the down-channel
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CcDirector.Gateway;

/// <summary>
/// The Gateway's Kestrel host. One process per machine. Binds to 127.0.0.1:7878.
/// </summary>
public sealed class GatewayHost : IAsyncDisposable
{
    public const int DefaultPort = 7878;

    public int Port { get; }
    public string Token { get; }
    public DirectorRegistry Registry { get; }

    /// <summary>
    /// Issue #1292: the fleet-wide authority for the short three-digit session numbers. One instance for
    /// the whole Gateway, so a number names exactly one session across every Director on every machine.
    /// Directors ask it for a number at session creation (POST /session-numbers/allocate) and free it at
    /// session end; the /sessions aggregation adopts every observed number so the in-use set survives a
    /// Gateway restart.
    /// </summary>
    public Discovery.FleetSessionNumberAllocator SessionNumbers { get; } = new();

    /// <summary>
    /// Issue #1176 (Phase 1a): the Gateway's cache of session state pushed up by stream-connected
    /// Directors. The <c>/sessions</c> aggregation serves a Director from here (instead of pulling it)
    /// when that Director's stream is connected and fresh. Empty until Directors connect and push.
    /// </summary>
    public Streaming.PushedSessionStore PushedSessions { get; }

    /// <summary>
    /// DevThrottle Stats: the always-available aggregate of every session's input tally (turns + character
    /// volume by modality and surface). Fed by the director-stream hub from the pushed
    /// <see cref="Contracts.SessionDto.InputStats"/> and read by the private Gateway dashboard at
    /// <c>/stats</c> with no cloud round-trip.
    /// </summary>
    public Stats.GatewayInputStatsAggregator InputStats { get; }

    /// <summary>
    /// Issue #1215 (Cockpit plan phase 6): the last-known-good roster cache. The <c>/sessions</c>
    /// aggregation uses it so a single failed Director poll no longer drops that Director's sessions -
    /// they are served stale (Wobbly) through a short grace window and only dropped (Offline) once the
    /// grace window is exhausted. Presentation only; it never touches discovery or the registry constants.
    /// </summary>
    public Discovery.FleetRosterCache RosterCache { get; }

    /// <summary>
    /// launcher-persistent-join: the map of which machine's cc-launcher is currently joined over a
    /// persistent stream. When a launcher is stream-connected, the machine lifecycle relay pushes a command
    /// DOWN the open stream instead of dialing the launcher's REST API. Empty until launchers connect and
    /// only consulted when stream mode is on.
    /// </summary>
    public Streaming.LauncherConnectionRegistry LauncherConnections { get; }

    // Issue #1176 (Phase 1a): Gateway-side stream feature switch + staleness window, resolved from
    // config.json (or an explicit constructor override for tests). When off, the hub is not mapped and
    // /sessions never consults the pushed cache, so behaviour is byte-identical to today.
    private readonly bool _streamMode;
    private readonly TimeSpan _streamStaleAfter;

    public bool AuthEnabled { get; }

    /// <summary>
    /// Environment override for the host-wide auth gate (issue #917). As of Phase 1 the gate is ON by
    /// default, so this variable is now a DISABLE override for debugging: set <c>CC_GATEWAY_AUTH=0</c> to
    /// turn the gate off. (Setting it to <c>1</c> is a harmless no-op since enforcement is already the
    /// default.) A request that reaches the Gateway on the tailnet must present the shared token or a
    /// per-device key unless the gate is explicitly disabled.
    /// </summary>
    public const string AuthEnabledEnvVar = "CC_GATEWAY_AUTH";

    /// <summary>
    /// Environment override that turns the host-wide auth gate OFF for debugging (issue #917). Set
    /// <c>CC_GATEWAY_NO_AUTH=1</c> to disable enforcement on a Gateway that would otherwise enforce by
    /// default. This mirrors the <c>CC_GATEWAY_NO_TAILSCALE</c> env-toggle precedent.
    /// </summary>
    public const string AuthDisabledEnvVar = "CC_GATEWAY_NO_AUTH";

    /// <summary>
    /// Resolves whether the host-wide auth gate runs. As of issue #917 enforcement is ON by default:
    /// <list type="bullet">
    /// <item>An explicit constructor choice (<paramref name="explicitChoice"/> non-null) always wins - a
    /// test forces the gate on or off deterministically regardless of the environment.</item>
    /// <item>With no explicit choice (production), the gate is ON unless a disable override is set:
    /// <c>CC_GATEWAY_NO_AUTH=1</c> or <c>CC_GATEWAY_AUTH=0</c> turns it off for debugging.</item>
    /// </list>
    /// Pure and side-effect free so it is unit-tested directly.
    /// </summary>
    internal static bool ResolveAuthEnabled(bool? explicitChoice)
    {
        if (explicitChoice.HasValue)
            return explicitChoice.Value;

        if (string.Equals(Environment.GetEnvironmentVariable(AuthDisabledEnvVar), "1", StringComparison.Ordinal))
            return false;
        if (string.Equals(Environment.GetEnvironmentVariable(AuthEnabledEnvVar), "0", StringComparison.Ordinal))
            return false;

        return true;
    }

    /// <summary>
    /// Issue #469: mints and verifies the short-lived 4-digit pairing code that authorizes a new
    /// device to enroll. The GatewayApp host window drives this in-process (it mints the code,
    /// shows it locally, and polls the device registry for the join); the /devices/register
    /// endpoint verifies and consumes it. In-memory by design - a Gateway restart cancels any
    /// pending pairing.
    /// </summary>
    public Pairing.PairingCodeService Pairing { get; } = new();

    /// <summary>
    /// Issue #469: the registry of enrolled devices and their unique per-device keys - the single
    /// issuer and record of credentials in the per-device-key trust model. Persisted under the
    /// config root so issued keys survive a Gateway restart.
    /// </summary>
    public Pairing.DeviceRegistry Devices { get; }

    /// <summary>
    /// Issue #288: which Director last owned each session, so the per-session WS proxy can answer
    /// 503 (owner offline) instead of 404 (unknown session). Populated by the /sessions aggregator
    /// and the WS proxy; read by the WS proxy.
    /// </summary>
    public SessionOwnerCache SessionOwners { get; } = new();

    /// <summary>
    /// Issue #330: the per-director ring of received doorbell events (session-created /
    /// session-exited / prompt-detected) - the minimal Phase-1 observable sink, served at
    /// GET /directors/{id}/events. In-memory by design (resets on Gateway restart).
    /// </summary>
    public Events.DirectorEventLog DirectorEvents { get; } = new();

    /// <summary>
    /// Issue #376: the async voice-turn job cache (10-minute TTL). The submit endpoint creates
    /// jobs here, a background task mirrors the owning Director's SSE stage events into them,
    /// and the poll endpoint reads them - in-memory by design (a Gateway restart drops in-flight
    /// turns and the phone re-submits).
    /// </summary>
    public Voice.GatewayTurnJobStore TurnJobs { get; } = new();

    /// <summary>When this host was constructed - the Cockpit Settings page reads it for uptime.</summary>
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;

    /// <summary>
    /// Host-process-owned settings the Cockpit Settings page needs (run mode + autostart). The
    /// GatewayApp tray process sets this before <see cref="StartAsync"/>; null on hosts that have
    /// no tray (the dev console host), where the settings endpoint degrades gracefully.
    /// </summary>
    public Api.GatewaySettingsHooks? SettingsHooks { get; set; }

    /// <summary>
    /// Invoked when POST /shutdown is received (the self-update helper asking the running Gateway
    /// to exit so its exe unlocks). The hosting process decides how to exit: the tray app stops the
    /// host and shuts the Avalonia app down; the dev console host stops the generic host. When no
    /// handler is set the endpoint answers 501 - it never half-stops the host on its own.
    /// </summary>
    public Action? OnShutdownRequested { get; set; }

    /// <summary>
    /// Issue #331: registered cc-launcher processes. The relay endpoints use this to
    /// forward lifecycle verbs to the correct machine's launcher loopback REST API.
    /// </summary>
    public LauncherRegistry Launchers { get; } = new();

    /// <summary>
    /// Issue #636 (Gateway Centralization Phase 2 foundation): the Gateway-hosted DevThrottle credential
    /// service. Stores the access-plus-refresh token pair encrypted at rest under the Gateway config
    /// directory, answers "signed in?" locally with no network call, and reads the signed-in identity
    /// from the cached token. Reuses the Core <see cref="Core.Account.DevThrottleAccountService"/> as-is.
    /// On Windows it is backed by Windows Data Protection; on a non-Windows host (the operating-system
    /// credential store is Windows-only for now, per the issue's assumption) it is null and the Gateway
    /// holds no account credential until the macOS Keychain store is added.
    /// </summary>
    public Core.Account.DevThrottleAccountService? Account { get; }

    /// <summary>
    /// Issue #637 (Gateway Centralization Phase 2): the browser loopback sign-in flow relocated onto
    /// the Gateway. Built over <see cref="Account"/>, it opens the system browser at the configured
    /// sign-in address, captures the credential the sign-in completion hands back on the loopback
    /// callback, and stores it through the credential service. Null on a host with no credential
    /// service (a non-Windows host, where <see cref="Account"/> is null) - the tray then has nothing
    /// to prompt and stays inert. Tokens are never logged.
    /// </summary>
    public Account.GatewaySignInService? SignIn { get; }

    /// <summary>
    /// Issue #640 (Gateway Centralization Phase 2): the background token refresh service. Built over
    /// <see cref="Account"/>, it periodically renews the Gateway's access token when it has expired by
    /// exchanging the refresh token against the configured backend endpoint - in the background, never
    /// blocking startup or request handling. Null on a host with no credential service (a non-Windows host,
    /// where <see cref="Account"/> is null). Started in <see cref="StartAsync"/>, disposed in
    /// <see cref="StopAsync"/>. Tokens are never logged.
    /// </summary>
    private Account.GatewayTokenRefreshService? _tokenRefresh;

    /// <summary>
    /// Issue #857 (Gateway device registration): registers THIS Gateway as a device with the DevThrottle
    /// cloud account on sign-in and stores the cloud-issued per-device key locally. Built over
    /// <see cref="Account"/>, so it exists only when that service does (a non-Windows host has no
    /// credential service). Triggered immediately by the sign-in-completion hook on <see cref="SignIn"/>
    /// and, as the retry/first-launch safety net, by the heartbeat's first tick. Null on a host with no
    /// credential service. The per-device key is never logged (security rule DT-05).
    /// </summary>
    private Account.GatewayDeviceRegistrationService? _deviceRegistration;

    /// <summary>
    /// Issue #857: the Gateway's periodic last-seen heartbeat to the cloud (so the account device list's
    /// last-seen stays fresh), which also retries a registration that failed on sign-in. Built over
    /// <see cref="Account"/>; null on a host with no credential service. Started in <see cref="StartAsync"/>,
    /// disposed in <see cref="StopAsync"/>. Never blocks startup; a cloud failure only logs and retries.
    /// </summary>
    private Account.GatewayDeviceHeartbeatService? _deviceHeartbeat;

    /// <summary>
    /// Path B (device-gateway-topology.md Diagram 2b/2c): mirrors this Gateway's locally-paired children up
    /// to the cloud account roster on enrollment and reconciles them each sweep (drop children revoked on the
    /// account page, refresh child last-seen). Built over <see cref="Account"/>; null on a host with no
    /// credential service. Has no timer of its own - the enrollment endpoint fires its mirror-up and the
    /// device heartbeat sweep drives its reconcile. Tokens and per-device keys are never logged (DT-05).
    /// </summary>
    private Account.ChildDeviceMirrorService? _childMirror;

    private readonly DirectorEndpointClient _client;
    // The single resolve-then-create path for spawning a session on a target machine (cron + the
    // interactive POST /machines/{machine}/sessions relay). Built in the constructor, used by both.
    private readonly Running.MachineSessionSpawner _machineSessionSpawner;
    private readonly TailscaleServeProvisioner _serveProvisioner;
    private readonly GatewayTurnBriefStore _turnBriefStore;
    private readonly KeyVault _keyVault;
    // Issue #881: mints/ensures the DevThrottle inference key after sign-in and at startup. Null on a
    // host with no credential service (nothing to sign in to).
    private readonly Account.TranscriptionKeyAutoProvisioner? _transcriptionKeyProvisioner;
    private readonly WorkListStore _workLists;
    private readonly CronJobStore _cronJobs;
    private readonly CronRunHistoryStore _cronRuns;
    private readonly Running.CronEngine _cronEngine;
    // The cron firing sweep (epic #479, #483): wakes ~every minute and fires due jobs. Created in
    // StartAsync, disposed in StopAsync.
    private System.Threading.Timer? _cronTimer;
    private static readonly TimeSpan CronSweepInterval = TimeSpan.FromMinutes(1);

    // Scheduled-run auto-dismiss (issue #1200): wakes ~every 15s and closes automated runs that declared
    // themselves done, over the Director stream. Created in StartAsync only when stream mode is on (the
    // feature has no REST fallback), disposed in StopAsync. Freshness matches the aggregation's pushed-cache
    // staleness so a session whose Director stopped pushing is not acted on from a stale snapshot.
    private System.Threading.Timer? _autoDismissTimer;
    private Running.AutoDismissSweeper? _autoDismissSweeper;
    private static readonly TimeSpan AutoDismissSweepInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AutoDismissStaleAfter = TimeSpan.FromSeconds(30);
    private readonly Running.WorkListRunnerManager _runnerManager = new();
    // Issue #218: Gateway-owned clock for when each session entered the red / NEEDS-YOU state.
    private readonly NeedsYouClock _needsYouClock = new();
    // Car Mode (Car Mode mission): the server-side, per-device conversation context behind the fleet
    // tool-calling brain (POST /carmode/turn), so multi-turn references ("the latest one") resolve.
    // In-memory by design; one instance for the whole Gateway.
    private readonly CarMode.CarModeConversationStore _carModeConversations = new();
    // Car Mode (decision 3): the per-device store of a destructive action armed and awaiting the owner's
    // spoken confirmation, so a delete never runs without a clear spoken "confirm".
    private readonly CarMode.CarModePendingStore _carModePending = new();
    // Gateway-owned set of sessions whose dictated utterance is being transcribed in the background
    // (the phone released the Speak dialog and the audio is uploading/transcribing). Stamps the
    // orange "Transcribing..." roster color so nobody else grabs the session mid-dictation.
    private readonly Transcription.TranscribingSessions _transcribingSessions = new();
    // Issue #549: the always-on turn-brief stamping pipeline (GatewayTurnBriefAgent) is retired.
    // TurnEndWatcher stays and runs unconditionally - its only job now is firing voice
    // auto-refresh on turn-end for voice sessions, and clearing the stale voice/text cache on
    // the Working transition. The wingman brain (BrainSupervisor) is kept; voice mode uses it.
    private TurnEndWatcher? _turnEndWatcher;
    private Wingman.WingmanVoiceService? _voiceService;
    // Editable/versioned wingman instructions (issue #537); the voice translator reads the active set.
    private readonly Wingman.WingmanInstructionsStore _instructionsStore = new();
    // Shared training-data store: the voice service WRITES captures, the instructions A/B test READS them.
    private readonly Wingman.WingmanTrainingStore _trainingStore = new();
    private System.Threading.Timer? _voiceSweepTimer;
    // Durable dictation upload staging (issue #1006): the phone streams recorded audio here in chunks;
    // the Gateway assembles, transcribes, and injects the turn itself. Each upload id carries a durable
    // delivery record (issue #1183): PENDING chunks are retained until delivered/abandoned, and the
    // terminal tombstone de-dupes the upload id forever until the client acknowledges it - so there is no
    // age sweep for dictation staging (only the unrelated voice-turn staging is age-swept).
    private readonly Voice.VoiceUploadStore _dictationUploads = new(CcDirector.Core.Storage.CcStorage.DictationUploads());
    private AdvertisedEndpointMonitor? _endpointMonitor;
    // Issue #629: the durable, bounded, restart-surviving retry queue behind the login-telemetry
    // relay. Constructed here (loads any events a previous run left on disk), wired into the relay
    // endpoint, started flushing in StartAsync, and disposed in StopAsync.
    private readonly Api.TelemetryRetryQueue _telemetryQueue;
    // Web Push (mobile app-icon "needs you" dot): the VAPID key pair, the set of subscribed devices,
    // the loopback HTTP client the notifier reads /sessions with, and the background notifier itself.
    // The stores are constructed in the ctor (load-on-construct); the notifier is built and started in
    // StartAsync (once the loopback endpoint is live) and disposed in StopAsync.
    private readonly Push.WebPushVapidStore _vapidStore;
    private readonly Push.PushSubscriptionStore _pushSubscriptions;
    private readonly HttpClient _pushLoopbackHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private Push.WebPushNeedsYouNotifier? _pushNotifier;
    private WebApplication? _app;
    private bool _stopped;

    /// <param name="instancesDirectory">
    /// Override the Director-discovery instances directory (see <see cref="DirectorRegistry"/>).
    /// Tests pass an isolated temp directory; production omits it for the shared default.
    /// </param>
    /// <param name="turnBriefDirectory">
    /// Override the gateway turn-brief store directory (issue #185). Tests pass an isolated
    /// temp directory; production omits it for the shared default.
    /// </param>
    /// <param name="workListsPath">
    /// Override the named work-list store file (issue #301). Tests pass an isolated temp path;
    /// production omits it for the shared default at <c>%LOCALAPPDATA%\cc-director\worklists.json</c>
    /// (the keyvault.json precedent).
    /// </param>
    /// <param name="cronJobsPath">
    /// Override the cron-job store file (epic #479, #482). Tests pass an isolated temp path;
    /// production omits it for the shared default at <c>%LOCALAPPDATA%\cc-director\cronjobs.json</c>.
    /// </param>
    /// <param name="cronRunsPath">
    /// Override the cron run-history store file (epic #479, #483). Tests pass an isolated temp path;
    /// production omits it for the shared default at <c>%LOCALAPPDATA%\cc-director\cronruns.json</c>.
    /// </param>
    /// <param name="telemetryQueuePath">
    /// Override the durable telemetry retry-queue store file (issue #629). Tests pass an isolated temp
    /// path; production omits it for the shared default at
    /// <c>%LOCALAPPDATA%\cc-director\config\director\telemetry-queue.json</c>.
    /// </param>
    /// <param name="telemetryQueueMaxSize">
    /// Override the telemetry retry-queue bound (issue #629). Tests pass a small value to exercise
    /// eviction; production omits it for <see cref="Api.TelemetryRetryQueue.DefaultMaxSize"/>.
    /// </param>
    /// <param name="telemetryRetryInterval">
    /// Override how often the telemetry retry-queue flusher re-attempts delivery (issue #629). Tests
    /// pass a short interval; production omits it for a sensible default.
    /// </param>
    /// <param name="account">
    /// Override the Gateway-hosted DevThrottle credential service (issue #636). Tests pass a service
    /// over an in-memory or temp-directory store so they never touch the real Windows Data Protection
    /// store; production omits it so the host builds the Windows-backed service on Windows (and leaves
    /// <see cref="Account"/> null on a non-Windows host, where the operating-system credential store is
    /// not yet implemented).
    /// </param>
    public GatewayHost(int port = DefaultPort, string? token = null, bool? authEnabled = null, string? instancesDirectory = null, string? turnBriefDirectory = null, string? keyVaultPath = null, string? workListsPath = null, string? cronJobsPath = null, string? cronRunsPath = null, string? devicesPath = null, string? telemetryQueuePath = null, int? telemetryQueueMaxSize = null, TimeSpan? telemetryRetryInterval = null, Core.Account.DevThrottleAccountService? account = null, bool? streamMode = null, string? inputStatsPath = null)
    {
        Port = port;
        Token = token ?? GatewayAuth.LoadOrCreate();
        Registry = new DirectorRegistry(instancesDirectory);
        // Issue #1292: free a removed Director's session numbers so a Director that died without releasing
        // them does not leak the pool. OnDirectorRemoved fires on graceful unregister and on the registry's
        // own stale/unreachable sweep, so this never fires for a merely momentarily-unreachable Director.
        Registry.OnDirectorRemoved += directorId => SessionNumbers.ReleaseForDirector(directorId);
        PushedSessions = new Streaming.PushedSessionStore();
        InputStats = new Stats.GatewayInputStatsAggregator(inputStatsPath);
        RosterCache = new Discovery.FleetRosterCache();
        // Issue #1215: when a Director is unregistered or evicted from the registry, forget its cached
        // roster too so the cache does not grow without bound; a re-registering Director starts clean.
        Registry.OnDirectorRemoved += id => RosterCache.Forget(id);
        LauncherConnections = new Streaming.LauncherConnectionRegistry();
        var gatewayConfig = Core.Configuration.GatewayConfig.Load();
        _streamMode = streamMode ?? gatewayConfig.StreamMode;
        _streamStaleAfter = TimeSpan.FromSeconds(gatewayConfig.StreamStaleAfterSeconds);
        Devices = new Pairing.DeviceRegistry(devicesPath);
        AuthEnabled = ResolveAuthEnabled(authEnabled);
        if (AuthEnabled)
            FileLog.Write($"[GatewayHost] auth gate booted ON (enforced by default, issue #917 - a per-device key or the shared token is required, even on the tailnet; set {AuthDisabledEnvVar}=1 to disable for debugging)");
        else
            FileLog.Write($"[GatewayHost] auth gate booted OFF (disabled via override - requests are accepted without a credential; this is a debugging mode, not the shipped default)");
        _client = new DirectorEndpointClient(Token);
        _serveProvisioner = new TailscaleServeProvisioner(Registry, Port);

        // The Gateway's in-process warm brain (issue #184): supervisor only - the chosen
        // tool spawns on first use (the brief agent's first ask, or Settings' Restart Brain).
        // The tool and model are an EXPLICIT Gateway-level choice (issue #393, building on the
        // pinned-model #204): the wingman is the product's one always-on intelligence point,
        // so it runs the configured tool + model deliberately instead of a hardcoded claude.exe
        // and the account-default model. Both default to claude + opus when unset, so existing
        // fleets are unchanged. A config change applies on the next Gateway restart.
        BrainTool = BrainToolConfig.Get();
        BrainModel = BrainModelConfig.Get();
        var brainDriver = AgentDrivers.For(BrainTool);
        FileLog.Write($"[GatewayHost] brain tool: {BrainTool}, model: {BrainModel}");
        Brain = new BrainSupervisor(
            new HostedAgentOptions
            {
                WorkingDirectory = Path.Combine(CcStorage.Root(), "brain"),
                AgentArgs = $"{ClaudeDriver.DefaultArgs} --model {BrainModel}",
                Log = FileLog.Write,
            },
            // Host the chosen agent through its own driver. As of issue #510 the wingman agent is
            // chosen from the machine's registered agents (any AgentKind), since the driver-level
            // hostability work landed in issue #509; BrainToolConfig.Get validates the configured
            // name is a recognised AgentKind (default ClaudeCode).
            agentFactory: o => new CcDirector.HostedAgent.HostedAgent(o, brainDriver));
        _turnBriefStore = new GatewayTurnBriefStore(turnBriefDirectory);
        // Production omits keyVaultPath for the shared default; tests pass an isolated path so
        // they never touch the real %LOCALAPPDATA% key store.
        _keyVault = new KeyVault(keyVaultPath);
        // Named work lists persist across a Gateway restart (issue #301): one JSON file in the
        // Gateway data dir, loaded here (stale claims released) and written through on every
        // mutation. Tests MUST pass an isolated path so they never touch the real store.
        _workLists = new WorkListStore(workListsPath ?? Path.Combine(CcStorage.Root(), "worklists.json"));
        // Cron-job definitions persist across a Gateway restart (epic #479, #482): one JSON file in
        // the Gateway data dir, loaded here (next-run times recomputed) and written through on every
        // mutation - the WorkListStore precedent. Tests MUST pass an isolated path so they never
        // touch the real store.
        _cronJobs = new CronJobStore(cronJobsPath ?? Path.Combine(CcStorage.Root(), "cronjobs.json"));
        // Cron run history + the firing engine (epic #479, #483). The engine resolves each due job's
        // target Director from the registry and starts a session over the shared client (the same
        // path the work-list runner uses). The background sweep timer is started in StartAsync.
        _cronRuns = new CronRunHistoryStore(cronRunsPath ?? Path.Combine(CcStorage.Root(), "cronruns.json"));
        // The Gateway-hosted DevThrottle credential service (issue #636, Gateway Centralization Phase 2
        // foundation). Tests inject their own service over an isolated store; production builds the
        // Windows Data Protection-backed service rooted under the Gateway config directory. The
        // operating-system credential store is Windows-only for now (the issue's assumption), so on a
        // non-Windows host Account stays null until the macOS Keychain store is added - the platform
        // guard also satisfies the platform-compatibility analyzer. Resolved BEFORE the telemetry queue
        // so the queue can attach the Gateway's own account token when forwarding (issue #639).
        if (account is not null)
            Account = account;
        else if (OperatingSystem.IsWindows())
            Account = CcDirector.Gateway.Account.GatewayAccountFactory.CreateForWindows();
        else
            FileLog.Write("[GatewayHost] DevThrottle credential service not built: operating-system credential store is Windows-only for now");

        // Durable telemetry retry queue (issue #629): one JSON file under the Gateway config directory
        // (the DeviceRegistry / gateway-token precedent), loaded here so events a previous run left
        // undelivered survive a restart. A short-timeout forwarder client keeps a slow/unreachable
        // backend from holding a flush pass open. Tests pass an isolated path + a small bound + a short
        // retry interval so they never touch the real store and can exercise eviction quickly.
        // Issue #639: when the Gateway has a credential service the queue is wired with a Gateway token
        // source, so it attaches the GATEWAY's own account token at forward time (and holds events while
        // the Gateway is not signed in). On a host with no credential service the source stays null and
        // the queue keeps its Phase 1 behaviour (forward with the per-event stored bearer, unchanged).
        var telemetryForwardClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var gatewayTelemetryTokenSource = Account is not null
            ? new Api.GatewayAccountTelemetryTokenSource(Account)
            : null;
        _telemetryQueue = new Api.TelemetryRetryQueue(
            telemetryQueuePath ?? Path.Combine(CcStorage.Config(), "director", "telemetry-queue.json"),
            telemetryForwardClient,
            telemetryRetryInterval ?? TimeSpan.FromSeconds(30),
            telemetryQueueMaxSize ?? Api.TelemetryRetryQueue.DefaultMaxSize,
            gatewayTelemetryTokenSource);
        // A cron job targets a MACHINE (#503): resolve it to a Director at fire time, launching one
        // via the launcher (the shipped /machines/{m}/director/start relay, #331) if none is running.
        var cronTargetResolver = new Running.RegistryDirectorTargetResolver(
            () => Registry.ListDirectors(),
            new Running.RelayDirectorLauncher(Port, Token));
        // The single resolve-then-create path shared by the cron firing engine and the interactive
        // POST /machines/{machine}/sessions relay ("start a session on another computer").
        _machineSessionSpawner = new Running.MachineSessionSpawner(_client, cronTargetResolver);
        // A work-list cron job (#484) drains a named list via the shipped #274 runner on the resolved
        // Director, launching the drain in the background on the shared runner manager.
        var cronWorkListRunner = new Running.DirectorCronWorkListRunner(
            _workLists,
            cronTargetResolver,
            _runnerManager,
            new Running.DirectorWorkListDrainLauncher(_workLists, _client));
        // Run-complete notifications (issue #622, the deferred "notify on completion" piece of #479).
        // The notifier rides the EXISTING fleet channel - the per-Director doorbell event ring
        // (DirectorEvents, #330) observed at GET /directors/{id}/events - and optionally POSTs the same
        // payload to a per-job webhook. The deep link is built from the resolved Director's tailnet
        // endpoint (the same source the /sessions aggregation uses for ViewUrl); the gw query roots on
        // this Gateway's loopback base. The webhook client is short-timeout, best-effort.
        var cronNotifier = new Running.GatewayCronNotifier(
            DirectorEvents,
            directorId =>
            {
                var d = Registry.Get(directorId);
                return d is null ? null : (d.TailnetEndpoint ?? d.ControlEndpoint);
            },
            $"http://127.0.0.1:{Port}",
            new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
        _cronEngine = new Running.CronEngine(
            _cronJobs, _cronRuns, new Running.DirectorCronSessionStarter(_machineSessionSpawner),
            cronWorkListRunner, cronNotifier, new Running.SystemClock());

        // Web Push (mobile app-icon "needs you" dot): load (or generate on first run) the VAPID key
        // pair and the set of subscribed devices. The notifier that fans out to these is built and
        // started in StartAsync, once this Gateway's own /sessions endpoint is reachable on loopback.
        _vapidStore = new Push.WebPushVapidStore();
        _pushSubscriptions = new Push.PushSubscriptionStore();

        // Gateway device registration (issue #857): on sign-in (and as a first-launch/retry safety net on
        // the heartbeat) register THIS Gateway as a device with the cloud account and store the issued
        // per-device key locally. Built over the credential service, so it exists only when that service
        // does. Egress reuses the SAME cloud base (DEVTHROTTLE_API_URL) and the SAME forwarding token the
        // rest of the account egress uses; the device-registry client gets its own short-timeout HttpClient
        // (the AccountDevicesEndpoint precedent). The install id is resolved lazily (no disk I/O at
        // construction). The per-device key is never logged (security rule DT-05).
        if (Account is not null)
        {
            var deviceRegistryClient = new Core.Account.DeviceRegistryClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
            // Issue #1233 (following #1206): resolve THIS Gateway's own reachable front-door URLs as an
            // ORDERED LIST and publish the whole list (endpoint_urls) on register and every heartbeat, so a
            // joining machine can try them in order and use the first that answers rather than being stuck on
            // one address. Priority order (the reasoning in issue #1233):
            //   1. Machine name plus port (http://<MachineName>:<port>) - ALWAYS. The most stable and most
            //      direct name on a local network (it survives an IP change).
            //   2. The Tailscale front door - only when Tailscale is actually available on this machine. The
            //      reliable cross-network path.
            //   3. The local network IP plus port - the last-resort path (least stable; the IP can change).
            // So the list is three addresses when Tailscale is available and two when it is not, and every entry
            // is a real Gateway front door on this Gateway's own port. The Gateway applies NO operator endpoint
            // override here: gateway.tailnetEndpoint is the DIRECTOR's advertised-endpoint override and can
            // legitimately point at a Director on a different port (for example 7883). Feeding it into the
            // Gateway's published list ranked that Director URL FIRST, so item[0] was not reachable as a Gateway
            // at all (issue #1237). If a Gateway-specific operator override is ever wanted, add a NEW
            // gateway-only config key and pass it as overrideUrl - never reuse the Director's tailnetEndpoint.
            // The single endpoint_url (issue #1206) is the first list entry, so existing readers keep working.
            // Re-resolved on each call (never cached) so an address that appears after start heals within one
            // heartbeat cycle; the resolvers never throw (they report an unresolved address as unresolved).
            var tailnetResolver = new Core.Network.TailnetIdentityResolver();
            var lanResolver = new Core.Network.LanIdentityResolver();
            var gatewayPort = Port;
            Func<IReadOnlyList<string>> resolveEndpointUrls = () =>
            {
                // Do the I/O here (probe Tailscale and the LAN), then hand the resolved pieces to the pure
                // BuildOrderedEndpointUrls assembler so the ordering/dedup/loopback-skip logic is unit-tested
                // without a real network. Passing no config override to the resolvers yields the PURE
                // discovered address, and the assembler is given no override either (see #1237 above).
                var tailnet = tailnetResolver.ResolveEndpoint(gatewayPort, configOverride: null);
                var lan = lanResolver.ResolveEndpoint(gatewayPort, configOverride: null);
                return BuildOrderedEndpointUrls(
                    // No operator override: gateway.tailnetEndpoint is the Director's key, not the Gateway's (#1237).
                    overrideUrl: null,
                    Core.Network.TailscaleIdentity.BuildMachineNameUrl(Environment.MachineName, gatewayPort),
                    tailnet.IsResolved ? tailnet.Endpoint : null,
                    lan.IsResolved ? lan.Endpoint : null);
            };
            _deviceRegistration = new Account.GatewayDeviceRegistrationService(
                Account,
                deviceRegistryClient,
                new Account.GatewayDeviceKeyStore(),
                machineName: Environment.MachineName,
                platform: ResolvePlatform(),
                appVersion: AppVersion.Semver,
                endpointUrlsProvider: resolveEndpointUrls);
            _childMirror = new Account.ChildDeviceMirrorService(
                Account,
                deviceRegistryClient,
                Devices,
                appVersion: AppVersion.Semver);
            _deviceHeartbeat = new Account.GatewayDeviceHeartbeatService(
                _deviceRegistration,
                Account,
                deviceRegistryClient,
                appVersion: AppVersion.Semver,
                childMirror: _childMirror);
        }
        else
        {
            FileLog.Write("[GatewayHost] DevThrottle device registration not built: no credential service on this host");
        }

        // The browser loopback sign-in flow relocated onto the Gateway (issue #637). It is built over
        // the credential service, so it exists only when that service does - on a host with no
        // credential service (a non-Windows host) there is nothing to sign in to and the tray stays
        // inert. The reused Core coordinator opens the browser and captures the loopback hand-back.
        // Issue #857: on a successful sign-in it fires the device-registration hook (best-effort,
        // detached) so signing in registers this Gateway as a device.
        if (Account is not null)
        {
            // Issue #881: after sign-in, mint a DevThrottle inference key for the account and store it in
            // the vault so hosted transcription/TTS "just work" with zero configuration. The account JWT
            // authenticates the mint; a manual or already-minted vault key short-circuits it (manual
            // override + reuse across restarts, no key sprawl).
            _transcriptionKeyProvisioner = new Account.TranscriptionKeyAutoProvisioner(
                _keyVault,
                accessTokenProvider: Account.GetAccessTokenForForwarding,
                minter: new Account.AccountInferenceKeyProvisioner());

            SignIn = new Account.GatewaySignInService(
                Account,
                onSignedIn: async ct =>
                {
                    if (_deviceRegistration is not null)
                        await _deviceRegistration.EnsureRegisteredAsync(ct);
                    await _transcriptionKeyProvisioner.EnsureAsync(ct);
                });
        }
        else
            FileLog.Write("[GatewayHost] DevThrottle sign-in flow not built: no credential service on this host");

        // The Gateway-owned background token refresh (issue #640, Gateway Centralization Phase 2). Built
        // over the credential service, so it exists only when that service does - on a host with no
        // credential service there is no token to refresh. Constructed here; the timer is started in
        // StartAsync (so it never blocks construction) and disposed in StopAsync.
        if (Account is not null)
            _tokenRefresh = new Account.GatewayTokenRefreshService(Account);
        else
            FileLog.Write("[GatewayHost] DevThrottle token refresh not built: no credential service on this host");
    }

    /// <summary>
    /// Renders a request's query string for the access / exception log, REDACTING the query of the sign-in
    /// callback path (epic #1069, issue #1080). That path is the reachable front-door callback the cloud
    /// sign-in page redirects the browser back to, and it carries the handed-back access/refresh token in
    /// its query - so logging it verbatim (as every other request's query is logged) would write credential
    /// material to the gateway log, violating security rule DT-05. Every other path keeps its query
    /// unchanged so a remote-side problem stays traceable after the fact.
    /// </summary>
    private static string SafeQueryForLog(PathString path, QueryString query)
    {
        if (string.Equals(path.Value, Api.AccountSignInCallbackEndpoint.Path, StringComparison.OrdinalIgnoreCase))
            return query.HasValue ? "?[redacted: sign-in callback credential, DT-05]" : "";
        return query.Value ?? "";
    }

    /// <summary>
    /// Resolves this device's platform string for cloud device registration (issue #857): a short, stable
    /// operating-system label sent as the device's <c>platform</c>. The Gateway credential service is
    /// Windows-only today, so this is "windows" in practice, but the label is computed (not hard-coded) so
    /// it is correct should the credential store land on another platform.
    /// </summary>
    private static string ResolvePlatform()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "macos";
        if (OperatingSystem.IsLinux()) return "linux";
        return "unknown";
    }

    /// <summary>
    /// Assembles this Gateway's reachable front-door URLs into the ordered, de-duplicated list published as
    /// <c>endpoint_urls</c> (issue #1233). Pure and side-effect free (the caller does the probing and passes
    /// the resolved pieces), so the priority order and de-duplication are unit-tested without a real network.
    /// Order:
    /// <list type="number">
    /// <item>An explicit, non-loopback <paramref name="overrideUrl"/> (gateway.tailnetEndpoint) - the
    /// operator's hand-set reachable address - first; a loopback override is dropped (never advertised).</item>
    /// <item><paramref name="machineNameUrl"/> - the machine name plus port, always present, the most stable
    /// local-network name.</item>
    /// <item><paramref name="tailscaleUrl"/> - the Tailscale front door, only when it was resolved (null when
    /// Tailscale is unavailable), the reliable cross-network path.</item>
    /// <item><paramref name="lanUrl"/> - the local network IP plus port, only when it was resolved (null when
    /// no routable LAN IPv4 exists), the last-resort path.</item>
    /// </list>
    /// Blank entries are skipped and duplicates are collapsed case-insensitively (for example when the
    /// override equals the machine-name URL), so the list carries each distinct reachable address once.
    /// The operator's <paramref name="overrideUrl"/> is the one caller-supplied value, so it is additionally
    /// validated as a real http/https URL before being published (issue #334 contract: any non-http(s) or
    /// unparseable entry would make the account reject the WHOLE register/heartbeat with 400) - a malformed
    /// override is simply dropped, never sent, so it can never break device registration. The three
    /// discovered addresses are always well-formed by construction.
    /// </summary>
    internal static IReadOnlyList<string> BuildOrderedEndpointUrls(
        string? overrideUrl, string machineNameUrl, string? tailscaleUrl, string? lanUrl)
    {
        var urls = new List<string>();
        void Add(string? url)
        {
            if (!string.IsNullOrWhiteSpace(url) && !urls.Contains(url, StringComparer.OrdinalIgnoreCase))
                urls.Add(url);
        }

        // Only a valid, non-loopback http/https override is publishable. A loopback address is a lie to every
        // remote caller; a non-http(s) or unparseable string would 400 the whole request (issue #334).
        if (IsPublishableHttpUrl(overrideUrl) && !Core.Network.TailnetIdentityResolver.IsLoopback(overrideUrl))
            Add(overrideUrl);
        Add(machineNameUrl);
        Add(tailscaleUrl);
        Add(lanUrl);
        return urls;
    }

    /// <summary>
    /// True when <paramref name="url"/> is a publishable front-door address for the account endpoint_urls
    /// list (issue #334 contract): a non-blank, absolute http or https URL of at most 200 characters. Used to
    /// keep a malformed operator override out of the published list so it can never 400 the whole request.
    /// Pure - unit-tested.
    /// </summary>
    internal static bool IsPublishableHttpUrl([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 200)
            return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
    }

    /// <summary>
    /// One-time bootstrap of the central vault from the user environment. If the vault does not yet
    /// carry the DevThrottle account key, seed it from the environment (process, then User scope on
    /// Windows). Never clobbers an existing vault value.
    /// Key name matches <see cref="Core.Configuration.HostedAiKeyResolver.KeyName"/>.
    /// </summary>
    private void SeedKeyVaultFromEnvironment()
    {
        const string keyName = Core.Configuration.TranscriptionEndpointResolver.DevThrottleKeyName;
        var fromEnv = Environment.GetEnvironmentVariable(keyName);
        if (string.IsNullOrWhiteSpace(fromEnv) && OperatingSystem.IsWindows())
            fromEnv = Environment.GetEnvironmentVariable(keyName, EnvironmentVariableTarget.User);

        if (string.IsNullOrWhiteSpace(fromEnv))
        {
            FileLog.Write($"[GatewayHost] no {keyName} in the environment to seed the vault from");
            return;
        }

        var seeded = _keyVault.SetIfAbsent(keyName, fromEnv.Trim());
        FileLog.Write(seeded
            ? $"[GatewayHost] seeded vault {keyName} from the user environment (one-time bootstrap)"
            : $"[GatewayHost] vault already has {keyName}; left as-is (vault is the source of truth)");
    }

    /// <summary>
    /// Pre-build voice for voice sessions that are idle and missing it, so the session list shows
    /// them "voice ready" BEFORE the person enters - including after a gateway restart (the voice-
    /// session set is persisted). Gentle: at most a few per cycle, idle sessions only (a working
    /// session regenerates on its turn-end). Best-effort; never throws into the timer.
    /// </summary>
    private async Task SweepVoiceSessionsAsync()
    {
        var vs = _voiceService;
        if (vs is null) return;
        try
        {
            var directors = Registry.ListDirectors();
            if (directors.Count == 0) return;
            var generated = 0;
            foreach (var sid in vs.VoiceSessionIds())
            {
                if (generated >= 3) break;          // gentle on the serialized brain
                if (vs.HasVoice(sid)) continue;     // already cached, nothing to do
                foreach (var d in directors)
                {
                    var ep = (d.ControlEndpoint ?? d.TailnetEndpoint ?? "").TrimEnd('/');
                    if (string.IsNullOrWhiteSpace(ep)) continue;
                    var s = await _client.GetSessionAsync(ep, sid);
                    if (s is null) continue;        // not owned by this Director
                    var st = s.ActivityState ?? "";
                    if (st is "Idle" or "WaitingForInput" or "WaitingForPerm")
                    {
                        FileLog.Write($"[GatewayHost] voice sweep: pre-building voice for idle session {sid}");
                        // A pre-build is not a new turn - generate quietly so an idle session a client
                        // may be listening to is never flipped yellow mid-play (issue #1322).
                        await vs.GenerateAsync(sid, ep, CancellationToken.None, showReadingWindow: false);
                        generated++;
                    }
                    break;  // found the owning Director
                }
            }
        }
        catch (Exception ex) { FileLog.Write($"[GatewayHost] voice sweep error: {ex.Message}"); }
    }

    /// <summary>
    /// The Gateway's warm brain (issue #184): a claude.exe this process hosts itself - no
    /// Director dependency. Dormant until first use; RestartAsync is the recovery verb.
    /// </summary>
    public BrainSupervisor Brain { get; }

    /// <summary>The agent tool the brain runs as (issue #393), resolved at construction from
    /// config.json "brain_tool" (default: <see cref="BrainToolConfig.Default"/>, Claude Code).
    /// A config change applies on the next Gateway restart.</summary>
    public Core.Agents.AgentKind BrainTool { get; }

    /// <summary>The model the brain is pinned to (issue #204), resolved at construction
    /// from config.json "brain_model" (default: <see cref="BrainModelConfig.Default"/>).
    /// Recorded on every brief; a config change applies on the next Gateway restart.</summary>
    public string BrainModel { get; }

    /// <summary>Gateway-side turn-brief storage (issue #185): append-only, fleet-wide.</summary>
    public GatewayTurnBriefStore TurnBriefs => _turnBriefStore;

    /// <summary>
    /// Build the wingman's brain for the CURRENTLY selected AI provider and requested model role. The
    /// wingman is a stateless hosted chat-completions call, not the warm <c>claude.exe</c> brain,
    /// because that agent speaks a different protocol and cannot run these hosted models.
    /// The provider, credential, and role-specific model are read at CALL time, so a settings change is
    /// honored on the next turn without a Gateway restart.
    /// </summary>
    private Task<CcDirector.AgentBrain.IAgentBrain> WingmanBrainAsync(Core.Configuration.WingmanModelRole role, CancellationToken ct)
    {
        var mode = Core.Configuration.TranscriptionModeConfig.Get();
        var ep = Core.Configuration.TranscriptionEndpointResolver.ResolveWingman(mode);
        var key = _keyVault.Get(ep.KeyName) ?? "";
        var model = Core.Configuration.WingmanModelConfig.Resolve(mode, role);
        CcDirector.AgentBrain.IAgentBrain brain =
            new Wingman.HostedInferenceBrain(ep.BaseUrl, key, model, log: FileLog.Write);
        return Task.FromResult(brain);
    }

    public async Task StartAsync()
    {
        FileLog.Write($"[GatewayHost] StartAsync: port={Port}");

        // Seed the central vault from a DevThrottle account-key environment value once when present.
        // The vault is the live source of truth thereafter and SetIfAbsent never clobbers it.
        SeedKeyVaultFromEnvironment();

        // Issue #881: an install that was already signed in before this shipped won't fire the
        // post-sign-in hook again, so ensure the hosted transcription key here too - detached and
        // best-effort, so a mint call never delays or blocks startup. No-op when a key is already
        // stored or the host has no credential service.
        if (_transcriptionKeyProvisioner is not null)
            _ = Task.Run(async () =>
            {
                try { await _transcriptionKeyProvisioner.EnsureAsync(); }
                catch (Exception ex) { FileLog.Write($"[GatewayHost] startup transcription-key ensure failed (ignored, best-effort): {ex.Message}"); }
            });

        // Subscribe the Tailscale provisioner BEFORE Registry.Start() so the initial
        // file-discovery load fires OnDirectorAdded into it and every Director port
        // gets an HTTPS mapping without anyone re-running a script.
        _serveProvisioner.Start();
        Registry.Start();

        // Issue #331: start the stale-launcher sweep timer so launchers that crash
        // without unregistering are evicted after 90 s.
        Launchers.StartSweep();

        // Registry is now loaded with the current Director set: run the first self-healing
        // reconcile - re-assert the front door, drop serve mappings for Directors that died
        // while the Gateway was down (orphans -> 502 from a phone), and sweep any leaked
        // ephemeral-port mappings (issue #179). The provisioner repeats this on a timer.
        _serveProvisioner.Reconcile();

        // Issue #325: re-verify each HTTP-registered Director's advertised endpoint every
        // heartbeat cycle (15 s) - an advertised name that goes bad AFTER the registration-time
        // handshake (#223/#224) is flagged unreachable-by-name on the registration within two
        // cycles, and auto-clears when the name answers again.
        _endpointMonitor = new AdvertisedEndpointMonitor(Registry, _client);
        _endpointMonitor.Start();

        // Issue #549: the always-on turn-brief stamping pipeline is retired. TurnEndWatcher stays
        // and runs unconditionally - a small always-running watcher whose only job is firing voice
        // auto-refresh for voice sessions on turn-end, and clearing the stale voice/text cache on
        // the Working transition. It no longer depends on a brief agent existing. PUSH-fed since
        // #186 by Director doorbell pings and heartbeat snapshots (wired into the endpoints below);
        // the only pull left is the one-time startup catch-up sweep.
        FileLog.Write("[GatewayHost] StartAsync: starting the turn-end watcher (voice auto-refresh only; turn-brief pipeline retired in #549)");
        _voiceService ??= new Wingman.WingmanVoiceService(WingmanBrainAsync, _keyVault, _client, training: _trainingStore, instructionsProvider: () => _instructionsStore.ActiveContent);
        _turnEndWatcher = new TurnEndWatcher(
            Registry, _client,
            onTurnEnd: signal =>
            {
                // Voice sessions (issue #531): the turn just finished on its own, so re-make the
                // spoken summary + audio in the background. It is then "voice ready" in the session
                // list with no wait. Non-voice sessions do nothing here - the watcher is voice-only.
                if (_voiceService is { } vs && vs.IsVoiceSession(signal.SessionId))
                {
                    FileLog.Write($"[GatewayHost] turn-end -> voice auto-refresh: sid={signal.SessionId} newTurn={signal.IsNewTurn}");
                    // Show the yellow "wingman reading" hold only for a genuinely new turn; a startup
                    // catch-up of an earlier turn refreshes quietly so a listening client is not
                    // dropped out of the speaking screen (issue #1322).
                    _ = vs.GenerateAsync(signal.SessionId, signal.DirectorEndpoint, CancellationToken.None, showReadingWindow: signal.IsNewTurn);
                }
            },
            onSessionWorking: sid =>
            {
                // Working again: the cached voice/text summary is now stale - clear it so the list
                // stops showing it ready and nothing stale plays (issue #531). It regenerates on the
                // next turn-end.
                _voiceService?.OnSessionWorking(sid);
            });
        // First tick = the startup catch-up sweep; then the 15s reconcile poll for
        // Directors that never push (file-discovered locals, old builds).
        _turnEndWatcher.Start();

        // PreventHostingStartup avoids ASP.NET Core trying to load a (nonexistent) hosting startup
        // assembly with our application name, which otherwise emits a noisy crit log line on boot.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "CcDirector.Gateway",
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");

        builder.WebHost.ConfigureKestrel(o =>
        {
            // Bind to all interfaces so Tailscale clients can reach the dashboard.
            // Auth is required for every route except /healthz, /login, /logout.
            o.Listen(IPAddress.Any, Port);
        });

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddRoutingCore();
        // Direct forwarding for the per-Director request proxy (see DirectorForwarding).
        builder.Services.AddHttpForwarder();
        // Issue #806 (mobile foundation): emit an OpenAPI document at /openapi/v1.json. The mobile
        // app's build-time codegen (openapi-typescript) turns it into a typed TypeScript client, so
        // the C# DTOs stay the single source of truth for the front-end.
        builder.Services.AddOpenApi();

        // Issue #1176 (Phase 1a): the Director-push stream. The hub and its two collaborators are
        // registered as singletons so the hub (constructed per-invocation by SignalR's container) and the
        // /sessions aggregation (wired explicitly below) share the one PushedSessionStore instance.
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(PushedSessions);
        // DevThrottle Stats: the hub (constructed per-invocation by SignalR) folds each pushed session's
        // tally into this one aggregator instance, which the /stats dashboard reads.
        builder.Services.AddSingleton(InputStats);
        builder.Services.AddSingleton(Registry);
        // launcher-persistent-join: the LauncherHub (constructed per-invocation by SignalR) and
        // SendLauncherCommandAsync share this one connection registry.
        builder.Services.AddSingleton(LauncherConnections);

        // Honor X-Forwarded-Proto/Host/For from a Tailscale Serve front-end so
        // ctx.Request.Scheme reflects the public scheme the user actually used.
        // Without this, every request appears as plain "http" to the Gateway
        // (Tailscale terminates TLS at :443 and forwards plaintext to loopback),
        // and ViewUrl ends up with the wrong scheme on the phone.
        //
        // Trust only loopback as a forwarding proxy: anything else must not be
        // allowed to claim "I'm HTTPS" by spoofing the header.
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                               | ForwardedHeaders.XForwardedProto
                               | ForwardedHeaders.XForwardedHost;
            o.KnownProxies.Clear();
            o.KnownProxies.Add(IPAddress.Loopback);
            o.KnownProxies.Add(IPAddress.IPv6Loopback);
            o.KnownIPNetworks.Clear();
        });

        _app = builder.Build();

        _app.UseForwardedHeaders();

        // Access log + single top-level exception boundary. Every request leaves one
        // line (method, path, status, elapsed, client, host) so a phone-side problem is
        // traceable after the fact. Health polls and favicon are skipped to keep the log
        // focused on real traffic. RemoteIpAddress reflects X-Forwarded-For because
        // UseForwardedHeaders ran first, so a phone shows its tailnet IP.
        _app.Use(async (ctx, next) =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                // Log full detail server-side; return a generic body so we never leak
                // an exception type or message to a remote client.
                Console.Error.WriteLine($"[GatewayHost] pipeline exception: {ex}");
                FileLog.Write($"[GatewayHost] unhandled exception: {ctx.Request.Method} {ctx.Request.Path}{SafeQueryForLog(ctx.Request.Path, ctx.Request.QueryString)}: {ex}");
                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    await ctx.Response.WriteAsync("{\"error\":\"internal error\"}");
                }
            }
            finally
            {
                sw.Stop();
                var path = ctx.Request.Path.Value ?? "";
                if (!path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
                    && !path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)
                    // The React Cockpit's hashed static assets would flood the log.
                    && !path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
                {
                    var client = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
                    FileLog.Write($"[GatewayHost] {ctx.Request.Method} {path}{SafeQueryForLog(ctx.Request.Path, ctx.Request.QueryString)} -> {ctx.Response.StatusCode} ({sw.ElapsedMilliseconds}ms) client={client} host={ctx.Request.Host}");
                }
            }
        });

        if (AuthEnabled)
        {
            // Issue #469: a per-device key issued at enrollment is a valid Bearer credential
            // alongside the shared machine token, so an enrolled Director authenticates with its
            // own unique key. The shared token still authenticates the host's own browser/cookie
            // surface, but it is no longer the path a NEW device uses to get in (that is pairing).
            var requireToken = new AuthMiddleware.RequireToken { Token = Token, Devices = Devices };
            _app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, requireToken, next));
        }

        // Mobile front door (issue #806, docs/architecture/mobile/): a phone browser-navigation
        // (Accept: text/html, phone User-Agent) not already under /m gets a 302 to the mobile app
        // at /m/; a desktop UA falls through unchanged to the Cockpit. After auth, before the
        // Cockpit's browser-page routes - so a phone never reaches the Cockpit sitemap.
        Mobile.MobileRedirect.UseMobileRedirect(_app);

        // Browser-aware front door (the Cockpit sitemap): a PERSON navigating to /sessions,
        // /directors, or /cockpit (Accept: text/html) gets the React Cockpit shell; programs keep
        // getting JSON from the explicit endpoints below. After auth, before routing.
        Cockpit.CockpitReactApp.UseBrowserPageRoutes(_app);

        // Enable ASP.NET WebSocket support so the per-session proxy can recognize an inbound WS
        // upgrade (ctx.WebSockets.IsWebSocketRequest) and accept it (AcceptWebSocketAsync) for the
        // hand-rolled terminal/dictation stream proxy. The old YARP forwarder used the raw upgrade
        // feature and needed no middleware; the manual proxy (SessionWsForwarder) does. Pass-through
        // for upgrades it does not accept, so the YARP-forwarded Cockpit/Blazor circuit is unaffected.
        _app.UseWebSockets();

        _app.UseRouting();

        // Issue #1176 (Phase 1a): the Director-push stream endpoint, mapped only when stream mode is on
        // (kill-switch: off => the hub does not exist and behaviour is byte-identical to today). Mapped
        // after the host-wide auth middleware above, so the handshake is token-gated exactly like every
        // other route; a Director's .NET SignalR client presents its Bearer token on the handshake.
        if (_streamMode)
        {
            _app.MapHub<Streaming.DirectorHub>("/director-stream");
            FileLog.Write("[GatewayHost] stream mode ON: DirectorHub mapped at /director-stream; /sessions serves from the push cache when fresh");

            // launcher-persistent-join: the launcher-push stream endpoint, mapped under the SAME kill-switch
            // as DirectorHub. When a launcher joins, the machine lifecycle relay pushes commands DOWN this
            // stream instead of dialing the launcher's REST API; off => the hub does not exist and the relay
            // uses REST exactly as today.
            _app.MapHub<Streaming.LauncherHub>("/launcher-stream");
            FileLog.Write("[GatewayHost] stream mode ON: LauncherHub mapped at /launcher-stream; machine lifecycle relay prefers the stream when a launcher is joined");
        }

        // Product version stamped by Directory.Build.props; full form carries the commit SHA.
        var version = AppVersion.Full;
        GatewayEndpoints.Map(_app, Registry, _client, version, Token, AuthEnabled,
            requestShutdown: () =>
            {
                var handler = OnShutdownRequested;
                if (handler is null) return false;
                handler();
                return true;
            },
            // Issue #186: doorbell pings and heartbeat snapshots feed the turn tracker;
            // the aggregated /sessions view carries the Gateway-owned assessedState.
            onSessionState: (directorId, sessionId, newState) =>
            {
                // Any observed Working state means a new turn is in progress, so the cached voice/text
                // summary is stale - clear it (broad net for turns started outside the voice app, e.g.
                // the desktop cockpit). The voice-turn endpoint also clears deterministically on send.
                if (string.Equals(newState, "Working", StringComparison.OrdinalIgnoreCase))
                    _voiceService?.OnSessionWorking(sessionId);
                if (_turnEndWatcher is null) return;
                var endpoint = Registry.Get(directorId)?.ControlEndpoint;
                if (string.IsNullOrEmpty(endpoint)) return;
                _turnEndWatcher.Observe(sessionId, newState, endpoint);
            },
            // Issue #549: the assessed-state refutation (issue #186) is dropped with the pipeline
            // (Option A) - "needs you" reverts to the Director's raw mechanical signal. The
            // turn-brief stamping (issue #187 briefStampFor) is gone too; the brief agent that
            // wrote those fields no longer exists.
            // Voice mode (issue #531): while the gateway's wingman is producing a session's spoken
            // summary, paint it yellow ("not ready yet") and back to red. Independent of any brief
            // agent and never via the Director's --print explain.
            voiceGeneratingFor: sid => _voiceService?.IsGenerating(sid) == true,
            // Issue #553: whether the gateway has fetchable, playable cached audio for this session -
            // the single truthful "voice you can play right now" signal. Holds a voice-mode waiting
            // session yellow until this is true, then lets it go red (SessionOrdering.IsVoicePreparing).
            voiceAudioReadyFor: sid => _voiceService?.HasVoice(sid) == true,
            // Issue #939: when turn-end voice could not be kept because hosted AI is unavailable (out
            // of credits / cap / no key), stamp the shared unavailable state onto the session so the UI
            // shows the consistent add-credit / add-key message instead of a silently missing triangle.
            voiceUnavailableFor: sid => _voiceService?.VoiceUnavailableFor(sid),
            // Issue #218: stamp the Gateway-owned NeedsYouSince entry clock onto each session.
            needsYouStampFor: (sid, isRed) => _needsYouClock.Stamp(sid, isRed),
            // Stamp the orange "Transcribing..." flag while a dictated utterance is being uploaded
            // and transcribed in the background for this session (mobile Speak -> Send).
            transcribingFor: sid => _transcribingSessions.IsTranscribing(sid),
            // Issue #1181, Task 4: the honest phase label. "Transcribing" while the server is actively
            // turning the uploaded audio into text (a bounded run); otherwise "Uploading from phone" while
            // the durable PENDING delivery marker stands (the phone is still sending, and this never wedges
            // because the marker clears only on delivery/abandon); null when no dictation is inbound.
            dictationStatusFor: sid =>
                _transcribingSessions.IsActivelyTranscribing(sid) ? "Transcribing"
                : _dictationUploads.IsSessionLocked(sid) ? "Uploading from phone"
                : null,
            // The mobile Speak flow marks/clears this via POST /sessions/{sid}/transcribing.
            transcribingSessions: _transcribingSessions,
            // Issue #212 W3: enrich the Interrupted sessions list from the durable brief store. Always
            // available (read-only is safe even with briefing disabled), and the brief survives
            // the Director that died - which is exactly when we need it.
            interruptedBriefFor: sid =>
            {
                var b = _turnBriefStore.Latest(sid);
                return (b?.NeedsYou?.RailLine, b?.Headline);
            },
            // Issue #212 W4: the restore endpoint builds its continuation context from the
            // full brief history; the store outlives the dead Director, so this serves
            // sessions whose owner is gone.
            briefHistoryFor: sid => _turnBriefStore.List(sid),
            // Issue #288: record session->Director ownership as the fleet is aggregated, so the WS
            // proxy can return 503 (owner offline) rather than 404 for a session whose Director went dark.
            owners: SessionOwners,
            // Issue #330: doorbell event-vocabulary pings land in the per-director event ring.
            directorEvents: DirectorEvents,
            // Issue #376: async voice-turn submit/poll rides the host-owned job store.
            turnJobs: TurnJobs,
            // Issue #1045: pass the per-device-key registry so the voice-turn routes' own token
            // check (issue #369) accepts a phone's enrolled device key, not just the shared token.
            devices: Devices,
            // Issue #1176 (Phase 1a): serve /sessions from the Director-push cache when the stream is
            // fresh; null when stream mode is off, keeping /sessions byte-identical to today.
            pushedSessions: _streamMode ? PushedSessions : null,
            streamStaleAfter: _streamStaleAfter,
            // Issue #1177 (Phase 1): route per-session commands DOWN the Director's stream when stream mode
            // is on. Null when off, so every command endpoint stays on its HTTP path (byte-identical).
            sendCommand: _streamMode ? SendCommandAsync : null,
            // Issue #1215 (Cockpit plan phase 6): the last-known-good roster cache absorbs a transient poll
            // failure as Wobbly (served stale through a short grace window) instead of blinking the
            // Director's sessions out of the roster; only a sustained failure reads as Offline.
            rosterCache: RosterCache,
            // Issue #1292: the fleet-wide session-number authority backs POST /session-numbers/allocate
            // (Directors ask here at session creation) and the /sessions adopt-reconcile.
            sessionNumbers: SessionNumbers,
            // DevThrottle Stats: feed the input-tally aggregator from the assembled /sessions roster, so
            // "Your Throttle" is populated whether stream mode is on or off (the DirectorHub push fold only
            // runs in stream mode, which is off in production).
            inputStats: InputStats);

        // Issue #268: the two raw per-session WebSocket legs (live Terminal stream + dictation)
        // proxied through the Gateway so a remote Cockpit talks same-origin to the Gateway and
        // never needs a Director's own (possibly loopback) address. Mapped endpoints win over the
        // fallback Cockpit proxy below.
        // Pass the fleet token (issue #457): the proxy injects it as the Bearer on every forward
        // so an auth-enabled Director (LAN mode) accepts the call. Harmless for auth-off Directors.
        SessionWsProxyEndpoints.Map(_app, Registry, _client, SessionOwners, Token);

        // Issue #469: device enrollment via local pairing code (the ONLY way a new device gets in).
        // POST /devices/register verifies+consumes the 4-digit code and issues a unique per-device
        // key; GET /devices is the host-readable registry listing. Mapped after the WS proxy so its
        // literal routes win over the catch-all session forwarder, same as the other literal routes.
        Api.DeviceEnrollmentEndpoint.Map(_app, Pairing, Devices, _childMirror);

        // Wingman-voice surface for the Cockpit's Voice tab (issue #531): drive one turn of a
        // session and have the persistent wingman brain translate the reply into speakable form,
        // plus the direct-to-wingman path. Backed by the same warm Brain the brief agent uses.
        _voiceService ??= new Wingman.WingmanVoiceService(WingmanBrainAsync, _keyVault, _client, training: _trainingStore, instructionsProvider: () => _instructionsStore.ActiveContent);
        GatewayWingmanVoiceEndpoint.Map(_app, Registry, _client, WingmanBrainAsync, _keyVault, _voiceService, instructionsProvider: () => _instructionsStore.ActiveContent);

        // Car Mode brain (Car Mode mission, New build A): the fleet tool-calling loop behind
        // POST /carmode/turn. The chat transport resolves the fast wingman model + the vault key at CALL
        // time (a settings change applies on the next turn, no restart); the fleet tools reach THIS
        // Gateway's own endpoints over loopback (the same aggregated roster every client sees); the
        // conversation context is kept server-side per device. Inherits the host-wide auth gate (the
        // caller's per-device key), like every other data route.
        var carModeChat = new CarMode.HostedCarModeChat(CarMode.HostedCarModeChat.DefaultResolver(_keyVault.Get));
        var carModeFleet = new CarMode.LoopbackCarModeFleet(Port, Token);
        var carModeBrain = new CarMode.CarModeBrain(carModeChat, carModeFleet, _carModeConversations, _carModePending);
        Api.CarModeEndpoint.Map(_app, carModeBrain);
        // Editable/versioned wingman instructions settings surface (issue #537), incl. A/B test
        // over saved training sessions (reads the shared training store; uses the hosted wingman brain).
        WingmanInstructionsEndpoint.Map(_app, _instructionsStore, _trainingStore, WingmanBrainAsync);
        // The gateway OWNS keeping voice sessions' summaries pre-built (issue #531): a gentle
        // background sweep regenerates voice for any idle voice session that is missing it, so the
        // list shows it ready BEFORE you enter - including after a gateway restart (the voice-session
        // set is persisted). Turn-end regeneration + the deterministic voice-turn path also feed it.
        _voiceSweepTimer = new System.Threading.Timer(_ => { _ = SweepVoiceSessionsAsync(); }, null,
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(45));

        // Durable, server-owned dictation upload (issue #1006): the phone streams recorded audio here
        // in resumable chunks and the Gateway assembles → transcribes → injects the turn into the
        // owning session itself, so a refresh / dropped connection cannot lose a recorded utterance.
        GatewayDictationEndpoint.Map(_app, Registry, _client, SessionOwners, Token,
            new Transcription.GatewayTranscriptionService(_keyVault), _transcribingSessions, _dictationUploads, Devices);
        // Durable per-upload-id dictation record (issue #1183): a PENDING upload's chunks are retained
        // until it becomes DELIVERED or ABANDONED, and the delivered/abandoned tombstone (the durable
        // de-dupe marker) is retained until the client acknowledges it - so an undelivered dictation
        // survives time and a restart, and a delivered upload id is de-duplicated forever. There is
        // deliberately NO age sweep here: a fixed age cut would reopen exactly the hole this closes - a
        // phone out of signal for longer than the cut would lose its already-uploaded chunks, or re-inject
        // an already-delivered turn. The record is retired only by the client ack
        // (POST /dictation/{uploadId}/ack). The unrelated voice-turn upload staging keeps its own
        // transient SweepAbandoned; only the dictation record changed.

        // Central key vault (docs/architecture/gateway/GATEWAY_KEY_VAULT.md): set keys once
        // here (via the Cockpit Keys page); Directors pull them on demand. Inherits the
        // host-wide token middleware above.
        VaultEndpoints.Map(_app, _keyVault);

        // The AI model catalog + test surface for the Settings AI tab (list the selected provider's
        // models, test a chat model, save the chosen wingman/speech model). Uses the vault credential.
        Api.AiModelsEndpoint.Map(_app, _keyVault);

        // Gateway Centralization Phase 1 (issue #628): the inbound login-telemetry RELAY. The Director
        // POSTs its login-telemetry event here (instead of the cloud) and the Gateway forwards it on,
        // so the Gateway becomes the single egress. Best-effort: a backend failure is logged and the
        // caller still gets a non-5xx; the inbound access token is forwarded unchanged but NEVER logged.
        // Inherits the host-wide token middleware above (the existing gateway.token convention).
        // Issue #629: the relay enqueues every accepted event into the durable retry queue (which owns
        // delivery, retry-with-backoff, FIFO flush, the bound, and restart survival) instead of
        // forwarding inline. The flush loop is started just below in StartAsync.
        TelemetryRelayEndpoint.Map(_app, _telemetryQueue);

        // Gateway Centralization Phase 1 (issue #631): the inbound Director-STARTUP telemetry endpoint.
        // A Director POSTs a startup event here on launch (Director-side firing is issue #632); the
        // Gateway RECORDS it (a log line carrying director_id + app_version) so the startup is observable
        // Gateway-side, then BEST-EFFORT forwards it to the cloud ONLY when a startup endpoint is
        // configured (env DEVTHROTTLE_STARTUP_TELEMETRY_URL). The backend has no startup endpoint yet
        // (flagged dependency), so with no URL configured the event is recorded locally and the caller
        // still gets a 202 - no error. Forwarding reuses the SAME durable retry queue as the login relay
        // (issues #628 / #629), adding no new forwarder. Inherits the host-wide token middleware above.
        DirectorStartupTelemetryEndpoint.Map(_app, _telemetryQueue);

        // Gateway Centralization Phase 2 (issue #638): GET /account/status answers "is the Gateway
        // signed in to DevThrottle, and as whom?" computed ENTIRELY LOCALLY from the Gateway-hosted
        // credential service (issue #636, the reused DevThrottleAccountService exposed as Account) -
        // no cloud call. A Director's future startup gate (a separate issue) reads this. The response
        // carries only the boolean + identity, never the access/refresh token (security rule DT-05).
        // Inherits the host-wide token middleware above (the existing gateway.token convention). On a
        // host with no credential service (a non-Windows host, Account null) it truthfully reports
        // not-signed-in.
        AccountStatusEndpoint.Map(_app, Account);

        // Gateway Centralization Phase 3 (issue #648): POST /account/logout CLEARS the Gateway-hosted
        // DevThrottle credential through the same reused DevThrottleAccountService (Account). The account
        // lives on the gateway, so the logout action lives here too: the Cockpit account surface calls
        // it, and afterward GET /account/status reports signedIn=false and the gateway returns to its
        // sign-in prompt. The clear is entirely local (no cloud call) and the response carries only the
        // post-logout boolean, never the access/refresh token (security rule DT-05). Inherits the
        // host-wide token middleware above. On a host with no credential service (Account null) there is
        // nothing to clear and it reports not-signed-in.
        // Issue #881: revoke the auto-minted inference key on sign-out (before the credential is cleared),
        // so a signed-out install leaves no live key behind. A manually-pasted key has no recorded id and
        // is left untouched. Best-effort - never blocks logout.
        AccountLogoutEndpoint.Map(_app, Account,
            onBeforeLogout: _transcriptionKeyProvisioner is null ? null : ct => _transcriptionKeyProvisioner.RevokeMintedKeyAsync(ct));

        // Account device list + revoke proxy (issue #854): GET /account/devices and
        // DELETE /account/devices/{id}. The Cockpit Account page needs the account-wide device list with
        // last-seen and a per-device revoke, but the Cockpit must never hold the account token or call the
        // cloud directly - the token lives here on the Gateway. So the Gateway proxies: it reads its own
        // stored account token (the SAME GetAccessTokenForForwarding credential it uses to forward
        // telemetry/login to the cloud) and calls the cloud device registry through DeviceRegistryClient,
        // returning a local token-free DTO (security rule DT-05). Signed-out yields an explicit
        // signedIn:false envelope (never a fabricated empty list) and an unreachable cloud yields a clear
        // 502 (logged). The injectable HttpClient is the test seam. This is distinct from the LOCAL pairing
        // registry GET /devices (issue #469), which is left unchanged. Inherits the host-wide token
        // middleware above, exactly like the other /account routes.
        var accountDevicesClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        AccountDevicesEndpoint.Map(_app, Account, new Core.Account.DeviceRegistryClient(accountDevicesClient), Environment.MachineName);

        // Account credit-balance proxy (issue #884): GET /account/credits. Same proxy shape as the device
        // list - the Gateway reads the balance from the cloud with its own stored account token (JWT) and
        // returns a token-free DTO, so the Settings account section shows the balance without the Cockpit
        // ever holding the token. Signed-out -> explicit signedIn:false; unreachable cloud -> clear 502.
        AccountCreditsEndpoint.Map(_app, Account, new Core.Account.AccountCreditsClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }));

        // "DevThrottle emails me" relay (issue #1318 consumer): POST /account/email. A session or scheduled
        // run passes a subject + body (+ optional attachments); the Gateway injects its own stored account
        // token and forwards to the cloud primitive (POST /api/v1/account/notify-owner, devthrottle_internal
        // #338), which resolves the recipient from the token and sends via Resend. The Gateway holds NO
        // Resend key and runs no email code - it only relays the account's own token. Single-recipient by
        // construction (no recipient field). Signed-out -> 401; cloud failure -> clear 502. Inherits the
        // host-wide token middleware above like the other /account routes.
        AccountEmailEndpoint.Map(_app, Account, new Core.Account.AccountNotifyClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }));

        // Start the browser loopback sign-in from a web request (issue #853): POST /account/sign-in. The
        // Cockpit Account page's signed-out state needs a real "Sign in" action, but the loopback flow that
        // captures the credential lives here on the Gateway (issue #637, GatewaySignInService = SignIn). So
        // the Gateway exposes this trigger: the Cockpit POSTs here, the Gateway opens the system browser and
        // runs the hand-off in the background, and the Cockpit polls GET /account/status to see the result.
        // The captured token never leaves the Gateway (security rule DT-05). On a host with no sign-in flow
        // (SignIn null) it reports an explicit "not available" result. Inherits the host-wide token
        // middleware above, exactly like the other /account routes.
        AccountSignInEndpoint.Map(_app, SignIn);

        // The credential-free cloud sign-in START front door (epic #1069, issue #1076): GET + POST
        // /account/sign-in-start. Unlike POST /account/sign-in above, this pair is on the public-paths
        // allow-list (AuthMiddleware) so a SIGNED-OUT browser with no Gateway token can reach it to BEGIN
        // cloud sign-in - breaking the deadlock where cloud sign-in sat behind the raw gateway-token wall.
        // It reads/echoes no credential and returns no account data; the POST reuses the same host-local
        // browser loopback flow (SignIn) as the authenticated endpoint above (security rule DT-05). Every
        // other /account/* data endpoint stays gated (the allow-list is exact-match).
        AccountSignInStartEndpoint.Map(_app, SignIn);

        // The reachable front-door sign-in CALLBACK (epic #1069, issue #1080): GET /account/sign-in-callback.
        // This is the routable address the cloud sign-in page redirects the user's OWN browser back to after
        // sign-in - the remote-capable counterpart to the host-local loopback listener. It completes remote
        // sign-in: a person reaching the Gateway front door from ANOTHER machine over Tailscale is redirected
        // by the sign-in START to the cloud page carrying THIS callback as the redirect_uri, and the cloud
        // completion redirects the browser back here with the token pair, which the Gateway stores. Public
        // (allow-listed) because the browser completing sign-in has no Gateway credential yet; the captured
        // token never leaves the Gateway and is never logged (security rule DT-05 - the access logger below
        // redacts this path's query so the handed-back credential never reaches the gateway log). On a host
        // with no sign-in flow (SignIn null) it reports an explicit "not available" result.
        AccountSignInCallbackEndpoint.Map(_app, SignIn);

        // The single Gateway speech-to-text endpoint (issue #839): a caller POSTs raw audio and gets
        // text back. The phone Notes worker, the Settings "Test it" button, and on-device mode all go
        // through this one endpoint - it resolves the mode + key and runs the right provider (in-process
        // Whisper, or the resolved provider-compatible batch endpoint). Optional ?correct=true also runs
        // the validated dictionary correction, keeping that out of the callers too.
        TranscriptionBatchEndpoint.Map(_app, _keyVault);

        // Read-only analysis over the LOCAL transcription telemetry log: latency percentiles, cleanup
        // behaviour, most-corrected terms, and word frequencies, so any agent can query the Gateway to
        // see how fast and how good transcription is - all from data on this machine, never a server.
        Api.TranscriptionAnalysisEndpoint.Map(_app);

        // Text-in / text-out cleanup: run ONLY the deterministic dictionary correction over supplied
        // text + a supplied term list (no audio). The engine the multilingual eval harness drives, and
        // a way for any agent to test cleanup on arbitrary text/terms.
        Api.TranscriptionCleanupEndpoint.Map(_app);

        // Named work lists (issue #273, child of #270): an ordered list of structured item refs
        // { source, id, area? } + a single-consumer claim, the object the product skill writes to,
        // the Cockpit views, and the queue runner drains. Persisted to worklists.json across
        // Gateway restarts since issue #301 (write-through + reload-on-start with stale-claim
        // release). Inherits the host-wide token middleware above and is reachable cross-machine
        // like the rest of the Gateway surface.
        WorkListEndpoints.Map(_app, _workLists);

        // Per-work-item title + status for the Cockpit Lists view (issue #275, moved behind the
        // Gateway for the React rebuild, issue #970). The React Cockpit is a browser SPA that holds
        // no secret, so the GitHub resolve lives here: the browser calls GET
        // /gateway/lists/item-status?source&id same-origin and the GitHub token never leaves the
        // Gateway host. Inherits the host-wide token middleware above.
        Api.ItemStatusEndpoint.Map(_app, Api.GitHubItemStatusResolver.CreateDefault());

        // Cron jobs (epic #479, part 1 = #482): the REST CRUD surface over the cron-job definition
        // store. Manages definitions only - the background firing engine is part 2 (#483).
        // Persisted to cronjobs.json across restarts (write-through + reload-on-start with
        // next-run recompute). Inherits the host-wide token middleware above.
        CronJobEndpoints.Map(_app, _cronJobs);

        // Cron firing surface (epic #479, part 2 = #483): run-now and run-history over the engine.
        // Scheduled firing runs on the background sweep timer started below in StartAsync.
        CronRunEndpoints.Map(_app, _cronEngine, _cronRuns);

        // The queue runner (issue #274, child 3 of #270): the thin orchestration that turns a named
        // work list into unattended, ordered runs - one implementation session per github item,
        // watched to its IMPL-LOOP-TERMINAL sentinel (child 1, #272) before advancing. All runner
        // logic lives HERE at the Gateway; the Director host gains nothing (criterion 7). The
        // same-machine single-drain guard (criterion 8) lives on the shared runner manager.
        WorkListRunnerEndpoints.Map(_app, _workLists, Registry, _client, _runnerManager);

        // Issue #331: launcher registration + cross-machine Director lifecycle relay.
        // Launchers POST /launchers/register on startup; relay callers POST
        // /machines/{machine}/director/restart|start|stop to reach that machine's Director.
        // launcher-persistent-join: pass the stream-send hook only when stream mode is on. The relay tries
        // this first and falls back to the REST relay when it returns null (stream off, or launcher offline).
        MachineEndpoints.Map(_app, Launchers, _machineSessionSpawner, _streamMode ? SendLauncherCommandAsync : null);

        // The Cockpit Settings page surface (docs/architecture/gateway/SETTINGS_OWNERSHIP.md):
        // one snapshot GET plus brain-restart and autostart actions. Reads this host directly
        // for status/brain; run mode + autostart come from SettingsHooks (GatewayApp-owned).
        SettingsEndpoints.Map(_app, this);

        // The fleet-level wingman pipeline view (issue #239): GET /wingman/queue. Issue #549
        // retired the always-on stamping machine that fed it, so there is no live pipeline to
        // snapshot - pass null and the endpoint answers an honest idle "Disabled" snapshot.
        WingmanQueueEndpoints.Map(_app, snapshot: null);

        // Gateway-served turn briefs (issue #185): the Cockpit and the interrupted/restore paths
        // read briefs from the store HERE. Issue #549 removed the only WRITER (GatewayTurnBriefAgent),
        // so the store is read-only-serving (effectively empty going forward); the read endpoints
        // stay so existing callers degrade cleanly. The explain trigger (#217) rode the brief agent,
        // which is gone - pass null and the explain endpoint answers 503.
        TurnBriefGatewayEndpoints.Map(_app, _turnBriefStore,
            sid => _turnBriefStore.Latest(sid) is not null ? "Briefed" : "None",
            requestExplainAsync: null);

        // Issue #806 (mobile foundation): the OpenAPI document the mobile codegen consumes, and the
        // mobile app static serving at /m (built shell + token-injected index.html). Mapped before
        // the fallback proxy so these explicit routes win over the Cockpit catch-all.
        _app.MapOpenApi();

        // Web Push (mobile app-icon "needs you" dot): the phone fetches the VAPID public key and
        // registers/removes its push subscription here. Inherits the host-wide token middleware
        // (the mobile app attaches the per-machine Bearer). A new subscription nudges the notifier
        // so the fresh device gets the current dot promptly. Mapped before the mobile shell and the
        // Cockpit catch-all so these explicit routes win.
        Api.WebPushEndpoints.Map(_app, _vapidStore.PublicKey, _pushSubscriptions,
            onSubscribed: () => _pushNotifier?.ResetDedupe());

        // Mobile device enrollment (issue #908): POST /m/enroll. A phone that signed in on
        // devthrottle.com and received its per-device key hands that key here; the Gateway confirms
        // (account-scoped, by key hash) that the key belongs to its OWN signed-in account and issues the
        // phone a LOCAL device key it validates offline - so the master token is no longer injected into
        // the mobile shell. Under /m/ so it is reachable before the phone holds any credential; it carries
        // its own authorization (the account-scoped device key), exactly like /devices/register. Mapped
        // before the mobile shell so the explicit POST route wins over the shell's GET catch-all.
        var mobileEnrollmentClient = new Core.Account.DeviceRegistryClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
        Api.MobileEnrollmentEndpoint.Map(_app, new Account.MobileDeviceEnrollmentService(Account, mobileEnrollmentClient, Devices));

        // DevThrottle Stats: the always-available private dashboard (/stats) and its JSON (/stats/data).
        // A self-contained embedded page, so it works even on a plain dev build with no React wwwroot.
        // Mapped before the mobile/cockpit catch-alls so the explicit routes win.
        Stats.StatsPageEndpoint.Map(_app, InputStats);

        Mobile.MobileApp.Map(_app, Token);

        // One URL (epic #967 cutover, issue #979): the React desktop Cockpit is the Gateway's
        // canonical front door. Everything no explicit endpoint above claimed - the shell at "/",
        // client-side routes, and the hashed static assets (built into wwwroot/c by the release-gated
        // MSBuild target) - resolves here. Mapped LAST by design, exactly like /m above. The Blazor
        // Server Cockpit and its fallback reverse-proxy were retired in this cutover.
        Cockpit.CockpitReactApp.Map(_app);

        await _app.StartAsync();
        FileLog.Write($"[GatewayHost] listening on http://127.0.0.1:{Port} (version {version})");

        // Cron firing sweep (epic #479, #483): wake ~every minute and fire due jobs. The first tick
        // also catches up a fire that came due while the Gateway was down (at most once per job).
        _cronTimer = new System.Threading.Timer(_ => SweepCron(), null, CronSweepInterval, CronSweepInterval);
        FileLog.Write($"[GatewayHost] cron sweep started: every {CronSweepInterval.TotalSeconds:0}s");

        // Scheduled-run auto-dismiss (issue #1200): close automated runs that declared themselves done, by
        // sending the kill verb DOWN the Director stream. Only when stream mode is on - the close has no REST
        // fallback by design (the Gateway owns session lifecycle and reaches the Director through its stream).
        if (_streamMode)
        {
            _autoDismissSweeper = new Running.AutoDismissSweeper(
                () => PushedSessions.SnapshotFresh(AutoDismissStaleAfter),
                SendCommandAsync);
            _autoDismissTimer = new System.Threading.Timer(_ => SweepAutoDismiss(), null, AutoDismissSweepInterval, AutoDismissSweepInterval);
            FileLog.Write($"[GatewayHost] auto-dismiss sweep started: every {AutoDismissSweepInterval.TotalSeconds:0}s");
        }

        // Issue #629: start the durable telemetry retry-queue flusher. It drains any events restored
        // from disk on construction (so a backend outage that spanned the previous run's lifetime now
        // delivers) and every event the relay enqueues going forward, in FIFO order, retrying with
        // backoff while the backend is unreachable.
        _telemetryQueue.StartFlushing();

        // Issue #640: start the Gateway-owned background token refresh. Start() returns immediately (the
        // first sweep runs after a short delay), so this never blocks startup. When the cached access
        // token has expired and a refresh endpoint is configured, the sweep exchanges the refresh token
        // for a fresh pair; otherwise it is a no-op or keeps the cached credential. Null on a host with no
        // credential service.
        _tokenRefresh?.Start();

        // Issue #857: start the Gateway's background device heartbeat. The first tick (after a short delay)
        // also acts as the first-launch/retry safety net for device registration - if the Gateway is signed
        // in but not yet registered (or a sign-in-time registration failed against an unreachable cloud), it
        // registers here and then advances last-seen on every tick. Never blocks startup; a cloud failure
        // only logs and retries next tick. Null on a host with no credential service.
        _deviceHeartbeat?.Start();

        // Web Push (mobile app-icon "needs you" dot): start the background notifier now that this
        // Gateway's own /sessions endpoint is live on loopback. The notifier reads that endpoint (so its
        // "needs you" verdict is byte-identical to the roster's) and pushes the count to subscribed
        // phones. It self-gates on having at least one subscription, so it is free until a phone opts in.
        var pushSender = new Push.VapidWebPushSender(
            _vapidStore.PublicKey, _vapidStore.PrivateKey, "mailto:support@devthrottle.com");
        _pushNotifier = new Push.WebPushNeedsYouNotifier(_pushSubscriptions, GetNeedsYouCountAsync, pushSender);
        _pushNotifier.Start();
    }

    /// <summary>
    /// Read THIS Gateway's own aggregated roster over loopback and count the sessions that "need you".
    /// Going through the real <c>/sessions</c> endpoint (rather than re-implementing the fan-out) keeps
    /// the notifier's verdict identical to what every client sees - same aggregation, same effective-red
    /// fold. The per-machine Bearer is attached so it works whether or not global Gateway auth is on.
    /// </summary>
    private async Task<int> GetNeedsYouCountAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Port}/sessions");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        using var response = await _pushLoopbackHttp.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var sessions = JsonSerializer.Deserialize<List<Contracts.SessionDto>>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        return Push.WebPushNeedsYouNotifier.CountNeedsYou(sessions);
    }

    /// <summary>
    /// The cron sweep timer callback (a boundary - it owns the try/catch so a sweep failure never
    /// crashes the timer thread). Fires due jobs; per-job failures are isolated inside the engine.
    /// </summary>
    private void SweepCron()
    {
        try
        {
            _ = _cronEngine.EvaluateDueAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayHost] cron sweep FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// The auto-dismiss sweep timer callback (issue #1200; a boundary - it owns the try/catch so a sweep
    /// failure never crashes the timer thread). Closes automated runs that declared themselves done, over the
    /// Director stream. Fire-and-forget: the async sweep runs on the thread pool so the timer thread returns.
    /// </summary>
    private void SweepAutoDismiss()
    {
        try
        {
            _ = _autoDismissSweeper?.SweepAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayHost] auto-dismiss sweep FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Issue #1176 (Phase 1b): the down-channel proof. Sends a message DOWN a Director's stream and awaits
    /// its reply over the SAME connection (SignalR client results), demonstrating that the Gateway can
    /// both push to and request from a Director on the one outbound-dialed connection. Returns null when
    /// that Director has no active stream. NOTE: this is a synthetic proof, not a production command path -
    /// the retired assessed-state producer meant there was no live down-path to migrate (plan 4.7b).
    /// </summary>
    public async Task<string?> PingDirectorAsync(string directorId, string message, CancellationToken ct = default)
    {
        var connectionId = PushedSessions.GetActiveConnectionId(directorId);
        var hub = _app?.Services.GetService(typeof(Microsoft.AspNetCore.SignalR.IHubContext<Streaming.DirectorHub>))
            as Microsoft.AspNetCore.SignalR.IHubContext<Streaming.DirectorHub>;
        if (connectionId is null || hub is null)
        {
            FileLog.Write($"[GatewayHost] PingDirectorAsync: no active stream for director={directorId}");
            return null;
        }
        return await hub.Clients.Client(connectionId).InvokeAsync<string>("Ping", message, ct);
    }

    /// <summary>
    /// Issue #1177 (Phase 1): send a command DOWN a Director's stream and await its result over the SAME
    /// connection (SignalR client results), modeled exactly on <see cref="PingDirectorAsync"/>. Returns
    /// null when that Director has no active stream connection (or the hub is unavailable), which the
    /// caller treats as "no stream" and falls back to the HTTP command path. Any non-null result - success
    /// OR a typed failure - means the stream handled the command and its outcome is authoritative.
    /// </summary>
    public async Task<DirectorCommandResult?> SendCommandAsync(string directorId, DirectorCommand command, CancellationToken ct = default)
    {
        if (command is null) throw new ArgumentNullException(nameof(command));

        var connectionId = PushedSessions.GetActiveConnectionId(directorId);
        var hub = _app?.Services.GetService(typeof(Microsoft.AspNetCore.SignalR.IHubContext<Streaming.DirectorHub>))
            as Microsoft.AspNetCore.SignalR.IHubContext<Streaming.DirectorHub>;
        if (connectionId is null || hub is null)
        {
            FileLog.Write($"[GatewayHost] SendCommandAsync: no active stream for director={directorId}, verb={command.Verb}");
            return null;
        }
        FileLog.Write($"[GatewayHost] SendCommandAsync: director={directorId}, verb={command.Verb}, sid={command.SessionId}, cmdId={command.CommandId}");
        return await hub.Clients.Client(connectionId).InvokeAsync<DirectorCommandResult>("Command", command, ct);
    }

    /// <summary>
    /// launcher-persistent-join: push a lifecycle command DOWN a machine's launcher stream and await its
    /// result over the SAME connection (SignalR client results), modeled exactly on <see cref="SendCommandAsync"/>.
    /// Returns null when that machine's launcher has no active stream connection (or the hub is unavailable),
    /// which the caller treats as "no stream" and falls back to the HTTP relay. Any non-null result - success
    /// OR a typed failure - means the stream handled the command and its outcome is authoritative.
    /// </summary>
    public async Task<LauncherCommandResult?> SendLauncherCommandAsync(string machineName, LauncherCommand command, CancellationToken ct = default)
    {
        if (command is null) throw new ArgumentNullException(nameof(command));

        var connectionId = LauncherConnections.GetActiveConnectionId(machineName);
        var hub = _app?.Services.GetService(typeof(Microsoft.AspNetCore.SignalR.IHubContext<Streaming.LauncherHub>))
            as Microsoft.AspNetCore.SignalR.IHubContext<Streaming.LauncherHub>;
        if (connectionId is null || hub is null)
        {
            FileLog.Write($"[GatewayHost] SendLauncherCommandAsync: no active stream for machine={machineName}, verb={command.Verb}");
            return null;
        }
        FileLog.Write($"[GatewayHost] SendLauncherCommandAsync: machine={machineName}, verb={command.Verb}");
        return await hub.Clients.Client(connectionId).InvokeAsync<LauncherCommandResult>("Command", command, ct);
    }

    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        FileLog.Write($"[GatewayHost] StopAsync");

        try { _cronTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] cron timer dispose error: {ex.Message}"); }
        _cronTimer = null;
        try { _autoDismissTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] auto-dismiss timer dispose error: {ex.Message}"); }
        _autoDismissTimer = null;

        try { _endpointMonitor?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] endpoint monitor dispose error: {ex.Message}"); }
        _endpointMonitor = null;

        // Issue #640: stop the background token refresh timer.
        try { _tokenRefresh?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] token refresh dispose error: {ex.Message}"); }
        _tokenRefresh = null;

        // Issue #857: stop the background device heartbeat timer.
        try { _deviceHeartbeat?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] device heartbeat dispose error: {ex.Message}"); }
        _deviceHeartbeat = null;

        // Web Push: stop the background needs-you notifier (also disposes its VAPID push sender) and the
        // loopback HTTP client it read /sessions with. Subscriptions are already on disk (written through
        // on every change), so stopping loses nothing.
        try { _pushNotifier?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] push notifier dispose error: {ex.Message}"); }
        _pushNotifier = null;
        try { _pushLoopbackHttp.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] push loopback client dispose error: {ex.Message}"); }

        // Issue #629: stop the telemetry retry-queue flusher. The queue file is written through on
        // every mutation, so any undelivered events are already on disk and reload on the next start -
        // stopping never loses them.
        try { await _telemetryQueue.DisposeAsync(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] telemetry queue dispose error: {ex.Message}"); }

        // Turn-end watcher + voice sweep first (they drive the brain), then the brain itself - the
        // supervisor's dispose gracefully stops the hosted claude.exe (never leaked).
        try { _voiceSweepTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] voice sweep dispose error: {ex.Message}"); }
        _voiceSweepTimer = null;
        try { _turnEndWatcher?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] watcher dispose error: {ex.Message}"); }
        _turnEndWatcher = null;
        try { Brain.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] brain dispose error: {ex.Message}"); }

        // Unsubscribe from registry events. We deliberately do NOT tear down the serve
        // mappings: the Directors are still alive and reachable, and a Gateway restart
        // re-asserts every mapping on Start().
        _serveProvisioner.Dispose();
        Registry.Dispose();
        Launchers.Dispose();
        _client.Dispose();

        if (_app is not null)
        {
            try { await _app.StopAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { FileLog.Write($"[GatewayHost] StopAsync error: {ex.Message}"); }
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
