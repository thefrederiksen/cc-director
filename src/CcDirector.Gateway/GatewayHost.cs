using System.Diagnostics;
using System.Net;
using System.Text.Json;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
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
/// The Gateway's Kestrel host. One process per machine. Binds all interfaces (0.0.0.0:7878 by default) so a
/// tailnet or hosted client can reach it; every route except /healthz, /login, /logout requires auth, so the
/// bind is reachable-but-authenticated, not open. When CC_GATEWAY_HOSTED=1 the port follows the platform's
/// WEBSITES_PORT/PORT (see <see cref="GatewayHostedMode"/>).
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
    /// Gateway Cleanup mission (Wave 4b): the Gateway's OWN store of Missions. Missions are a fleet-level
    /// concept (they span Directors and machines and nest), so the source of truth lives at the Gateway,
    /// like fleet messaging and scheduling - not on any one Director. Reuses the same JSON-file-backed
    /// <see cref="Core.Sessions.MissionStore"/> the Director uses, pointed at a Gateway-side file. The
    /// mission REST endpoints (POST/GET /missions) read and write this, and a mission-scoped spawn
    /// validates against it before forwarding the create to a Director. The Director's own /missions
    /// routes stay until a later phase; this is the additive Gateway-native equivalent.
    /// </summary>
    public Core.Sessions.MissionStore Missions { get; }

    /// <summary>
    /// Gateway Cleanup mission, Phase 0 (up-stream): the registry of live Director up-streams (terminal output
    /// and finite file/screenshot reads), keyed by the stream id the Gateway mints per browser request. The
    /// director-stream hub's StreamUp method pumps the Director's frames into this registry, which forwards
    /// them to the browser-facing sink with pull-then-forward backpressure. Phase 2 wires the browser-facing
    /// legs to it; Phase 0 delivers and unit-tests the machinery.
    /// </summary>
    public Streaming.GatewayStreamRegistry StreamRegistry { get; }

    /// <summary>
    /// DevThrottle Stats: the always-available aggregate of every session's input tally (turns + character
    /// volume by modality and surface). Fed by the director-stream hub from the pushed
    /// <see cref="Contracts.SessionDto.InputStats"/> and read by the private Gateway dashboard at
    /// <c>/stats</c> with no cloud round-trip.
    /// </summary>
    public Stats.GatewayInputStatsAggregator InputStats { get; }

    /// <summary>
    /// Defect 20: the observer that starts a deferred snooze's clock when the Director pushes up the hold
    /// having LANDED. Shared with the DirectorHub through the container, like <see cref="InputStats"/>.
    /// </summary>
    internal Snooze.SnoozeLandingObserver SnoozeLandings { get; }

    /// <summary>
    /// Defect 5: the observer that stamps each session's Gateway-resolved role back DOWN to the Director
    /// that owns it, so the desktop rail folds the same role the phone and the Cockpit fold. Shared with the
    /// DirectorHub through the container, like <see cref="SnoozeLandings"/>.
    /// </summary>
    internal Fleet.FleetRoleObserver FleetRoles { get; }

    /// <summary>
    /// The observer that stamps each session's FOLDED display state (effective color, label, triage bucket,
    /// needs-you-since, the snooze clock, the snooze-ended marker) back DOWN to the Director that owns it, so
    /// the desktop rail renders the Gateway's answer instead of re-folding from local facts it cannot see.
    /// Shared with the DirectorHub through the container, like <see cref="FleetRoles"/>; also driven by a
    /// periodic sweep (<see cref="_displayStateSweepTimer"/>) as the backstop for Gateway-only overlay
    /// changes that arrive on no Director push.
    /// </summary>
    internal Fleet.FleetDisplayStateObserver FleetDisplayState { get; }

    /// <summary>
    /// The durable fleet CONCURRENCY record (how many sessions run at once, and how many are actively
    /// working at once) that the private Gateway dashboard and the agent API read. Fed from the same
    /// assembled /sessions roster as <see cref="InputStats"/>, so it is fleet-wide with no per-Director
    /// instrumentation - a session count is visible for every session on every machine, new build or old.
    /// </summary>
    public Stats.GatewaySessionConcurrencyStats SessionConcurrency { get; }

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
    /// Snooze Length mission: the Gateway-owned snooze registry. Exposed so a test can inject a pending
    /// (or already-expired) snooze and assert the /sessions overlay flips it back into "needs you".
    /// </summary>
    internal Snooze.SnoozeRegistry SnoozeRegistry => _snoozeRegistry;

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

    // The single resolve-then-create path for spawning a session on a target machine (cron + the
    // interactive POST /machines/{machine}/sessions relay). Built in the constructor, used by both.
    private readonly Running.MachineSessionSpawner _machineSessionSpawner;
    private readonly TailscaleServeProvisioner _serveProvisioner;
    private readonly GatewayTurnBriefStore _turnBriefStore;
    private readonly KeyVault _keyVault;

    // Lost Dictations mission (#1593): the transcription owner the dictation endpoint uses. Null in
    // production - StartAsync then builds the real one over _keyVault. Only a test injects a stub, because
    // the dictation delivery arm sits BEHIND a successful transcribe and the hosted URL is a constant.
    private readonly Transcription.GatewayTranscriptionService? _dictationTranscription;
    // Issue #881: mints/ensures the DevThrottle inference key after sign-in and at startup. Null on a
    // host with no credential service (nothing to sign in to).
    private readonly Account.TranscriptionKeyAutoProvisioner? _transcriptionKeyProvisioner;
    private readonly WorkListStore _workLists;
    // Snooze Length mission: the Gateway-owned, restart-surviving snooze registry (the one piece of
    // new state). An expired snooze comes back on its own with no background timer: HoldStateFor reports
    // an elapsed entry as None on every read. Constructed here (load-on-construct re-arms every pending
    // snooze). There is no expiry sweep - an elapsed entry lingers as a durable returned-by-timer
    // tombstone and is retired only by an edge that ends a snooze (work, an owner turn, an exit, a
    // re-snooze), bounded by the live-session prune paths.
    private readonly Snooze.SnoozeRegistry _snoozeRegistry;
    // Fills account_hosted_ai_spend by periodically mirroring the cloud credit-debit ledger (issue #1771).
    private Governance.HostedAiSpendSweep? _hostedAiSpendSweep;
    // Mission Screen mission (Phase 1b, issue #1405): the Gateway-owned, restart-surviving store of each
    // mission's WHY, keyed by the mission's normalized name. Durable + shared so every Cockpit, the phone,
    // and the future Mission-Control chat/API read the same WHY. Constructed here (load-on-construct
    // re-serves every WHY after a restart); exposed to the client over MissionNotesEndpoint.
    private readonly MissionNotes.MissionNoteStore _missionNotes;
    // Hosted Gateway mission, Step 1b: the EF data layer (gateway.db). The host owns ONE instance and the
    // structured stores that have moved off hand-rolled JSON read/write through it. On the single-tenant
    // local install every row is the "local" tenant (SingleTenantContext), so behavior is unchanged.
    // Hosted Multi-Tenancy increment 1: the tenant context GatewayDatabase reads. On the hosted Gateway it is
    // the AsyncLocalTenantContext (per-account, fail-closed, set at the auth boundary); on self-host it is the
    // SingleTenantContext (always Local). Assigned in the constructor from GatewayHostedMode.IsHosted.
    private readonly Core.Tenancy.ITenantContext _tenantContext;
    // The concrete ambient context - non-null ONLY on the hosted Gateway - the object the auth boundaries
    // enter per-account (and the reserved SYSTEM) scopes on. Null on self-host (Local is the ambient answer,
    // nothing to enter). It is the SAME instance GatewayDatabase reads, so a boundary's scope is what the
    // stores see.
    private readonly Core.Tenancy.AsyncLocalTenantContext? _hostedTenant;
    // The auth-boundary tenant binder (Hosted Multi-Tenancy increment 1): resolves an authenticated device
    // key to its tenant and enters the scope. Used by the tunnel Hello and the device-key HTTP middleware.
    // Inert on self-host (every authenticated caller is Local).
    private readonly Tenancy.HostedTenantBoundary _tenantBoundary;
    // The background-loop tenant seam (Hosted Multi-Tenancy, session-serving PR2). A sweep is on no request
    // and no tunnel connection, so it has no tenant of its own: it runs ONE PASS PER TENANT through this,
    // instead of the single implicit TenantId.Local pass the loops used to hard-code. Self-host runs exactly
    // one Local pass (unchanged); hosted enters each live tenant's scope in turn. Its Current is also what
    // every Gateway-internal store read and every down-channel command resolves to - null on hosted with no
    // scope, which is a DENY, never a fall back to Local.
    private readonly Tenancy.ITenantPass _tenantPass;
    private readonly Data.GatewayDatabase _gatewayDb;

    /// <summary>
    /// The auth-boundary tenant binder. Exposed to the test assembly so an isolation test can enter the same
    /// tenant scope a real request or tunnel connection would, and drive the production loop code inside it.
    /// </summary>
    internal Tenancy.HostedTenantBoundary TenantBoundary => _tenantBoundary;

    /// <summary>
    /// The account-to-tenant resolver (Hosted Multi-Tenancy increment 1): owns the tenants mapping table and
    /// mints or looks up a tenant from a verified account subject. Exposed so the hosted enrollment boundary
    /// can resolve a tenant once, at the point the account token is validated. Unused on the single-tenant
    /// local install (everything resolves to "local" without a lookup).
    /// </summary>
    public Tenancy.TenantRegistry TenantRegistry { get; }

    /// <summary>The paid-entitlement gate read at hosted enrollment. Present on every host; consulted only
    /// where the hosted enrollment route is mapped, which is hosted only.</summary>
    public Tenancy.EntitlementRegistry EntitlementRegistry { get; }

    // The persisted workflow catalog (Workflows mission, phase 1): built-ins seeded at startup,
    // user-defined workflows beside them, served by Api.WorkflowEndpoints.
    private readonly Workflows.WorkflowStore _workflows;
    // Workflow runs (phase 4, issue #1771): one row per execution of a workflow definition, pinned to
    // the version that governed it. The governance outcome spine.
    private readonly Workflows.WorkflowRunStore _workflowRuns;
    // The append-only governance event ledger (issue #1771, spine item 2): immutable session/run state
    // transitions, the duration spine no run row can give.
    private readonly Governance.GovernanceEventLedger _governanceEvents;
    // Honest driver-normalized spend (issue #1771, spine item 3): per-session token effort + billing-mode
    // label, and the account-level hosted-AI service dollars mirrored from the credit-debit ledger.
    private readonly Governance.SessionSpendStore _sessionSpend;
    private readonly Governance.AccountHostedAiSpendStore _hostedAiSpend;
    // The append-only governance audit trail (issue #1771, spine item 4): structured intervention +
    // permission/sandbox decisions, recorded as events, never inferred from transcripts.
    private readonly Governance.GovernanceAuditLog _governanceAudit;
    // Fills session_spend at each turn-end from the pushed roster snapshot (issue #1771, spine item 3).
    private readonly Governance.SessionSpendEmitter _sessionSpendEmitter;
    // Fills the governance event ledger with session state transitions (issue #1771, spine item 2).
    private readonly Governance.SessionStateEventEmitter _sessionStateEmitter;
    // The weekly Outcome Ledger reporter (issue #1771, spine item 4): a read-only assembly over the run
    // tables, event ledger, spend, and audit trail - the first governance report that pays rent.
    private readonly Governance.OutcomeLedgerReporter _outcomeLedger;
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
    // The fold push backstop: the DirectorHub seam re-folds immediately on every Director push, which covers
    // every Director-driven change (activity, hold, desktop dictation). This periodic sweep catches the
    // GATEWAY-ONLY overlay changes that arrive on no push - voice generation, the Gateway's own
    // transcription, a phone dictation, a snooze expiring - so the desktop rail is never more than one
    // interval behind them. The observer's change gate keeps it quiet when nothing changed. Disposed in
    // StopAsync.
    private System.Threading.Timer? _displayStateSweepTimer;
    private static readonly TimeSpan DisplayStateSweepInterval = TimeSpan.FromSeconds(5);

    // Voice-turn upload staging retention. The staging directory for a voice turn is deleted on the SUCCESS
    // path only, so every upload that ends any other way - a size refusal, a dropped connection, an assembly
    // that never completed, a caller that simply walked away - stays on disk with its recorded audio until
    // something removes it. This timer is that something; without it the staging grows without bound and
    // holds recorded speech for as long as the Gateway lives.
    private System.Threading.Timer? _voiceTurnUploadSweepTimer;
    private static readonly TimeSpan VoiceTurnUploadSweepInterval = TimeSpan.FromMinutes(15);
    // WHY FOUR HOURS, from what the staging is for rather than from a round number. A voice-turn staging
    // directory exists only between a register and that same upload's own complete: the phone records one
    // push-to-talk utterance, streams it in chunks, and the Gateway assembles it immediately. In normal
    // operation that whole life is seconds to a couple of minutes, and the worst legitimate case - a phone
    // on a failing mobile link retrying chunks with backoff - is minutes, tens of minutes at the extreme.
    // Four hours is an order of magnitude beyond that extreme, so no upload that is still genuinely trying
    // can be cut off, while abandoned audio has a hard retention ceiling measured in hours rather than in
    // the lifetime of the process. The sweep judges by an EXPLICIT last-activity signal that every
    // successful operation refreshes - a register or resume, an idempotent chunk, a real chunk, an assemble
    // (see VoiceUploadStore.EnsureFreshStaging) - not by whether a byte happened to be written, so a client
    // that resumes an upload without writing new bytes still counts as alive and only truly-idle staging
    // ages out. That is precisely the definition of abandoned.
    private static readonly TimeSpan VoiceTurnUploadMaxAge = TimeSpan.FromHours(4);
    /// <summary>
    /// Test seam: overrides the voice-turn upload sweep schedule (both the first tick and the period).
    /// Null in production and never assigned outside tests.
    ///
    /// It exists so the WIRING can be tested rather than assumed. The sweep method itself already had a
    /// direct unit test while nothing in production called it at all - which is exactly how an unbounded
    /// staging directory sat behind what read like coverage. A test that boots a real Gateway and watches
    /// stale staging disappear on its own can only pass when the timer below is really started, so removing
    /// the timer turns it red.
    /// </summary>
    internal static TimeSpan? VoiceTurnUploadSweepScheduleForTests;
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
    // Car Mode (Voice-screen-actions phase, design B): the per-device "current subject" - the session the
    // owner is talking about - so "it" / "answer it" / "snooze it" resolve after a focus or a read.
    private readonly CarMode.CarModeSubjectStore _carModeSubjects = new();
    // Car Mode performance round: the durable, Gateway-local store of per-turn timing records. The browser
    // posts ONE record per turn; GET /carmode/telemetry reads them back. Retained about 90 days by age.
    private readonly CarMode.CarModeTelemetryStore _carModeTelemetry = new();
    // Car Mode offline resilience Phase 4b (issue #1427): idempotency + single-flight cache for
    // POST /carmode/turn keyed by the client's turn id, so an already-sent turn whose result was lost in a
    // dead zone auto-retries and ACTS at most once. In-memory, one instance for the whole Gateway.
    private readonly CarMode.CarModeTurnCache _carModeTurnCache = new();
    // Gateway-owned set of sessions whose dictated utterance is being transcribed in the background
    // (the phone released the Speak dialog and the audio is uploading/transcribing). Stamps the
    // orange "Transcribing..." roster color so nobody else grabs the session mid-dictation.
    private readonly Transcription.TranscribingSessions _transcribingSessions = new();
    // Issue #549: the always-on turn-brief stamping pipeline (GatewayTurnBriefAgent) is retired.
    // TurnEndWatcher stays and runs unconditionally - its only job now is firing voice
    // auto-refresh on turn-end for voice sessions, and clearing the stale voice/text cache on
    // the Working transition. The wingman narration (text and voice) now runs on the stateless
    // HostedInferenceBrain (a hosted model call), not the spawned BrainSupervisor.
    private TurnEndWatcher? _turnEndWatcher;
    private Wingman.WingmanVoiceService? _voiceService;
    // Editable/versioned wingman instructions (issue #537); the voice translator reads the active set.
    // Constructed in the constructor body once the EF database is built (it persists to the data layer).
    private readonly Wingman.WingmanInstructionsStore _instructionsStore;
    // Shared training-data store: the voice service WRITES captures, the instructions A/B test READS them.
    private readonly Wingman.WingmanTrainingStore _trainingStore = new();
    private System.Threading.Timer? _voiceSweepTimer;
    // Durable dictation upload staging (issue #1006): the phone streams recorded audio here in chunks;
    // the Gateway assembles, transcribes, and injects the turn itself. Each upload id carries a durable
    // delivery record (issue #1183): PENDING chunks are retained until delivered/abandoned, and the
    // terminal tombstone de-dupes the upload id forever until the client acknowledges it - so there is no
    // age sweep for dictation staging (only the unrelated voice-turn staging is age-swept).
    private readonly Voice.VoiceUploadStore _dictationUploads = new(CcDirector.Core.Storage.CcStorage.DictationUploads());
    // Store injection points (Hosted Gateway, Step 1b): the host owns ONE instance of each durable store
    // that was previously reached through a process-wide static, and hands it to the endpoint/service that
    // uses it, so a tenant id can reach the storage layer in a later pull request. Same default paths as
    // the retired statics; no behavior change. The voice-turn upload store (VoiceTurnUploads root) is
    // distinct from _dictationUploads above (DictationUploads root) - two roots, two subsystems.
    private readonly Prompts.GatewayPromptLog _promptLog;
    private readonly Transcription.TranscriptionTelemetryLog _transcriptionTelemetry = new();
    private readonly Transcription.TranscriptionAudioArchive _transcriptionAudioArchive = new();
    private readonly Voice.VoiceUploadStore _voiceTurnUploads = new();
    // The Gateway's stable per-machine install identity (issue #857), owned by the host and handed to the
    // device-registration service instead of the retired static GatewayInstallId.LoadOrCreate.
    private readonly Account.GatewayInstallId _installIdStore = new();
    // The Gateway bearer token store, owned by the host and used to resolve the token below instead of the
    // retired static GatewayAuth.LoadOrCreate. The tray's read-only path stays on GatewayAuth.DefaultTokenFile.
    private readonly Util.GatewayAuth _gatewayAuth = new();

    /// <summary>
    /// THE PRODUCER of the dictation phase label - the three facts that decide whether a session paints
    /// orange for a dictation. The roster's <c>dictationStatusFor</c> callback is exactly this method, so
    /// what a test drives here is what production runs.
    ///
    /// WHY IT IS A NAMED METHOD AND NOT AN INLINE LAMBDA. The rule (<see cref="Transcription.DictationPhase.For"/>)
    /// has regression tests; the WIRING that supplies its three facts had none - the callback appeared in
    /// zero test files. You could wire <c>progressing: true</c> as a constant and the whole Gateway suite
    /// stayed green while defect 19 returned in full, because a hard-true progress flag makes any
    /// undelivered record paint forever, which IS the defect. An unbindable seam is an untestable one, and
    /// this repository's signature failure is a live consumer with an unguarded producer - the rule pinned,
    /// the wiring not. Extracting it changes no behaviour and makes the seam bindable;
    /// <c>DictationOrangeProducerTests</c> binds it with the REAL collaborators and goes red if any of the
    /// three facts is replaced by a constant.
    ///
    /// Read the three facts as the two questions they answer, because conflating them was the whole defect:
    /// <paramref name="uploads"/> answers the DURABLE one ("are there undelivered words?" - must never
    /// expire), and <paramref name="marks"/> answers the BOUNDED one ("is anything actually happening right
    /// now?" - must always expire).
    /// </summary>
    internal static string? DictationStatusFor(
        string sessionId, Transcription.TranscribingSessions marks, Voice.VoiceUploadStore uploads)
        => Transcription.DictationPhase.For(
            activelyTranscribing: marks.IsActivelyTranscribing(sessionId),
            undelivered: uploads.IsSessionLocked(sessionId),
            progressing: marks.IsTranscribing(sessionId));
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
    private Api.NetDiagMonitor? _netDiagMonitor;
    private Api.NetDiagRollupStore? _netDiagRollup;
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
    /// <param name="missionsPath">
    /// Override the Gateway-native mission store file (Gateway Cleanup mission, Wave 4b). Tests pass an
    /// isolated temp path; production omits it for the shared default at
    /// <c>CcStorage.Root()\missions.json</c>.
    /// </param>
    /// <param name="missionNotesPath">
    /// Override the mission-WHY store file (Mission Screen mission, Phase 1b, issue #1405). Tests pass an
    /// isolated temp path; production omits it for the shared default at
    /// <c>CcStorage.Root()\mission-notes.json</c>.
    /// </param>
    /// <param name="dictationTranscription">
    /// Override the transcription owner the DICTATION endpoint uses (Lost Dictations mission, issue #1593).
    /// The dictation complete path transcribes BEFORE it delivers, so an end-to-end test of the delivery
    /// arm cannot reach that arm at all without a transcription that succeeds - and the hosted base URL is a
    /// compile-time constant, so there is no way to point it at a local stub. Tests pass a service built over
    /// a stub HttpClient (and their own telemetry + audio archive, which otherwise default to process-wide
    /// Shared instances that write to the real user's directories). Production omits it and the host builds
    /// the service over its own key vault, exactly as before.
    /// </param>
    public GatewayHost(int port = DefaultPort, string? token = null, bool? authEnabled = null, string? instancesDirectory = null, string? turnBriefDirectory = null, string? keyVaultPath = null, string? workListsPath = null, string? cronJobsPath = null, string? cronRunsPath = null, string? devicesPath = null, string? telemetryQueuePath = null, int? telemetryQueueMaxSize = null, TimeSpan? telemetryRetryInterval = null, Core.Account.DevThrottleAccountService? account = null, bool? streamMode = null, string? inputStatsPath = null, string? promptLogPath = null, string? snoozePath = null, string? pushSubscriptionsPath = null, string? wingmanInstructionsPath = null, string? missionsPath = null, string? missionNotesPath = null, Transcription.GatewayTranscriptionService? dictationTranscription = null, Core.Agents.AgentKind? brainTool = null)
    {
        // Resolve and VALIDATE the warm-brain tool up front, before any resource is opened: a brain tool
        // that cannot be hosted is a configuration error that must fail loudly at construction, not
        // silently later at the brain's first spawn. BrainToolConfig.Get reads config.json; a test passes
        // brainTool directly. Only ClaudeCode is hostable today (the hosted-agent path needs a preassigned
        // session id and transcript reads), so a non-hostable value throws here with the fix.
        BrainTool = Core.Configuration.BrainToolConfig.EnsureHostable(brainTool ?? Core.Configuration.BrainToolConfig.Get());

        Port = port;
        Token = token ?? _gatewayAuth.LoadOrCreate();
        Registry = new DirectorRegistry(instancesDirectory);
        // Issue #1292: free a removed Director's session numbers so a Director that died without releasing
        // them does not leak the pool. OnDirectorRemoved fires on graceful unregister and on the registry's
        // own stale/unreachable sweep, so this never fires for a merely momentarily-unreachable Director.
        // The allocator's own store keys assignments by BARE director id with no tenant beside them, so it
        // cannot yet scope this release even though the event now carries the tenant. That is a real hole of
        // the same family - one account's disconnect frees another account's session numbers - but closing it
        // means partitioning the allocator, which is its own unit of work; see the note on
        // FleetSessionNumberAllocator.ReleaseForDirector.
        Registry.OnDirectorRemoved += removal => SessionNumbers.ReleaseForDirector(removal.DirectorId);
        PushedSessions = new Streaming.PushedSessionStore();
        // Gateway Cleanup mission (Wave 4b): the Gateway-native mission store, at a Gateway-side file path
        // (CcStorage.Root(), the same location the cron and snooze stores use), NOT the Director's tool-config
        // missions.json. Reuses Core.Sessions.MissionStore unchanged.
        Missions = new Core.Sessions.MissionStore(missionsPath ?? Path.Combine(CcStorage.Root(), "missions.json"));
        StreamRegistry = new Streaming.GatewayStreamRegistry();
        InputStats = new Stats.GatewayInputStatsAggregator(inputStatsPath);
        _promptLog = new Prompts.GatewayPromptLog(promptLogPath);
        SessionConcurrency = new Stats.GatewaySessionConcurrencyStats();
        RosterCache = new Discovery.FleetRosterCache();
        // Issue #1215: when a Director is unregistered or evicted from the registry, forget its cached
        // roster too so the cache does not grow without bound; a re-registering Director starts clean.
        // Scoped to the tenant the removal names. The cache is partitioned by (tenant, director) and the
        // removal now carries its owner, so forgetting one account's Director cannot reach another's - which
        // it could when this event was a bare string and the forget swept every matching partition.
        Registry.OnDirectorRemoved += removal => RosterCache.Forget(removal.Tenant, removal.DirectorId);
        LauncherConnections = new Streaming.LauncherConnectionRegistry();
        var gatewayConfig = Core.Configuration.GatewayConfig.Load();
        // Gateway Cleanup: the tunnel is mandatory; the streamMode parameter is ignored and retained only for existing test call sites (removed with the test rewrite).
        _streamStaleAfter = TimeSpan.FromSeconds(gatewayConfig.StreamStaleAfterSeconds);
        Devices = new Pairing.DeviceRegistry(devicesPath);
        AuthEnabled = ResolveAuthEnabled(authEnabled);
        if (AuthEnabled)
            FileLog.Write($"[GatewayHost] auth gate booted ON (enforced by default, issue #917 - a per-device key or the shared token is required, even on the tailnet; set {AuthDisabledEnvVar}=1 to disable for debugging)");
        else
            FileLog.Write($"[GatewayHost] auth gate booted OFF (disabled via override - requests are accepted without a credential; this is a debugging mode, not the shipped default)");
        _serveProvisioner = new TailscaleServeProvisioner(Registry, Port);

        // The Gateway's in-process warm brain (issue #184): supervisor only - the chosen tool
        // spawns lazily. Its former consumers have since moved off it: the wingman narration now
        // runs on the stateless HostedInferenceBrain (a hosted model call), and the always-on
        // turn-brief agent was retired (issue #549). Today the only thing that spawns it is the
        // Settings "Restart Brain" action, so this supervisor is kept for that manual path only.
        // The tool and model remain an EXPLICIT Gateway-level choice (issue #393, building on the
        // pinned-model #204); both default to claude + opus when unset. A config change applies on
        // the next Gateway restart. BrainTool is resolved and validated hostable at the top of this
        // constructor.
        BrainModel = BrainModelConfig.Get();
        FileLog.Write($"[GatewayHost] brain tool: {BrainTool}, model: {BrainModel}");
        Brain = new BrainSupervisor(
            new HostedAgentOptions
            {
                WorkingDirectory = Path.Combine(CcStorage.Root(), "brain"),
                AgentArgs = $"{ClaudeDriver.DefaultArgs} --model {BrainModel}",
                Log = FileLog.Write,
            },
            // Construct the hosted brain through HostedAgent.For - the single guard for which agent kinds
            // can be hosted headless (only ClaudeCode today). BrainTool is already validated hostable at
            // the top of this constructor, so this never throws here; routing through For instead of
            // newing a HostedAgent with an arbitrary registry driver keeps that guard the one and only
            // path to a hosted brain, so a non-hostable tool can never slip through to fail at Start.
            agentFactory: o => CcDirector.HostedAgent.HostedAgent.For(BrainTool, o));
        _turnBriefStore = new GatewayTurnBriefStore(turnBriefDirectory);
        // Production omits keyVaultPath for the shared default; tests pass an isolated path so
        // they never touch the real %LOCALAPPDATA% key store.
        _keyVault = new KeyVault(keyVaultPath);
        _dictationTranscription = dictationTranscription;
        // The EF data layer (Hosted Gateway mission, Step 1b): one gateway.db under the storage root,
        // migrated on open, fail-loud (no JSON fallback). The structured stores that have moved off JSON
        // (work lists, cron) read/write through it, so it is built before them. Tests get an isolated
        // database via CC_DIRECTOR_ROOT, exactly as gateway-stats.db is isolated today.
        // Hosted Multi-Tenancy increment 1: choose the tenant context. Hosted -> the AsyncLocalTenantContext
        // (per-account, fail-closed: a tenant-scoped op outside a resolved scope THROWS, never defaults to
        // local); self-host -> the SingleTenantContext (always Local, unchanged). The same instance is handed
        // to GatewayDatabase (below) and registered in DI, so a scope entered at an auth boundary is exactly
        // what the stores read.
        if (GatewayHostedMode.IsHosted)
        {
            _hostedTenant = new Core.Tenancy.AsyncLocalTenantContext();
            _tenantContext = _hostedTenant;
        }
        else
        {
            _tenantContext = new Core.Tenancy.SingleTenantContext();
        }

        _gatewayDb = new Data.GatewayDatabase(_tenantContext);
        // The account-to-tenant resolver (Hosted Multi-Tenancy increment 1): owns the tenants mapping table
        // and mints/looks up a tenant from a verified account subject. Built over the EF database; wired into
        // the hosted enrollment boundary (which validates the account token and stamps the resolved tenant on
        // the device) in the follow-up increment. Unused on the single-tenant local install.
        TenantRegistry = new Tenancy.TenantRegistry(_gatewayDb);
        EntitlementRegistry = new Tenancy.EntitlementRegistry(_gatewayDb);
        // The auth-boundary tenant binder (Hosted Multi-Tenancy increment 1): the tunnel Hello and the
        // device-key HTTP middleware resolve a tenant from the AUTHENTICATED device key through this, and
        // enter the scope the stores read. Inert on self-host. Built over the same _tenantContext instance
        // the stores read (so a scope it enters is what they resolve) and the device registry.
        _tenantBoundary = new Tenancy.HostedTenantBoundary(_tenantContext, Devices);
        // The background-loop seam (Hosted Multi-Tenancy, session-serving PR2). Its tenant list is the live
        // push-store partition set - exactly the tenants with a Director bound to the tunnel, which is the
        // only fleet a push-store-driven sweep could act on - so a sweep costs no per-tick database scan.
        // (Loops driven by a DATABASE table rather than the push store - cron, hosted-AI spend mirroring,
        // web-push - are NOT converted here: KnownTenants would silently skip every tenant whose Director is
        // offline, which is the tenant most likely to need a scheduled job. They stay hosted-disabled until a
        // database-backed tenant enumeration and a fairness order exist. That is its own increment.)
        _tenantPass = new Tenancy.TenantPass(_tenantBoundary, _hostedTenant, PushedSessions.KnownTenants);
        // Hosted Multi-Tenancy increment 1: the store constructors below seed built-ins and import/re-arm from
        // the database at startup - legitimate SYSTEM operations with no per-account identity. On the hosted
        // Gateway (fail-closed ambient tenant) they must run inside the reserved SYSTEM scope, or the very
        // first construction-time read/write would fail closed at boot. On self-host _hostedTenant is null and
        // this is a no-op (SingleTenantContext already answers Local). The scope is disposed in the finally so
        // a construction fault cannot leak it, and so per-account and per-request scopes (never SYSTEM) govern
        // everything after startup.
        var startupSystemScope = _hostedTenant?.Enter(Core.Tenancy.TenantId.System);
        try
        {
        // Named work lists persist across a Gateway restart (issue #301) in the worklists table (stale
        // claims released on load). The path argument is the LEGACY worklists.json, imported once on first
        // upgrade then renamed aside. Tests MUST pass an isolated path so they never touch the real legacy file.
        _workLists = new WorkListStore(_gatewayDb, workListsPath ?? Path.Combine(CcStorage.Root(), "worklists.json"));
        // The workflow catalog (Workflows mission, phase 1): persisted in the workflows tables, the
        // shipped built-ins seeded/upgraded at construction. No legacy JSON - the previous catalog was
        // compiled-in C# literals, so there is nothing on disk to import.
        _workflows = new Workflows.WorkflowStore(_gatewayDb);
        // Workflow runs (phase 4, issue #1771): built after the catalog store so the built-ins a run
        // pins are already seeded.
        _workflowRuns = new Workflows.WorkflowRunStore(_gatewayDb);
        // The governance event ledger (issue #1771, spine item 2): append-only session/run transitions on
        // the EF data layer, so a Gateway restart never loses a recorded transition.
        _governanceEvents = new Governance.GovernanceEventLedger(_gatewayDb);
        // Honest spend (issue #1771, spine item 3): per-session token effort + billing-mode label, and the
        // account-level hosted-AI service dollars mirrored from the credit-debit ledger.
        _sessionSpend = new Governance.SessionSpendStore(_gatewayDb);
        _hostedAiSpend = new Governance.AccountHostedAiSpendStore(_gatewayDb);
        // The governance audit trail (issue #1771, spine item 4): append-only intervention + permission/
        // sandbox decisions on the EF data layer, so a Gateway restart never loses a recorded audit fact.
        _governanceAudit = new Governance.GovernanceAuditLog(_gatewayDb);
        _sessionSpendEmitter = new Governance.SessionSpendEmitter(_sessionSpend);
        _sessionStateEmitter = new Governance.SessionStateEventEmitter(_governanceEvents);
        // The weekly Outcome Ledger reporter (issue #1771, spine item 4): read-only over the run tables +
        // event ledger + spend + audit trail. No store of its own.
        _outcomeLedger = new Governance.OutcomeLedgerReporter(_gatewayDb);
        // Snooze Length mission: the persisted snooze registry (sessionId -> SnoozeUntilUtc), now in the
        // snoozes table of the EF data layer - a Gateway restart re-arms every pending snooze from the
        // database; an entry already past its time simply fires on the first sweep. The path argument is the
        // LEGACY snooze.json, imported once on first upgrade then renamed aside. Tests MUST pass an isolated
        // path so they never touch the real legacy file. The registry is bounded by dropping a removed
        // Director's entries so they do not accumulate.
        _snoozeRegistry = new Snooze.SnoozeRegistry(_gatewayDb, snoozePath ?? Path.Combine(CcStorage.Root(), "snooze.json"));
        // Editable/versioned wingman instructions (issue #537) now persist in the wingman_instructions table
        // of the EF data layer. The path argument is the LEGACY wingman-instructions.json, imported once on
        // first upgrade then renamed aside. Tests MUST pass an isolated path so they never touch the real file.
        _instructionsStore = new Wingman.WingmanInstructionsStore(_gatewayDb, wingmanInstructionsPath ?? Path.Combine(CcStorage.Root(), "wingman-instructions.json"));
        // Skipped when HOSTED (Hosted Multi-Tenancy): this cleanup writes the tenant-scoped snoozes store, but
        // it fires from the DirectorRegistry stale sweep (a background thread with no ambient tenant), so on
        // hosted it would fail closed. It is also a per-director-across-tenants operation, which the
        // session-serving increment makes per-tenant. The other OnDirectorRemoved subscribers (session-number
        // release, roster-cache forget) are in-memory and stay wired. Skipping it only leaves a removed
        // Director's snoozes as durable tombstones, bounded by the live-session prune paths.
        Registry.OnDirectorRemoved += removal => { if (!GatewayHostedMode.IsHosted) _snoozeRegistry.ClearForDirector(removal.DirectorId); };
        // THE PUSH SEAM where this Gateway drives the hold machine off the facts Directors report. The
        // DirectorHub (constructed per-invocation by SignalR) folds every pushed session through this one
        // instance, exactly as it does the input-stats aggregator.
        //
        // SINGLE WRITER OF HOLD (round 2 finding 1). This observer only mutates the registry now; it no
        // longer stamps a one-shot hold mirror down. That fire-and-forget could land a stale None after a
        // fresh Held and be suppressed by the reliable channel's change gate, leaving the desktop rail
        // permanently stale. The SINGLE writer of HoldState down to the Director is FleetDisplayStateObserver
        // (constructed just below), which folds the hold from the registry on the same push, is change-gated,
        // retried, and driven by the periodic display-state sweep - so every transition this observer makes
        // reaches the rail at fold cadence, with no racing second writer.
        SnoozeLandings = new Snooze.SnoozeLandingObserver(
            _snoozeRegistry,
            utcNow: null);
        // Defect 5: the push seam that stamps each session's resolved role down to its owning Director, so
        // the desktop stops being the one screen that cannot suppress a Worker's red. Reads the same fresh
        // fleet snapshot the auto-dismiss sweeper reads (roles need the WHOLE fleet - a controller may be on
        // another machine) and sends over the same down-channel. Like SnoozeLandings, the DirectorHub
        // (constructed per-invocation by SignalR) folds every pushed session through this ONE instance -
        // which matters here more than anywhere: the instance holds the change gate that stops the stamp
        // echoing back up and re-triggering itself forever.
        // Hosted Multi-Tenancy (session-serving): NOT yet converted to the per-tenant pass.
        // BLOCKED ON: partitioning FleetRoleObserver._lastSent by tenant. That change gate is keyed by session
        // id alone and is PRUNED against the current pass's snapshot, so running this once per tenant would
        // have each tenant's pass delete every other tenant's gate entries - inverting a suppressed no-op into
        // a role-stamp storm every sweep. Converting the loop before partitioning the state it MUTATES makes
        // things worse, not better. Until then it stays on Local: correct on self-host, and on hosted a Local
        // read is empty, so it degrades to a no-op exactly as today (never a wrong-tenant read).
        FleetRoles = new Fleet.FleetRoleObserver(
            () => PushedSessions.SnapshotFresh(TenantId.Local, AutoDismissStaleAfter),
            SendCommandAsync);
        // The fold push seam: stamps each session's folded display state down to its owning Director, so the
        // desktop rail stops re-folding from local facts it cannot see. Folds through the SAME method the
        // roster serves from (StampFleetRolesAndFold with THIS host's NeedsYouClock and snooze registry), so
        // the answer pushed to the desktop is byte-identical to the answer every browser gets - one fold, one
        // authority. Like FleetRoles it holds the change gate that stops the stamp echoing back up forever.
        //
        // The snapshot carries only Director-owned facts (it is what the Director pushed up); the two voice
        // readiness booleans are Gateway-only (_voiceService), so they MUST be enriched onto each session
        // here BEFORE the fold - exactly as the roster handler does before its own StampFleetRolesAndFold
        // call - or the push seam folds VoiceAudioReady=false for every session and holds every voice-mode
        // session permanently "Preparing voice" (yellow), never moving it to red once the voice is ready.
        // The voice-ready flip arrives on no Director push, so only the 5s backstop sweep would catch it -
        // and without this enrichment the sweep re-derives the same yellow every tick and the change gate
        // suppresses the update forever. The roster path enriched these (GatewayHost roster map below), so
        // the browsers folded red while the push-only desktop stuck yellow. Same source, same answer.
        // Hosted Multi-Tenancy (session-serving): NOT yet converted to the per-tenant pass.
        // BLOCKED ON: partitioning FleetDisplayStateObserver._lastSent by tenant - same shape as FleetRoles
        // above (session-id-keyed, pruned against the current pass's snapshot), so a per-tenant pass would
        // turn the display-state fold into a stamp storm every 5 seconds.
        FleetDisplayState = new Fleet.FleetDisplayStateObserver(
            () => PushedSessions.SnapshotFresh(TenantId.Local, AutoDismissStaleAfter),
            sessions => EnrichVoiceThenFoldForPush(
                sessions,
                voiceGeneratingFor: sid => _voiceService?.IsGenerating(TenantId.Local, sid) == true,
                voiceAudioReadyFor: sid => _voiceService?.HasVoice(TenantId.Local, sid) == true,
                needsYouStampFor: (sid, isRed) => _needsYouClock.Stamp(sid, isRed),
                snoozeRegistry: _snoozeRegistry),
            SendCommandAsync);
        // Mission Screen mission (Phase 1b, issue #1405): the mission-WHY store, at a Gateway-side file
        // (CcStorage.Root(), the same location the snooze and cron stores use). Loaded here so a Gateway
        // restart re-serves every WHY. Tests MUST pass an isolated path so they never touch the real store.
        // Mission WHY notes now persist in the mission_notes table of the EF data layer. The path argument is
        // the LEGACY mission-notes.json, imported once on first upgrade (quarantine-on-corrupt, boot empty -
        // a cosmetic store must not block boot) then renamed aside. Tests MUST pass an isolated path so they
        // never touch the real legacy file.
        _missionNotes = new MissionNotes.MissionNoteStore(_gatewayDb, missionNotesPath ?? Path.Combine(CcStorage.Root(), "mission-notes.json"));
        // Cron-job definitions persist across a Gateway restart (epic #479, #482) in the cron_jobs table
        // (next-run times recomputed on load). The path argument is the LEGACY cronjobs.json, imported once
        // on first upgrade then renamed aside. Tests MUST pass an isolated path so they never touch the real
        // legacy file.
        _cronJobs = new CronJobStore(_gatewayDb, cronJobsPath ?? Path.Combine(CcStorage.Root(), "cronjobs.json"));
        // Cron run history + the firing engine (epic #479, #483). The engine resolves each due job's
        // target Director from the registry and starts a session over the shared client (the same
        // path the work-list runner uses). The background sweep timer is started in StartAsync. The path
        // argument is the LEGACY cronruns.json, imported once then renamed aside.
        _cronRuns = new CronRunHistoryStore(_gatewayDb, cronRunsPath ?? Path.Combine(CcStorage.Root(), "cronruns.json"));
        }
        finally
        {
            startupSystemScope?.Dispose();
        }
        // The Gateway-hosted DevThrottle credential service (issue #636, Gateway Centralization Phase 2
        // foundation). Tests inject their own service over an isolated store; production builds the
        // service over the operating system credential store rooted under the Gateway config directory:
        // Windows Data Protection on Windows, the login Keychain on macOS. Linux has no local credential
        // store here (a headless container has no Keychain and no secret service), so on Linux Account
        // stays null - an explicit, logged null, not a silent fallback; a local install keeps its data
        // local and the hosted Supabase story owns Linux durable credentials later. The platform guards
        // also satisfy the platform-compatibility analyzer. Resolved BEFORE the telemetry queue so the
        // queue can attach the Gateway's own account token when forwarding (issue #639).
        if (account is not null)
            Account = account;
        else if (OperatingSystem.IsWindows())
            Account = CcDirector.Gateway.Account.GatewayAccountFactory.CreateForWindows();
        else if (OperatingSystem.IsMacOS())
            Account = CcDirector.Gateway.Account.GatewayAccountFactory.CreateForMac();
        else
            FileLog.Write("[GatewayHost] DevThrottle credential service not built: no local operating-system credential store on this platform (Linux); Account stays null");

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
        // POST /machines/{machine}/sessions relay ("start a session on another computer"). Gateway Cleanup
        // Phase 2 (PR E-B2): both the spawner and the work-list drain driver ride the tunnel. Tunnel-only:
        // an unconnected Director yields an error, never an HTTP dial.
        Api.DirectorCommandRouter.SendDirectorCommandAsync? spawnSendCommand = SendCommandAsync;
        _machineSessionSpawner = new Running.MachineSessionSpawner(cronTargetResolver, spawnSendCommand);
        // A work-list cron job (#484) drains a named list via the shipped #274 runner on the resolved
        // Director, launching the drain in the background on the shared runner manager. The tunnel-aware
        // driver factory closes over the stream hook so a cron drain creates + reads sessions down the tunnel.
        var cronWorkListRunner = new Running.DirectorCronWorkListRunner(
            _workLists,
            cronTargetResolver,
            _runnerManager,
            new Running.DirectorWorkListDrainLauncher(
                _workLists,
                (directorId, endpoint, repoPath) =>
                    new Running.DirectorImplSessionDriver(directorId, repoPath, spawnSendCommand)));
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
        // Web Push subscriptions now persist in the push_subscriptions table of the EF data layer. The path
        // argument is the LEGACY push-subscriptions.json, imported once on first upgrade then renamed aside.
        // Tests MUST pass an isolated path so they never touch the real legacy file.
        _pushSubscriptions = new Push.PushSubscriptionStore(_gatewayDb, pushSubscriptionsPath ?? Path.Combine(CcStorage.ToolConfig("gateway"), "push-subscriptions.json"));

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
                installIdProvider: _installIdStore.LoadOrCreate,
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
    /// The title of a session, for the wingman to speak first (WingmanTranslator.FidelityPrompt
    /// v5.2). Reads the pushed-session store, so it costs no round trip and stays inside the same
    /// stream-freshness window as every other stream-mode read.
    ///
    /// Returns null when the session is unknown or has no name, and that is deliberate rather than
    /// a placeholder: the prompt rule no-ops on a missing title, so the listener gets an untitled
    /// narration - whereas inventing something ("unknown session", the raw id) would either mislead
    /// or read out an identifier, which the same instructions explicitly forbid.
    /// </summary>
    /// <summary>
    /// The fresh fleet snapshot for the tenant of the CURRENT unit of work (Hosted Multi-Tenancy,
    /// session-serving PR2) - the request scope, the tunnel connection scope, or the per-tenant background
    /// pass. Self-host always resolves Local, so this is the same read as before. On hosted with no scope in
    /// effect it is EMPTY, which is the deny: a sweep with no tenant sweeps nothing rather than reading a
    /// partition that is not its own.
    /// </summary>
    private IReadOnlyList<(string DirectorId, SessionDto Session)> AmbientSnapshotFresh(TimeSpan staleAfter)
        => _tenantPass.Current is { } tenant
            ? PushedSessions.SnapshotFresh(tenant, staleAfter)
            : Array.Empty<(string DirectorId, SessionDto Session)>();

    /// <summary>
    /// Locate a session within the tenant of the CURRENT unit of work (see <see cref="AmbientSnapshotFresh"/>).
    /// Null when no tenant is in scope on hosted - the deny - as well as when the session is simply unknown.
    /// </summary>
    private (string DirectorId, SessionDto Session)? AmbientTryLocate(string sessionId, TimeSpan staleAfter)
        => _tenantPass.Current is { } tenant
            ? PushedSessions.TryLocate(tenant, sessionId, staleAfter)
            : null;

    private string? ResolveSessionTitle(string sessionId)
    {
        var located = PushedSessions.TryLocate(TenantId.Local, sessionId, _streamStaleAfter);
        var name = located?.Session.Name;
        return string.IsNullOrWhiteSpace(name) ? null : name;
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
            if (Registry.ListDirectors().Count == 0) return;
            // Gateway Cleanup mission, Phase 2: locate each voice session's owner push-store-first (no HTTP
            // fan-out) and reach it through the tunnel-only SessionVerbClient - an unconnected Director yields
            // an error, never an HTTP dial.
            var stale = TimeSpan.FromSeconds(Core.Configuration.GatewayConfig.DefaultStreamStaleAfterSeconds);
            Api.DirectorCommandRouter.SendDirectorCommandAsync? sendCommand = SendCommandAsync;
            var generated = 0;
            foreach (var sid in vs.VoiceSessionIds(TenantId.Local))
            {
                if (generated >= 3) break;          // gentle on the serialized brain
                if (vs.HasVoice(TenantId.Local, sid)) continue;     // already cached, nothing to do
                // Hosted Multi-Tenancy (session-serving): the voice sweep is NOT yet per-tenant.
                // The state it fills IS now partitioned (VOICE V1 - WingmanVoiceService holds a separate
                // bucket and a separate directory per tenant, and every read and mutate takes the tenant),
                // so the old blocker is gone. What remains is this loop and the /wingman/voice* routes: the
                // routes still resolve no tenant and pass Local, and /wingman/voice/ready still lists the
                // Local partition's ready ids. Converting THIS loop before those routes would generate into
                // per-tenant buckets that no route can read, so it stays on Local until the route work lands:
                // correct on self-host, and on hosted a Local read is empty (never a wrong-tenant read).
                // The per-tenant form is a loop over WingmanVoiceService.PartitionedTenants().
                var (director, session) = await Api.GatewayEndpoints.LocateSessionAsync(
                    Registry, sid, PushedSessions, stale, TenantId.Local, SessionOwners);
                if (director is null || session is null) continue;   // not owned by any known Director
                var st = session.ActivityState ?? "";
                if (st is "Idle" or "WaitingForInput" or "WaitingForPerm")
                {
                    FileLog.Write($"[GatewayHost] voice sweep: pre-building voice for idle session {sid}");
                    // A pre-build is not a new turn - generate quietly so an idle session a client
                    // may be listening to is never flipped yellow mid-play (issue #1322).
                    var route = new Api.SessionVerbClient(director, sendCommand);
                    await vs.GenerateAsync(TenantId.Local, sid, route, CancellationToken.None, showReadingWindow: false);
                    generated++;
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
        // gets an HTTPS mapping without anyone re-running a script. Skipped when HOSTED: a
        // hosted Gateway is reached by its public URL, not a tailnet, and the image bundles
        // no tailscale binary, so provisioning would only produce noise.
        if (!GatewayHostedMode.IsHosted)
            _serveProvisioner.Start();
        Registry.Start();

        // Issue #331: start the stale-launcher sweep timer so launchers that crash
        // without unregistering are evicted after 90 s.
        Launchers.StartSweep();

        // Registry is now loaded with the current Director set: run the first self-healing
        // reconcile - re-assert the front door, drop serve mappings for Directors that died
        // while the Gateway was down (orphans -> 502 from a phone), and sweep any leaked
        // ephemeral-port mappings (issue #179). The provisioner repeats this on a timer.
        // Skipped when HOSTED (no tailnet, no tailscale binary).
        if (!GatewayHostedMode.IsHosted)
            _serveProvisioner.Reconcile();

        // Gateway Cleanup mission (post-cut): the advertised-endpoint re-verification monitor (issue #325)
        // is DELETED. It HTTP-probed each Director's advertised /healthz; post-cut liveness is the tunnel
        // connection itself, so there is no advertised HTTP endpoint to re-verify.

        // Issue #549: the always-on turn-brief stamping pipeline is retired. TurnEndWatcher stays
        // and runs unconditionally - a small always-running watcher whose only job is firing voice
        // auto-refresh for voice sessions on turn-end, and clearing the stale voice/text cache on
        // the Working transition. It no longer depends on a brief agent existing. PUSH-fed since
        // #186 by Director doorbell pings and heartbeat snapshots (wired into the endpoints below);
        // the only pull left is the one-time startup catch-up sweep.
        FileLog.Write("[GatewayHost] StartAsync: starting the turn-end watcher (voice auto-refresh only; turn-brief pipeline retired in #549)");
        // sessionTitleResolver: the wingman opens every narration with the session's title, so a
        // listener with the phone in a pocket knows WHICH session is talking before anything else
        // (WingmanTranslator.FidelityPrompt v5.2). Push-store read - no dial. See ResolveSessionTitle.
        _voiceService ??= new Wingman.WingmanVoiceService(WingmanBrainAsync, _keyVault, training: _trainingStore, instructionsProvider: () => _instructionsStore.ActiveContent, sessionTitleResolver: ResolveSessionTitle);
        _turnEndWatcher = new TurnEndWatcher(
            onTurnEnd: signal =>
            {
                // Governance capture (issue #1771, spine item 3): record this session's cumulative spend at
                // turn-end from the pushed roster snapshot. Runs for EVERY session (not just voice), and is
                // isolated so a spend hiccup never breaks the voice refresh below - the failure is logged loud,
                // not swallowed into a fabricated value.
                try
                {
                    if (PushedSessions.TryLocate(TenantId.Local, signal.SessionId, _streamStaleAfter) is { } spendLoc)
                        _sessionSpendEmitter.Emit(spendLoc.Session);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[GatewayHost] turn-end spend emit FAILED: sid={signal.SessionId}: {ex.Message}");
                }

                // Voice sessions (issue #531): the turn just finished on its own, so re-make the
                // spoken summary + audio in the background. It is then "voice ready" in the session
                // list with no wait. Non-voice sessions do nothing here - the watcher is voice-only.
                if (_voiceService is { } vs && vs.IsVoiceSession(TenantId.Local, signal.SessionId))
                {
                    // Gateway Cleanup mission, Phase 2: reach the owning Director (carried on the signal as
                    // its DirectorId) through the tunnel-first SessionVerbClient - no HTTP dial. The Director
                    // may be push-only (empty control URL); the tunnel path still reaches it by id.
                    var director = Registry.Get(signal.DirectorId);
                    if (director is null) return;
                    Api.DirectorCommandRouter.SendDirectorCommandAsync? sendCommand = SendCommandAsync;
                    var route = new Api.SessionVerbClient(director, sendCommand);
                    FileLog.Write($"[GatewayHost] turn-end -> voice auto-refresh: sid={signal.SessionId} director={signal.DirectorId} newTurn={signal.IsNewTurn}");
                    // Show the yellow "wingman reading" hold only for a genuinely new turn; a startup
                    // catch-up of an earlier turn refreshes quietly so a listening client is not
                    // dropped out of the speaking screen (issue #1322).
                    _ = vs.GenerateAsync(TenantId.Local, signal.SessionId, route, CancellationToken.None, showReadingWindow: signal.IsNewTurn);
                }
            },
            onSessionWorking: sid =>
            {
                // Working again: the cached voice/text summary is now stale - clear it so the list
                // stops showing it ready and nothing stale plays (issue #531). It regenerates on the
                // next turn-end.
                _voiceService?.OnSessionWorking(TenantId.Local, sid);
            },
            // Gateway Cleanup mission, Phase 2: under stream mode the catch-up / reconcile reads the push
            // store instead of HTTP-pulling each Director's session list (no dial).
            pushedSessions: PushedSessions);
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
        // Issue #806 (mobile foundation): emit an OpenAPI document at /openapi/v1.json. The mobile
        // app's build-time codegen (openapi-typescript) turns it into a typed TypeScript client, so
        // the C# DTOs stay the single source of truth for the front-end.
        builder.Services.AddOpenApi();

        // Issue #1176 (Phase 1a): the Director-push stream. The hub and its two collaborators are
        // registered as singletons so the hub (constructed per-invocation by SignalR's container) and the
        // /sessions aggregation (wired explicitly below) share the one PushedSessionStore instance.
        // Gateway Cleanup mission, Phase 0 (up-stream): set the DirectorHub's message-size and stream-buffer
        // bounds from the shared DirectorStreamLimits so the hub and the Director's producer can never drift
        // (Architect ruling 2 + 1). MaximumReceiveMessageSize must admit a full-size framed binary frame
        // (the cap plus a small envelope allowance); StreamBufferCapacity is small so a slow browser sink
        // pushes back onto the producer's yield - the backpressure invariant, not an optimization.
        // AddMessagePackProtocol is ADDITIVE: the hub now negotiates MessagePack OR JSON, so a MessagePack
        // Director gets binary framing (a 48KB byte[] up-stream frame stays ~48KB, not ~64KB base64 that would
        // blow past MaximumReceiveMessageSize and drop the connection) while a JSON-only client still connects
        // (backward-compat for the fleet rollout). It removes the 33% base64 tax on every tunnel byte.
        builder.Services.AddSignalR()
            .AddMessagePackProtocol()
            .AddHubOptions<Streaming.DirectorHub>(o =>
            {
                // The receive cap must admit the LARGER of a framed up-stream message (chunked at
                // MaxBinaryFrameBytes) and a single unary command REPLY (turns / full buffer can be MBs, sent as
                // one client-result message). Using only the frame size tore the tunnel down on any large read
                // ("maximum message size ... exceeded"), which 500'd voice/History and dropped the roster. The
                // up-stream producer still chunks at MaxBinaryFrameBytes, so backpressure is unchanged.
                o.MaximumReceiveMessageSize = Contracts.DirectorStreamLimits.MaxInboundMessageBytes;
                o.StreamBufferCapacity = Contracts.DirectorStreamLimits.StreamBufferCapacity;
            });
        builder.Services.AddSingleton(PushedSessions);
        // Gateway Cleanup mission (Wave 4b): the Gateway-native mission store, so the mission endpoints and
        // spawn validation share the one instance.
        builder.Services.AddSingleton(Missions);
        // Gateway Cleanup Phase 0: the one up-stream registry the DirectorHub (constructed per-invocation by
        // SignalR) pumps StreamUp frames into.
        builder.Services.AddSingleton(StreamRegistry);
        // DevThrottle Stats: the hub (constructed per-invocation by SignalR) folds each pushed session's
        // tally into this one aggregator instance, which the /stats dashboard reads.
        builder.Services.AddSingleton(InputStats);
        // Defect 20: the hub lands a deferred snooze's clock through this one observer instance the moment
        // the Director pushes up "the hold landed".
        builder.Services.AddSingleton(SnoozeLandings);
        builder.Services.AddSingleton(FleetRoles);
        builder.Services.AddSingleton(FleetDisplayState);
        // Register the tenancy seam as the SAME instance GatewayDatabase reads (Hosted Multi-Tenancy
        // increment 1), so a scope a SignalR-hosted boundary enters is exactly what the stores resolve. On
        // self-host this is the SingleTenantContext (always Local); on hosted it is the AsyncLocalTenantContext.
        builder.Services.AddSingleton<CcDirector.Core.Tenancy.ITenantContext>(_tenantContext);
        // The auth-boundary tenant binder, so the SignalR-constructed DirectorHub resolves a tenant from the
        // authenticated device key at Hello and enters that scope on every push (Hosted Multi-Tenancy incr 1).
        builder.Services.AddSingleton(_tenantBoundary);
        builder.Services.AddSingleton(SessionConcurrency);
        builder.Services.AddSingleton(Registry);
        // launcher-persistent-join: the LauncherHub (constructed per-invocation by SignalR) and
        // SendLauncherCommandAsync share this one connection registry.
        builder.Services.AddSingleton(LauncherConnections);

        // Honor X-Forwarded-Proto/Host/For from the TLS-terminating front end so ctx.Request.Scheme
        // reflects the public scheme the user actually used. Which senders may be believed differs
        // between the two deployments - self-host trusts loopback only (Tailscale Serve), hosted must
        // also accept the Azure App Service front end, which forwards from a non-loopback platform
        // address (issue #1870). See ForwardedHeadersPolicy for why that is safe there and only there.
        var isHostedDeployment = _tenantBoundary.IsHosted;
        builder.Services.Configure<ForwardedHeadersOptions>(
            o => Tenancy.ForwardedHeadersPolicy.Apply(o, isHostedDeployment));

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
                    // The keep-warm heartbeat (P2) hits /diag/ping every ~25s per client - warming traffic,
                    // not a request worth logging; skip it so it does not flood the access log.
                    && !path.Equals("/diag/ping", StringComparison.OrdinalIgnoreCase)
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
            // surface, but it is no longer the path a NEW device uses to get in (that is account
            // sign-in - see SignedInEnrollmentEndpoint).
            var requireToken = new AuthMiddleware.RequireToken { Token = Token, Devices = Devices };
            _app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, requireToken, next));
        }

        // Hosted Multi-Tenancy increment 1: the device-key HTTP boundary. After auth, resolve this request's
        // tenant from the AUTHENTICATED device key the auth middleware stashed, and enter that scope for the
        // rest of the pipeline, so tenant-scoped stores read the caller's tenant - derived from the verified
        // credential, never from client input. Only device-key-authenticated requests carry a key; a
        // shared-token or public request enters no scope, so any tenant-scoped store access it makes fails
        // closed on hosted (deny-by-default), while non-tenant endpoints (enrollment, health) still work.
        // Registered only on the hosted Gateway; on self-host Local is the ambient answer (nothing to enter).
        if (_tenantBoundary.IsHosted)
        {
            _app.Use(async (ctx, next) =>
            {
                var key = ctx.Items.TryGetValue(AuthMiddleware.DeviceKeyItemKey, out var k) ? k as string : null;
                var tenant = key is null ? (Core.Tenancy.TenantId?)null : _tenantBoundary.ResolveForDeviceKey(key);
                if (tenant is { } resolved)
                {
                    using (_tenantBoundary.EnterScope(resolved))
                        await next();
                }
                else
                {
                    await next();
                }
            });
        }

        // Mobile front door (issue #806, docs/architecture/mobile/): a phone browser-navigation
        // (Accept: text/html, phone User-Agent) not already under the mobile app gets a 302 to the mobile
        // app at /mobile/; a desktop UA falls through unchanged to the Cockpit. After auth, before the
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

        // Issue #1176 (Phase 1a): the Director-push stream endpoint (the tunnel). The tunnel/hubs are
        // mandatory and always mapped. Mapped after the host-wide auth middleware above, so the handshake
        // is token-gated exactly like every other route; a Director's .NET SignalR client presents its
        // Bearer token on the handshake.
        _app.MapHub<Streaming.DirectorHub>("/director-stream");
        FileLog.Write("[GatewayHost] DirectorHub mapped at /director-stream; /sessions serves from the push cache when fresh");

        // launcher-persistent-join: the launcher-push stream endpoint, also mandatory. When a launcher
        // joins, the machine lifecycle relay pushes commands DOWN this stream instead of dialing the
        // launcher's REST API.
        _app.MapHub<Streaming.LauncherHub>("/launcher-stream");
        FileLog.Write("[GatewayHost] LauncherHub mapped at /launcher-stream; machine lifecycle relay prefers the stream when a launcher is joined");

        // Product version stamped by Directory.Build.props; full form carries the commit SHA.
        var version = AppVersion.Full;
        // Network Diagnostics mission (P1): the shared hourly quality rollup - POST /diag/result folds
        // client results into it, and the monitor (started below) folds its per-tick observations into the
        // same instance, so both writers share one thread-safe in-memory state + file.
        _netDiagRollup = new Api.NetDiagRollupStore(Path.Combine(CcStorage.Root(), "netdiag-rollup.json"));

        // Gateway Cleanup mission: the cut removed the DirectorEndpointClient argument (_client) - the
        // Gateway no longer dials Directors over HTTP, so Map no longer takes an HTTP client. The
        // network-diagnostics rollup store is threaded in as a named argument on the tunnel-only signature.
        GatewayEndpoints.Map(_app, Registry, version, Token, AuthEnabled,
            netDiagRollup: _netDiagRollup,
            // Store injection points: hand the phone-recorder ingest (RecordingEndpoints) the host's single
            // key vault + transcription telemetry + audio archive, so it stops newing its own copies.
            recordingKeyVault: _keyVault,
            transcriptionTelemetry: _transcriptionTelemetry,
            transcriptionAudioArchive: _transcriptionAudioArchive,
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
                    _voiceService?.OnSessionWorking(TenantId.Local, sessionId);
                if (_turnEndWatcher is null) return;
                // Gateway Cleanup mission, Phase 2: the doorbell/heartbeat already carries the owning
                // directorId, so feed THAT to the watcher (the voice-refresh path reaches the Director
                // through the tunnel by id) instead of converting it to a dialable control URL.
                _turnEndWatcher.Observe(sessionId, newState, directorId);

                // Governance capture (issue #1771, spine item 2): record this session's state transition on
                // the append-only ledger (emits only on a real change; isolated so a ledger hiccup never
                // breaks the turn tracking above).
                try
                {
                    _sessionStateEmitter.Observe(sessionId, newState);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[GatewayHost] session-state event emit FAILED: sid={sessionId}: {ex.Message}");
                }
            },
            // Issue #549: the assessed-state refutation (issue #186) is dropped with the pipeline
            // (Option A) - "needs you" reverts to the Director's raw mechanical signal. The
            // turn-brief stamping (issue #187 briefStampFor) is gone too; the brief agent that
            // wrote those fields no longer exists.
            // Voice mode (issue #531): while the gateway's wingman is producing a session's spoken
            // summary, paint it yellow ("not ready yet") and back to red. Independent of any brief
            // agent and never via the Director's --print explain.
            voiceGeneratingFor: sid => _voiceService?.IsGenerating(TenantId.Local, sid) == true,
            // Issue #553: whether the gateway has fetchable, playable cached audio for this session -
            // the single truthful "voice you can play right now" signal. Holds a voice-mode waiting
            // session yellow until this is true, then lets it go red (SessionOrdering.IsVoicePreparing).
            voiceAudioReadyFor: sid => _voiceService?.HasVoice(TenantId.Local, sid) == true,
            // Issue #939: when turn-end voice could not be kept because hosted AI is unavailable (out
            // of credits / cap / no key), stamp the shared unavailable state onto the session so the UI
            // shows the consistent add-credit / add-key message instead of a silently missing triangle.
            voiceUnavailableFor: sid => _voiceService?.VoiceUnavailableFor(TenantId.Local, sid),
            // The last turn has no text reply to read aloud (waiting on a prompt / menu). Feeds the folded
            // VoiceDisplay so the screen shows an honest "nothing to read aloud" instead of a Generate
            // button that cannot work - the client no longer rules on this.
            nothingToNarrateFor: sid => _voiceService?.NothingToNarrateFor(TenantId.Local, sid) == true,
            // TTS fallback: this session's ready clip was made by the backup voice provider (the primary
            // was overloaded and the cloud proxy failed over). Feeds the folded VoiceDisplay so the screen
            // shows the generic backup-voice notice. A success-with-a-note, never an outage state.
            servedViaFallbackFor: sid => _voiceService?.ServedViaFallbackFor(TenantId.Local, sid) == true,
            // Issue #218: stamp the Gateway-owned NeedsYouSince entry clock onto each session.
            needsYouStampFor: (sid, isRed) => _needsYouClock.Stamp(sid, isRed),
            // Stamp the orange "Transcribing..." flag while a dictated utterance is being uploaded
            // and transcribed in the background for this session (mobile Speak -> Send).
            transcribingFor: sid => _transcribingSessions.IsTranscribing(sid),
            // Issue #1181, Task 4: the honest phase label. "Transcribing" while the server is actively
            // turning the uploaded audio into text (a bounded run); "Uploading from phone" while the durable
            // PENDING marker stands AND the phone is still making progress; null when no dictation is
            // inbound.
            //
            // ONE FLAG WAS ANSWERING TWO QUESTIONS, and that was defect 19 (fixed 14 July 2026, mission
            // "Session State Truth"). The durable PENDING marker answers "is there an undelivered dictation
            // for this session?" - a durable fact that must NEVER expire, or a phone out of signal loses its
            // words. It was ALSO being used to answer "should this session be painted orange right now?" - a
            // presentation question that must ALWAYS be bounded. So an upload that stopped progressing left
            // the session orange indefinitely, reading "Uploading from phone" about an upload that was not
            // happening.
            //
            // The colour is now bounded by the SAME idle rule the transcribing mark already uses
            // (TranscribingSessions.IdleTimeout): the phone refreshes the mark on every stored chunk and
            // every completion attempt, so a genuinely slow upload keeps its label and is never cut short,
            // while one that goes quiet drops back to the session's true colour within the idle window.
            //
            // THAT "REFRESHES ON EVERY CHUNK" IS TRUE BY INSPECTION AND UNGUARDED BY ANY TEST. It is
            // GatewayDictationEndpoint.cs: Begin on register, Refresh in the chunk route, Refresh on the
            // completion attempt. The producer tests below drive TranscribingSessions directly, so they
            // prove the RULE reads the mark - they do not prove the ROUTES still write it. Delete the
            // Refresh in the chunk route and every test in this repository stays green while a slow real
            // upload stops painting mid-flight. It is the milder cousin of defect 19 (a label that goes
            // quiet too early, rather than one that never shuts up) and it loses no words, but it is
            // written here as a known gap rather than left to read as covered: proving it needs an
            // endpoint-level test that drives the real chunk route, and there is no host harness for these
            // routes yet. Raised by review of pull request 1588; accepted deliberately, not overlooked.
            //
            // The user's AUDIO is retained either way and still delivers whenever the phone returns - the
            // delivery submits text, which makes the agent work, which is blue. Note the precise claim:
            // the CHUNKS are kept and the record is never discarded or expired. The record itself is NOT
            // untouched - a retryable or out-of-credits transcription now parks it Pending -> Failed, and
            // Failed is a resting state that keeps the audio and re-drives on the next register/complete,
            // not a terminal one. Nothing is lost except the lie on the dot.
            //
            // The earlier comment here claimed this "never wedges because the marker clears only on
            // delivery/abandon". That was half-true and the half it left out WAS the bug: the marker does
            // clear on delivery, so the normal path never wedges - but the paths that reach no terminal state
            // at all never clear it. Observed: upload f13cb4b6d9d0 stood PENDING 1h30m on 12 July 2026,
            // orange the whole time, across four Gateway restarts (so "it clears on restart" is false too -
            // the record is on disk), before finally delivering 362 characters.
            // The rule itself lives in DictationPhase.For so it is testable without a running Gateway, and
            // the WIRING that supplies its three facts lives in DictationStatusFor so it is testable too -
            // see that method for why an inline lambda here was a hole rather than a style choice.
            dictationStatusFor: sid => DictationStatusFor(sid, _transcribingSessions, _dictationUploads),
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
            pushedSessions: PushedSessions,
            streamStaleAfter: _streamStaleAfter,
            // Issue #1177 (Phase 1): route per-session commands DOWN the Director's stream when stream mode
            // is on. Null when off, so every command endpoint stays on its HTTP path (byte-identical).
            sendCommand: SendCommandAsync,
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
            inputStats: InputStats,
            // DevThrottle Stats: record fleet concurrency (live + actively-working session counts) from the
            // same assembled roster, so the peak is captured fleet-wide regardless of stream mode.
            concurrency: SessionConcurrency,
            // Snooze Length mission: the Gateway-owned snooze registry. POST /sessions/{sid}/hold records
            // (or clears) a snooze-until here, and the /sessions fold overlays an EXPIRED snooze back into
            // "needs you" so the session returns on its own even after its Director dies.
            snoozeRegistry: _snoozeRegistry,
            // Gateway Cleanup mission (Wave 4b): the Gateway-native mission store backs POST/GET /missions and
            // mission-scoped spawn validation. Missions are a fleet concept, so their source of truth is here.
            missions: Missions,
            // Round 4 finding 1: the hold endpoint triggers a prompt display-state push through this ONE
            // channel after a snooze / unsnooze, instead of sending its own second hold command - the single
            // writer of the Director's raw hold.
            fleetDisplayState: FleetDisplayState,
            // Workflows mission (phase 4, issue #1771): creating a mission also opens a workflow run of
            // the built-in "mission" workflow, pinned to its published version - the outcome spine.
            workflowRuns: _workflowRuns,
            // Hosted Multi-Tenancy (session-serving PR1): the read endpoints resolve the request's tenant from
            // the authenticated device key through this boundary and deny (403) when hosted binds none.
            tenantBoundary: _tenantBoundary);

        // Issue #268: the two raw per-session WebSocket legs (live Terminal stream + dictation)
        // proxied through the Gateway so a remote Cockpit talks same-origin to the Gateway and
        // never needs a Director's own (possibly loopback) address. Mapped endpoints win over the
        // fallback Cockpit proxy below.
        // Pass the fleet token (issue #457): the proxy injects it as the Bearer on every forward
        // so an auth-enabled Director (LAN mode) accepts the call. Harmless for auth-off Directors.
        // Gateway Cleanup mission (the cut): the browser-facing per-session legs (terminal, file, screenshot
        // bytes/list/delete) and the director-level backfill all ride the tunnel - no HTTP dial to a Director
        // remains. StreamRegistry is the SAME singleton the DirectorHub pumps StreamUp frames into.
        SessionWsProxyEndpoints.Map(_app,
            pushedSessions: PushedSessions,
            streamRegistry: StreamRegistry,
            sendCommand: SendCommandAsync,
            streamStaleAfter: _streamStaleAfter,
            // Hosted Multi-Tenancy (session-serving PR1): the per-session tunnel legs resolve the request's
            // tenant at the session locate, so a wrong-tenant session is never located and a request with no
            // bound tenant is denied (403) - never a Local read on hosted.
            tenantBoundary: _tenantBoundary);

        // GET /devices: the host-readable device registry listing. Mapped after the WS proxy so its
        // literal route wins over the catch-all session forwarder, same as the other literal routes.
        Api.DeviceEnrollmentEndpoint.Map(_app, Devices);

        // POST /devices/enroll-signed-in (issue #1069): the sign-in replacement for the pairing code -
        // a co-located Director mints its own per-device key by having the Gateway signed in to
        // DevThrottle, gated on a loopback caller. Same-machine only; remote via tailnet is a follow-up.
        Api.SignedInEnrollmentEndpoint.Map(_app, Devices, SignIn, _childMirror);

        // The hosted-mint dependencies, built ONCE for every hosted entry point that mints a tenant-scoped
        // device key: the hosted Director enrollment below and the hosted /mobile/enroll branch both pass this same
        // bundle into the ONE mint (HostedEnrollmentEndpoint.Enroll). Non-null only on a hosted Gateway; null
        // keeps every entry point on its self-host path. Building the account-token validator here once means
        // both share the identical signature/audience/issuer configuration - there is no second place that
        // validates an account token.
        Api.HostedEnrollDependencies? hostedEnrollDeps = GatewayHostedMode.IsHosted
            ? new Api.HostedEnrollDependencies(
                Devices, TenantRegistry,
                CcDirector.Gateway.Account.GatewayAccountFactory.BuildAuthorizationValidator(),
                EntitlementRegistry)
            : null;

        // POST /devices/enroll-hosted (Hosted Multi-Tenancy increment 1): the HOSTED counterpart - a REMOTE
        // Director enrolls by presenting its OWN verified Supabase account token; the Gateway validates it,
        // resolves the account's tenant, and binds the minted device key to it. Only mapped on the hosted
        // Gateway (self-host uses the loopback signed-in route above and stays single-tenant Local).
        if (hostedEnrollDeps is not null)
        {
            // The paid gate rides along here and ONLY here. This route is mapped on hosted only, so passing
            // the entitlement registry means the gate is active wherever enrollment is possible - self-host
            // never maps this route at all and therefore cannot be gated by accident.
            Api.HostedEnrollmentEndpoint.Map(_app, hostedEnrollDeps.Devices, hostedEnrollDeps.Tenants,
                hostedEnrollDeps.AccountTokenValidator, entitlements: hostedEnrollDeps.Entitlements);
        }

        // Wingman-voice surface for the Cockpit's Voice tab (issue #531): drive one turn of a
        // session and have the persistent wingman brain translate the reply into speakable form,
        // plus the direct-to-wingman path. Backed by the same warm Brain the brief agent uses.
        // sessionTitleResolver: the wingman opens every narration with the session's title, so a
        // listener with the phone in a pocket knows WHICH session is talking before anything else
        // (WingmanTranslator.FidelityPrompt v5.2). Push-store read - no dial. See ResolveSessionTitle.
        _voiceService ??= new Wingman.WingmanVoiceService(WingmanBrainAsync, _keyVault, training: _trainingStore, instructionsProvider: () => _instructionsStore.ActiveContent, sessionTitleResolver: ResolveSessionTitle);
        GatewayWingmanVoiceEndpoint.Map(_app, Registry, WingmanBrainAsync, _keyVault, _voiceService,
            pushedSessions: PushedSessions,
            sendCommand: SendCommandAsync,
            owners: SessionOwners,
            instructionsProvider: () => _instructionsStore.ActiveContent,
            // Store injection points: hand the endpoint the host's single voice-turn upload store and the
            // host's transcription telemetry + audio archive, so it stops newing its own copies.
            uploadStore: _voiceTurnUploads,
            telemetry: _transcriptionTelemetry,
            audioArchive: _transcriptionAudioArchive);

        // Car Mode brain (Car Mode mission, New build A): the fleet tool-calling loop behind
        // POST /carmode/turn. The chat transport resolves the fast wingman model + the vault key at CALL
        // time (a settings change applies on the next turn, no restart); the fleet tools reach THIS
        // Gateway's own endpoints over loopback (the same aggregated roster every client sees); the
        // conversation context is kept server-side per device. Inherits the host-wide auth gate (the
        // caller's per-device key), like every other data route.
        var carModeChat = new CarMode.HostedCarModeChat(CarMode.HostedCarModeChat.DefaultResolver(_keyVault.Get));
        var carModeFleet = new CarMode.LoopbackCarModeFleet(Port, Token);
        var carModeBrain = new CarMode.CarModeBrain(carModeChat, carModeFleet, _carModeConversations, _carModePending, _carModeSubjects);
        // Keep-warm (Car Mode performance round): warm the SAME hosted model the brain uses and the SAME
        // text-to-speech target /wingman/tts uses, resolved fresh each warmup so a settings change applies.
        var carModeWarmup = new CarMode.CarModeWarmup(
            CarMode.HostedCarModeChat.DefaultResolver(_keyVault.Get),
            () =>
            {
                var mode = Core.Configuration.TranscriptionModeConfig.Get();
                var tts = Core.Configuration.TranscriptionEndpointResolver.ResolveTts(mode);
                var key = _keyVault.Get(tts.KeyName) ?? "";
                return (tts.BaseUrl, Core.Configuration.TtsVoiceConfig.Resolve(mode), Core.Configuration.TtsModelConfig.Resolve(mode), key);
            });
        Api.CarModeEndpoint.Map(_app, carModeBrain, _carModeTurnCache, _carModeTelemetry, carModeWarmup);
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
        GatewayDictationEndpoint.Map(_app, Registry, SessionOwners, Token,
            _dictationTranscription ?? new Transcription.GatewayTranscriptionService(_keyVault, telemetry: _transcriptionTelemetry, audioArchive: _transcriptionAudioArchive), _transcribingSessions, _dictationUploads, Devices,
            pushedSessions: PushedSessions,
            sendCommand: SendCommandAsync);
        // Durable per-upload-id dictation record (issue #1183): a PENDING upload's chunks are retained
        // until it becomes DELIVERED or ABANDONED, and the delivered/abandoned tombstone (the durable
        // de-dupe marker) is retained until the client acknowledges it - so an undelivered dictation
        // survives time and a restart, and a delivered upload id is de-duplicated forever. There is
        // deliberately NO age sweep here: a fixed age cut would reopen exactly the hole this closes - a
        // phone out of signal for longer than the cut would lose its already-uploaded chunks, or re-inject
        // an already-delivered turn. The record is retired only by the client ack
        // (POST /dictation/{uploadId}/ack). The unrelated voice-turn upload staging is transient and IS age
        // swept - by the timer started in StartAsync (see the voice-turn upload sweep there). That sweep was
        // written long before it was ever started: for its whole life this comment said the voice-turn
        // staging "keeps its own transient SweepAbandoned" while nothing in production called the method, so
        // a reader checking whether the staging was bounded found a confident answer here and stopped
        // looking. It is bounded now because a timer runs it, not because a method exists.

        // Central key vault (docs/architecture/gateway/GATEWAY_KEY_VAULT.md): set keys once
        // here (via the Cockpit Keys page); Directors pull them on demand. Inherits the
        // host-wide token middleware above.
        VaultEndpoints.Map(_app, _keyVault);

        // The AI model catalog + test surface for the Settings AI tab (list the selected provider's
        // models, test a chat model, save the chosen wingman/speech model). Uses the vault credential.
        Api.AiModelsEndpoint.Map(_app, _keyVault);

        // The workflow catalog (issue #1617; persisted by the Workflows mission): the shapes of work
        // the fleet knows how to run - Mission, Standalone, Standalone with review, plus user-defined
        // workflows. The Gateway is the home for these; Directors and the Cockpit ask it rather than
        // each carrying a private copy. Served from the persisted store (built-ins seeded at startup);
        // authoring routes are the next phase. Inherits the host-wide token middleware above.
        Api.WorkflowEndpoints.Map(_app, _workflows);

        // Workflow runs (phase 4, issue #1771): the outcome spine's REST surface. One row per
        // execution of a workflow, pinned to the exact published version that governed it.
        Api.WorkflowRunEndpoints.Map(_app, _workflowRuns);

        // The governance event ledger (issue #1771, spine item 2): append-only session/run state
        // transitions - the duration timeline the weekly Outcome Ledger reads. Append and read only.
        Api.GovernanceEventEndpoints.Map(_app, _governanceEvents);

        // Honest driver-normalized spend (issue #1771, spine item 3): per-session token effort + billing-mode
        // label, and the account-level hosted-AI service dollars read from the mirrored credit-debit ledger.
        Api.GovernanceSpendEndpoints.Map(_app, _sessionSpend, _hostedAiSpend);

        // The governance audit trail (issue #1771, spine item 4): append-only intervention +
        // permission/sandbox decisions - the safety and attention-burden audit. Append and read only.
        Api.GovernanceAuditEndpoints.Map(_app, _governanceAudit);

        // The weekly Outcome Ledger (issue #1771, spine item 4): the first report that pays rent - verified
        // yield, aging WIP, and high-effort/no-outcome runs with cost + attention-burden. Read-only.
        Api.GovernanceReportEndpoints.Map(_app, _outcomeLedger);

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
        // not-signed-in. Issue #1357: it also resolves the signed-in user's chosen nickname (cached,
        // best-effort) through the cloud nickname client, so a session's preamble can name the human;
        // the identity email/provider path stays entirely local.
        // Issue #1856: the boundary and the tenant registry make this endpoint tenant-bearing on hosted, where
        // it must answer about the CALLER's enrollment rather than about a Gateway credential hosted does not
        // hold. On self-host the boundary reports not-hosted and the endpoint behaves exactly as before.
        AccountStatusEndpoint.Map(_app, Account, new Core.Account.AccountNicknameClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }),
            tenantBoundary: _tenantBoundary, tenants: TenantRegistry);

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


        // The credential-free cloud sign-in START front door (epic #1069, issue #1076): GET + POST
        // /account/sign-in-start. This pair is on the public-paths
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
        TranscriptionBatchEndpoint.Map(_app, _keyVault, _transcriptionTelemetry, _transcriptionAudioArchive);

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
        WorkListRunnerEndpoints.Map(_app, _workLists, Registry, _runnerManager,
            SendCommandAsync);

        // Issue #331: launcher registration + cross-machine Director lifecycle relay.
        // Launchers POST /launchers/register on startup; relay callers POST
        // /machines/{machine}/director/restart|start|stop to reach that machine's Director.
        // launcher-persistent-join: pass the stream-send hook only when stream mode is on. The relay tries
        // this first and falls back to the REST relay when it returns null (stream off, or launcher offline).
        MachineEndpoints.Map(_app, Launchers, _machineSessionSpawner, SendLauncherCommandAsync,
            // Gateway Cleanup mission (Wave 4b): validate a mission-scoped spawn against the Gateway store and
            // stamp the resolved mission name onto the create request forwarded to the Director.
            missions: Missions,
            // Workflows mission (phase 5b): seat spawns on workflow runs and record participants.
            workflowRuns: _workflowRuns);

        // The Cockpit Settings page surface (docs/architecture/gateway/SETTINGS_OWNERSHIP.md):
        // one snapshot GET plus brain-restart and autostart actions. Reads this host directly
        // for status/brain; run mode + autostart come from SettingsHooks (GatewayApp-owned).
        SettingsEndpoints.Map(_app, this);

        // Gateway-served turn briefs (issue #185): the Cockpit and the interrupted/restore paths
        // read briefs from the store HERE. Issue #549 removed the only WRITER (GatewayTurnBriefAgent),
        // so the store is read-only-serving (effectively empty going forward); the read endpoints
        // stay so existing callers degrade cleanly. The explain trigger (#217) rode the brief agent,
        // which is gone - pass null and the explain endpoint answers 503.
        TurnBriefGatewayEndpoints.Map(_app, _turnBriefStore,
            sid => _turnBriefStore.Latest(sid) is not null ? "Briefed" : "None",
            requestExplainAsync: null);

        // Mission Screen mission (Phase 1b, issue #1405): the mission-WHY read/set surface. A device-authed
        // client route under /gateway/missions/notes - the host-wide token middleware gates it (proven by
        // MissionNotesEndpointTests). Deliberately NOT in GatewayEndpoints.cs and NOT on the bare /missions
        // prefix (the Gateway-native mission store owns that), so it stays clear of the Gateway Cleanup work.
        MissionNotesEndpoint.Map(_app, _missionNotes);

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

        // Mobile device enrollment (issue #908): POST /mobile/enroll (with the legacy /m/enroll kept as a
        // back-compat alias). A phone that signed in on devthrottle.com and received its per-device key
        // hands that key here; the Gateway confirms (account-scoped, by key hash) that the key belongs to
        // its OWN signed-in account and issues the phone a LOCAL device key it validates offline - so the
        // master token is no longer injected into the mobile shell. Under /mobile/ so it is reachable
        // before the phone holds any credential; it carries its own authorization (the account-scoped
        // device key), exactly like /devices/register. Mapped before the mobile shell so the explicit POST
        // route wins over the shell's GET catch-all.
        var mobileEnrollmentClient = new Core.Account.DeviceRegistryClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) });
        // On a HOSTED Gateway (hostedEnrollDeps non-null) /mobile/enroll takes a human's account access token
        // in the Bearer header and runs the ONE hosted mint; self-host (null) keeps the cloud-device-key-in-body path.
        Api.MobileEnrollmentEndpoint.Map(_app, new Account.MobileDeviceEnrollmentService(Account, mobileEnrollmentClient, Devices), hostedEnrollDeps);

        // DevThrottle Stats: the always-available private dashboard (/stats) and its JSON (/stats/data).
        // A self-contained embedded page, so it works even on a plain dev build with no React wwwroot.
        // Mapped before the mobile/cockpit catch-alls so the explicit routes win.
        Stats.StatsPageEndpoint.Map(_app, InputStats, SessionConcurrency);

        // The prompt log (issue #1551): Directors push what they captured to POST /prompts, and anyone
        // wanting history reads GET /prompts. It lives here, not on a Director, because the Gateway is
        // what the whole fleet reports to - so the history is already present rather than scattered
        // across machines - and because the Gateway is what moves to the server.
        Prompts.PromptEndpoints.Map(_app, _promptLog, _tenantBoundary);

        Mobile.MobileApp.Map(_app, Token);
        // The legacy /m mount: 301 to the canonical /mobile equivalent so installed phone PWAs and
        // bookmarks (and the sign-in callback devthrottle.com still hands back to /m/device-callback) keep
        // working. Mapped after the /mobile serving and the explicit POST /m/enroll route (both win by
        // being GET vs the same verb / a different verb), before the Cockpit catch-all.
        Mobile.MobileApp.MapLegacyRedirect(_app);

        // One URL (epic #967 cutover, issue #979): the React desktop Cockpit is the Gateway's
        // canonical front door. Everything no explicit endpoint above claimed - the shell at "/",
        // client-side routes, and the hashed static assets (built into wwwroot/c by the release-gated
        // MSBuild target) - resolves here. Mapped LAST by design, exactly like /mobile above. The Blazor
        // Server Cockpit and its fallback reverse-proxy were retired in this cutover.
        Cockpit.CockpitReactApp.Map(_app);

        await _app.StartAsync();
        FileLog.Write($"[GatewayHost] listening on http://0.0.0.0:{Port} (all interfaces, auth-gated; version {version})");

        // Cron firing sweep (epic #479, #483): wake ~every minute and fire due jobs. The first tick
        // also catches up a fire that came due while the Gateway was down (at most once per job).
        //
        // Skipped when HOSTED (Hosted Multi-Tenancy): the cron drain reads the tenant-scoped cron_jobs store,
        // which has no ambient tenant on a background timer and so would fail closed every tick. Firing cron
        // per-tenant is part of the session-serving increment (the same increment makes these sweeps
        // fail-loud/observed rather than fire-and-forget). Same guard shape as the NetDiag monitor below.
        if (!GatewayHostedMode.IsHosted)
        {
            _cronTimer = new System.Threading.Timer(_ => SweepCron(), null, CronSweepInterval, CronSweepInterval);
            FileLog.Write($"[GatewayHost] cron sweep started: every {CronSweepInterval.TotalSeconds:0}s");
        }
        else
        {
            FileLog.Write("[GatewayHost] cron sweep NOT started (hosted): per-tenant cron firing lands in the session-serving increment");
        }

        // Scheduled-run auto-dismiss (issue #1200): close automated runs that declared themselves done, by
        // sending the kill verb DOWN the Director stream. The close has no REST fallback by design (the
        // Gateway owns session lifecycle and reaches the Director through its stream).
        _autoDismissSweeper = new Running.AutoDismissSweeper(
            () => AmbientSnapshotFresh(AutoDismissStaleAfter),
            SendCommandAsync,
            // Partition the close-marks by the tenant of the pass currently running (see AutoDismissSweeper).
            // Null (no tenant in scope on hosted) is a DENY inside the sweeper, not an empty prefix.
            tenantKey: () => _tenantPass.Current?.Value);
        _autoDismissTimer = new System.Threading.Timer(_ => SweepAutoDismiss(), null, AutoDismissSweepInterval, AutoDismissSweepInterval);
        FileLog.Write($"[GatewayHost] auto-dismiss sweep started: every {AutoDismissSweepInterval.TotalSeconds:0}s");

        // The fold push backstop: re-fold the fleet and stamp changed answers down, catching the Gateway-only
        // overlays (voice, transcription, dictation, snooze expiry) that arrive on no Director push. Never
        // throws into the timer.
        _displayStateSweepTimer = new System.Threading.Timer(
            _ => { try { FleetDisplayState.Sweep(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] display-state sweep error: {ex.Message}"); } },
            null, DisplayStateSweepInterval, DisplayStateSweepInterval);
        FileLog.Write($"[GatewayHost] display-state sweep started: every {DisplayStateSweepInterval.TotalSeconds:0}s");

        // Voice-turn upload staging retention. The success path deletes an upload's staging directory when
        // the turn starts; everything else - a refused, dropped or never-completed upload - is bounded here
        // and only here. It runs on every deployment, self-host and hosted alike, because the audio it
        // removes is recorded speech and there is no deployment where keeping it forever is right.
        var voiceTurnUploadSchedule = VoiceTurnUploadSweepScheduleForTests ?? VoiceTurnUploadSweepInterval;
        _voiceTurnUploadSweepTimer = new System.Threading.Timer(
            _ =>
            {
                try { _voiceTurnUploads.SweepAbandoned(VoiceTurnUploadMaxAge); }
                catch (Exception ex) { FileLog.Write($"[GatewayHost] voice-turn upload sweep error: {ex.Message}"); }
            },
            null, voiceTurnUploadSchedule, voiceTurnUploadSchedule);
        FileLog.Write($"[GatewayHost] voice-turn upload sweep started: every " +
            $"{voiceTurnUploadSchedule.TotalMinutes:0.###}min, removing staging idle longer than " +
            $"{VoiceTurnUploadMaxAge.TotalHours:0.###}h");

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

        // No snooze expiry sweep. There used to be a 15-second watchdog here that retired an elapsed entry;
        // it was removed once the Gateway owned both the state and the clock, because expiry is a local fact
        // (HoldStateFor reports an elapsed entry as None on every read) and deleting the entry on a timer
        // erased the returned-by-timer badge before any consumer could see it. An elapsed entry now lingers
        // as a durable tombstone, retired only by an edge that ends a snooze; the registry is bounded by the
        // live-session prune paths. A no-op timer would be a smell, so there is none.

        // Governance capture (issue #1771, spine item 3): periodically mirror the account's real hosted-AI
        // service debits from the cloud credit-debit ledger into account_hosted_ai_spend, so the weekly
        // report shows an honest account-level "Hosted-AI services: $X" figure. Signed out is an expected
        // no-op (no fabricated spend); the store dedups the ledger's rolling window so over-polling is safe.
        // Skipped when HOSTED (Hosted Multi-Tenancy): mirroring writes the tenant-scoped account_hosted_ai_spend
        // store, which has no ambient tenant on a background timer and would fail closed. Per-tenant/account
        // spend mirroring lands in the session-serving increment.
        if (!GatewayHostedMode.IsHosted)
        {
            _hostedAiSpendSweep = new Governance.HostedAiSpendSweep(
                accessToken: () => Account?.GetAccessTokenForForwarding(),
                credits: new Core.Account.AccountCreditsClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }),
                store: _hostedAiSpend);
            _hostedAiSpendSweep.Start();
        }

        // Web Push (mobile app-icon "needs you" dot): start the background notifier now that this
        // Gateway's own /sessions endpoint is live on loopback. The notifier reads that endpoint (so its
        // "needs you" verdict is byte-identical to the roster's) and pushes the count to subscribed
        // phones. It self-gates on having at least one subscription, so it is free until a phone opts in.
        // Skipped when HOSTED (Hosted Multi-Tenancy): the notifier reads the tenant-scoped push_subscriptions
        // store on its timer, which has no ambient tenant and would fail closed. Per-tenant push lands in the
        // session-serving increment (which also makes the /sessions roster it reads tenant-aware).
        if (!GatewayHostedMode.IsHosted)
        {
            var pushSender = new Push.VapidWebPushSender(
                _vapidStore.PublicKey, _vapidStore.PrivateKey, "mailto:support@devthrottle.com");
            _pushNotifier = new Push.WebPushNeedsYouNotifier(_pushSubscriptions, GetNeedsYouCountAsync, pushSender);
            _pushNotifier.Start();
        }

        // Network Diagnostics monitor (Network Diagnostics mission, Phase 1): on a timer, watch each
        // connected device's direct-vs-relay path and log persistent home-relay drift QUIETLY. Alert
        // channels (doorbell + owner email) are wired in P5 onto the same Decide-machine state.
        //
        // Skipped when HOSTED: the monitor's collector shells out to the tailscale CLI, which the container
        // image does not bundle, so every poll would throw. A hosted Gateway has no tailnet to diagnose.
        if (!GatewayHostedMode.IsHosted)
        {
            var netDiagDeviceStore = new Api.NetDiagDeviceStore(Path.Combine(CcStorage.Root(), "netdiag-devices.json"));
            // P5: deliver the monitor's drift/resolve to the doorbell + owner email. The alert service owns the
            // 401-explicit "not signed in" path, the daily email cap, and one-email-per-episode; the monitor
            // just hands it the device name on the machine's rising/falling edges.
            var netDiagNotify = new Core.Account.AccountNotifyClient(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
            var netDiagAlerts = new Api.NetDiagAlertService(
                DirectorEvents,
                () => Account?.GetAccessTokenForForwarding(),
                async (token, subject, bodyText) =>
                {
                    var r = await netDiagNotify.SendOwnerAsync(token, subject, bodyText, null, null, default).ConfigureAwait(false);
                    return r.Sent;
                });
            _netDiagMonitor = new Api.NetDiagMonitor(
                Api.TailscaleDiagnostics.Collect, Api.LanPresenceProbe.TryResolveMac, netDiagDeviceStore, _netDiagRollup,
                netDiagAlerts.OnDrift, netDiagAlerts.OnResolve);
            _netDiagMonitor.Start();
        }
    }

    /// <summary>
    /// Read THIS Gateway's own aggregated roster over loopback and compute the notifier's snapshot: the
    /// count of sessions that "need you" plus the ids of those that just returned from an expired snooze.
    /// Going through the real <c>/sessions</c> endpoint (rather than re-implementing the fan-out) keeps
    /// the notifier's verdict identical to what every client sees - same aggregation, same effective-red
    /// fold, same <see cref="Contracts.SessionDto.SnoozeExpired"/> overlay. The per-machine Bearer is
    /// attached so it works whether or not global Gateway auth is on.
    /// </summary>
    private async Task<Push.WebPushNeedsYouNotifier.NeedsYouSnapshot> GetNeedsYouCountAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Port}/sessions");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        using var response = await _pushLoopbackHttp.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var sessions = JsonSerializer.Deserialize<List<Contracts.SessionDto>>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        return new Push.WebPushNeedsYouNotifier.NeedsYouSnapshot(
            Push.WebPushNeedsYouNotifier.CountNeedsYou(sessions),
            Push.WebPushNeedsYouNotifier.ExpiredNeedsYouIds(sessions));
    }

    /// <summary>
    /// The cron sweep timer callback (a boundary - it owns the try/catch so a sweep failure never
    /// crashes the timer thread). Fires due jobs; per-job failures are isolated inside the engine.
    /// </summary>
    /// <summary>
    /// The stamp delegate for the display-state PUSH seam (FleetDisplayStateObserver). The pushed snapshot
    /// carries only Director-owned facts, but the color fold reads two Gateway-only voice booleans
    /// (VoiceGenerating / VoiceAudioReady from _voiceService). This enriches each session with them BEFORE
    /// folding - the same order the roster handler uses (GatewayEndpoints roster map) - so the push seam and
    /// the roster fold IDENTICAL inputs and therefore the identical color.
    ///
    /// Without this the push seam sees VoiceAudioReady=false for every session and, since #1841 made
    /// IsVoicePreparing key on <c>!VoiceAudioReady</c>, holds every voice-mode waiting session yellow
    /// ("Preparing voice") forever, never moving to red once the voice is ready. The voice-ready flip
    /// arrives on no Director push, so only the 5s backstop sweep could carry it - and unenriched the sweep
    /// re-derives the same yellow every tick and the change gate suppresses the update permanently. This is
    /// exercised directly by the tests so the enrichment cannot silently regress again.
    /// </summary>
    internal static void EnrichVoiceThenFoldForPush(
        List<SessionDto> sessions,
        Func<string, bool> voiceGeneratingFor,
        Func<string, bool> voiceAudioReadyFor,
        Func<string, bool, DateTime?>? needsYouStampFor,
        Snooze.SnoozeRegistry? snoozeRegistry)
    {
        foreach (var s in sessions)
        {
            s.VoiceGenerating = voiceGeneratingFor(s.SessionId);
            s.VoiceAudioReady = voiceAudioReadyFor(s.SessionId);
        }
        Api.GatewayEndpoints.StampFleetRolesAndFold(sessions, sessions, needsYouStampFor, snoozeRegistry);
    }

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
    internal void SweepAutoDismiss()
    {
        // Hosted Multi-Tenancy (session-serving PR2): one pass per tenant, each inside that tenant's scope,
        // so the sweeper reads that tenant's fleet and its kill verbs go down that tenant's own Directors.
        // The try/catch is INSIDE the pass so one tenant failing does not skip the rest.
        _tenantPass.ForEachTenant(() =>
        {
            try
            {
                _ = _autoDismissSweeper?.SweepAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayHost] auto-dismiss sweep FAILED: {ex.Message}");
            }
        });
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
        var connectionId = PushedSessions.GetActiveConnectionId(TenantId.Local, directorId);
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
    /// null when that Director has no active stream connection (or the hub is unavailable). Gateway Cleanup
    /// (the cut) made the tunnel MANDATORY and deleted the HTTP command path, so null means the command is
    /// UNROUTABLE and the caller surfaces it as a 502 - there is nothing to fall back to. Any non-null
    /// result - success OR a typed failure - means the stream handled the command and its outcome is
    /// authoritative.
    ///
    /// This does not bound its own wait. The wait lives at the ONE chokepoint every command routes through,
    /// <c>DirectorCommandRouter.TrySendAsync</c>, which passes a token already linked to its timeout - so a
    /// Director that holds the tunnel open and answers nothing cancels the InvokeAsync below rather than
    /// hanging forever. Do not add a second timeout here; two would drift.
    /// </summary>
    public async Task<DirectorCommandResult?> SendCommandAsync(string directorId, DirectorCommand command, CancellationToken ct = default)
    {
        if (command is null) throw new ArgumentNullException(nameof(command));

        // Hosted Multi-Tenancy (session-serving PR2): resolve the Director's stream connection within the
        // tenant of the CURRENT unit of work - the request scope, the tunnel connection scope, or the
        // per-tenant background pass - never a hard-coded TenantId.Local. Local here would be a genuine
        // cross-tenant hazard, not just a wrong answer: it is the single lookup EVERY down-channel command
        // routes through, so one tenant's sweep resolving Local could address a Director it does not own.
        // No scope on hosted is a DENY: send nothing, exactly as an unconnected Director behaves.
        if (_tenantPass.Current is not { } commandTenant)
        {
            FileLog.Write($"[GatewayHost] SendCommandAsync: DENIED (no tenant in scope) director={directorId}, verb={command.Verb}");
            return null;
        }
        var connectionId = PushedSessions.GetActiveConnectionId(commandTenant, directorId);
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
        try { _displayStateSweepTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] display-state sweep timer dispose error: {ex.Message}"); }
        _displayStateSweepTimer = null;
        try { _voiceTurnUploadSweepTimer?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] voice-turn upload sweep timer dispose error: {ex.Message}"); }
        _voiceTurnUploadSweepTimer = null;


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
        try { _netDiagMonitor?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] netdiag monitor dispose error: {ex.Message}"); }
        try { _hostedAiSpendSweep?.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] hosted-ai spend sweep dispose error: {ex.Message}"); }
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

        // The EF data layer: dispose the pooled context factory and release the SQLite connections so a
        // restart (and a test) can reopen or delete the database file cleanly.
        try { _gatewayDb.Dispose(); } catch (Exception ex) { FileLog.Write($"[GatewayHost] gateway db dispose error: {ex.Message}"); }

        // Unsubscribe from registry events. We deliberately do NOT tear down the serve
        // mappings: the Directors are still alive and reachable, and a Gateway restart
        // re-asserts every mapping on Start().
        _serveProvisioner.Dispose();
        Registry.Dispose();
        Launchers.Dispose();

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
