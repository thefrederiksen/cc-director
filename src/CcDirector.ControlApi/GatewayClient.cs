using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Network;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Phase-1 Director-to-Gateway client. If a <see cref="GatewayConfig"/> is enabled
/// (gateway.url present in config.json), this client:
///
///   1. POSTs /directors/register on start.
///   2. POSTs /directors/{id}/heartbeat every <see cref="HeartbeatInterval"/>, carrying a
///      snapshot of every session's mechanical state (issue #186: the heartbeat doubles
///      as the reconcile channel for lost doorbell pings).
///   3. POSTs /directors/{id}/doorbell on every session activity-state change
///      (<see cref="NotifySessionState"/>) - fire-and-forget, payload announces THAT a
///      state changed, never WHAT happened. No retries, no outbox: a lost ping is
///      harmless because the heartbeat reconciles.
///   4. DELETEs /directors/{id}/registration on stop.
///   5. Reacts to 410 Gone on heartbeat by re-registering automatically.
///   6. Retries failed register and heartbeat calls with exponential backoff.
///
/// When the config is disabled (no gateway.url) the client is inert - every method
/// is a no-op so the Director boots normally in local-only mode.
/// </summary>
public sealed class GatewayClient : IGatewayHold, IDisposable
{
    /// <summary>How often the heartbeat fires.</summary>
    public static TimeSpan HeartbeatInterval { get; } = TimeSpan.FromSeconds(15);

    /// <summary>Max delay between failed register/heartbeat retries.</summary>
    public static TimeSpan MaxBackoff { get; } = TimeSpan.FromSeconds(60);

    private readonly GatewayConfig _config;
    private readonly string _directorId;
    private readonly int _port;
    private readonly string _version;
    private readonly Func<List<SessionStateSnapshot>>? _sessionStates;
    private readonly GatewayConnectionMonitor? _monitor;
    private readonly HttpClient _http;

    // The gateway address currently in use (issue #1233). Starts as _config.Url and is narrowed at
    // Start() to the first reachable entry of _config.CandidateUrls (machine name -> Tailscale -> IP).
    // volatile: read by the fleet-relay one-off clients on other threads.
    private volatile string _activeUrl = "";

    private CancellationTokenSource? _cts;
    private bool _registered;
    private bool _disposed;

    /// <summary>
    /// The plan-1A detection ladder (LocalAPI -> CLI -> config override). One instance per
    /// client so its log-dedup state spans the heartbeat re-resolves. Internal so tests can
    /// pin the environment-dependent probes (<see cref="TailnetIdentityResolver.LocalApiProbe"/>,
    /// <see cref="TailnetIdentityResolver.CliProbe"/>) while exercising the REAL ladder.
    /// </summary>
    internal TailnetIdentityResolver IdentityResolver { get; } = new();

    /// <summary>
    /// The LAN-IP resolver used when <see cref="GatewayConfig.AddressingMode"/> is
    /// <see cref="AddressingMode.Lan"/> (issue #457). One instance per client so its log-dedup
    /// state spans the heartbeat re-resolves, mirroring <see cref="IdentityResolver"/>.
    /// </summary>
    internal LanIdentityResolver LanResolver { get; } = new();

    /// <summary>
    /// Test seam (issue #324): some tests pin the whole resolution to an exact endpoint
    /// (e.g. a loopback callback host) that the production ladder would refuse. Production
    /// always resolves through <see cref="IdentityResolver"/> with this Director's port and
    /// configured override (wired in the constructor).
    /// </summary>
    internal Func<TailnetEndpointResolution> ResolveAdvertisedEndpoint { get; set; }

    /// <summary>
    /// The gateway address currently in use (issue #1233): <see cref="GatewayConfig.Url"/> until
    /// <see cref="Start"/> narrows it to the first reachable entry of
    /// <see cref="GatewayConfig.CandidateUrls"/>. Exposed for tests and diagnostics.
    /// </summary>
    internal string ActiveUrl => _activeUrl;

    /// <summary>
    /// Reachability probe for a single gateway candidate (issue #1233): returns null when the
    /// address answers GET /healthz, otherwise a reason. Injectable so candidate selection is
    /// unit-tested without a live gateway; production probes /healthz with a short timeout.
    /// </summary>
    internal Func<string, CancellationToken, Task<string?>> ProbeGatewayCandidate { get; set; }

    public bool IsEnabled => _config.IsEnabled;
    public bool IsRegistered => _registered;

    /// <param name="sessionStates">Snapshot provider for the heartbeat's per-session state
    /// map (issue #186). Null (old callers, tests) sends a body-less heartbeat.</param>
    /// <param name="monitor">Two-way handshake state home (issues #223/#224). Owned by the
    /// HOST, not this client, so it survives client replacement on settings changes. Null
    /// (old callers, tests) disables verification entirely - registration and heartbeat
    /// behave exactly as before.</param>
    public GatewayClient(GatewayConfig config, string directorId, int port, string version, Func<List<SessionStateSnapshot>>? sessionStates = null, GatewayConnectionMonitor? monitor = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _directorId = directorId ?? throw new ArgumentNullException(nameof(directorId));
        _port = port;
        _version = version ?? "0.0.0";
        _sessionStates = sessionStates;
        _monitor = monitor;
        // Pick the resolver by addressing mode (issue #457). Tailscale mode advertises the
        // Serve front door; LAN mode advertises this machine's LAN IP. Neither ever advertises
        // loopback. The mode is fixed for the life of this client (a change applies on restart,
        // same as the bind interface that pairs with it).
        ResolveAdvertisedEndpoint = () => _config.AddressingMode == AddressingMode.Lan
            ? LanResolver.ResolveEndpoint(_port, _config.TailnetEndpoint)
            : IdentityResolver.ResolveEndpoint(_port, _config.TailnetEndpoint);

        _activeUrl = _config.Url;
        ProbeGatewayCandidate = (url, ct) => ProbeGatewayHealthzAsync(url, ct);

        _http = new HttpClient(GatewayHttp.Handler())
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        if (_config.IsEnabled)
        {
            _http.BaseAddress = new Uri(_activeUrl.TrimEnd('/') + "/");
            if (!string.IsNullOrEmpty(_config.Token))
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
        }
    }

    /// <summary>
    /// Fetch the latest Gateway turn brief for a session - the desktop Wingman tab's source
    /// (the rich per-turn brief the warm brain stamps, same content the Cockpit renders).
    /// Returns null when the Gateway is disabled, has no brief yet (404), or is unreachable;
    /// the caller then shows the local explain instead. Best-effort, never throws - same
    /// posture as the rest of this network client.
    /// </summary>
    public async Task<TurnBriefDto?> GetLatestTurnBriefAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_config.IsEnabled || string.IsNullOrWhiteSpace(sessionId)) return null;
        try
        {
            using var resp = await _http.GetAsync($"sessions/{sessionId}/turnbriefs/latest", ct);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;   // no brief stamped yet
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[GatewayClient] GetLatestTurnBriefAsync {sessionId}: HTTP {(int)resp.StatusCode}");
                return null;
            }
            return await resp.Content.ReadFromJsonAsync<TurnBriefDto>(ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayClient] GetLatestTurnBriefAsync {sessionId} FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Turn a failed Gateway relay response into the exception the /fleet/* endpoint will word for the human,
    /// CARRYING the Gateway's own message instead of throwing it away.
    ///
    /// This is the second of two places the same explanation used to die. The Gateway's TunnelFailure dropped
    /// a Director-sent failure into a bodyless 502; these relays then threw "returned HTTP 502 Bad Gateway"
    /// without ever reading the body, so even once the Gateway carried the words, the Director discarded them
    /// one hop before the person who typed the command. Fixing either alone lands nowhere - the CLI's own
    /// error path already parses an "error" key and already tolerates an empty body, so with both legs carried
    /// the Director's real explanation reaches the terminal with no client change at all.
    ///
    /// Falls back to the status line ONLY when there genuinely is no message to show - an empty or non-JSON
    /// body. That is not papering over a failure; it is reporting the only fact available.
    /// </summary>
    private static async Task<InvalidOperationException> RelayFailureAsync(
        HttpResponseMessage resp, string what, CancellationToken ct)
    {
        string? message = null;
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("error", out var err)
                    && err.ValueKind == JsonValueKind.String)
                {
                    message = err.GetString();
                }
            }
        }
        catch (JsonException) { /* not JSON - fall through to the status line below */ }

        var statusLine = $"Gateway {what} returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}".TrimEnd();
        FileLog.Write($"[GatewayClient] {what} FAILED: {(string.IsNullOrWhiteSpace(message) ? statusLine : message)}");
        return new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? statusLine : message);
    }

    // ===== Fleet relay (issue #705) =====
    // A session can only reach its OWN Director (it is given CC_DIRECTOR_API, never the
    // Gateway URL or the fleet token). These three methods let the Director relay a session's
    // request on to the Gateway using the authenticated _http it already holds, so the fleet
    // token stays server-side and never enters an agent process. They THROW on failure (no
    // best-effort null like GetLatestTurnBriefAsync): the /fleet/* endpoints are the boundary
    // that turns a failure into a clear error, per the no-fallback rule.

    /// <summary>
    /// Relay the Gateway's aggregated fleet session list (GET /sessions) so a session can
    /// discover every other session across the fleet. Throws when the Gateway is disabled or
    /// the call fails.
    /// </summary>
    public async Task<List<SessionDto>> ListFleetSessionsAsync(CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot list the fleet.");

        FileLog.Write("[GatewayClient] ListFleetSessionsAsync: GET /sessions");
        using var resp = await _http.GetAsync("sessions", ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, "GET /sessions", ct);

        var list = await resp.Content.ReadFromJsonAsync<List<SessionDto>>(ct);
        if (list is null)
            throw new InvalidOperationException("Gateway GET /sessions returned an unparsable body.");

        FileLog.Write($"[GatewayClient] ListFleetSessionsAsync: {list.Count} session(s)");
        return list;
    }

    /// <summary>
    /// Like <see cref="ListFleetSessionsAsync"/> but asks for the envelope (GET /sessions?envelope=true),
    /// which also carries per-Director reachability (Online / Wobbly / Offline). The plain list
    /// silently DROPS an unreachable Director's sessions while still returning 200, so a caller that
    /// must not act on a partial roster - the destructive worktree reaper - needs the reachability to
    /// tell whether the fleet view is complete. Throws when the Gateway is disabled or the call fails.
    /// </summary>
    public async Task<(List<SessionDto> Sessions, List<DirectorReachabilityDto> Reachability)> ListFleetSessionsWithReachabilityAsync(CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot list the fleet.");

        FileLog.Write("[GatewayClient] ListFleetSessionsWithReachabilityAsync: GET /sessions?envelope=true");
        using var resp = await _http.GetAsync("sessions?envelope=true", ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, "GET /sessions?envelope=true", ct);

        var env = await resp.Content.ReadFromJsonAsync<SessionsEnvelope>(ct);
        if (env is null)
            throw new InvalidOperationException("Gateway GET /sessions?envelope=true returned an unparsable body.");

        // FAIL CLOSED on MISSING completeness metadata (inspection): a legacy or version-skewed Gateway
        // may return a 200 envelope WITHOUT the reachability ('directors') array - or without 'sessions'.
        // A destructive caller must distinguish "the server authoritatively returned an empty array"
        // (present but empty) from "the field was absent" (unknown). Coalescing absent-to-empty would
        // make the reaper trust a roster whose completeness it cannot confirm, so an absent field throws.
        if (env.Directors is null)
            throw new InvalidOperationException(
                "Gateway GET /sessions?envelope=true omitted per-Director reachability; cannot confirm the roster is complete.");
        if (env.Sessions is null)
            throw new InvalidOperationException(
                "Gateway GET /sessions?envelope=true omitted the session list; cannot confirm the roster is complete.");

        FileLog.Write($"[GatewayClient] ListFleetSessionsWithReachabilityAsync: {env.Sessions.Count} session(s), {env.Directors.Count} reachability record(s)");
        return (env.Sessions, env.Directors);
    }

    /// <summary>
    /// The roster plus reachability WHEN THE GATEWAY SUPPLIES IT, for a READING caller (issue #1051).
    ///
    /// Deliberately a separate method from <see cref="ListFleetSessionsWithReachabilityAsync"/> rather than a
    /// flag on it, because the two differ in ERROR POSTURE and that posture is the whole value of the other
    /// one. The worktree reaper DELETES directories, so for it "I cannot confirm the roster is complete" must
    /// be fatal - it fails closed and throws. A read-only listing has the opposite duty: turning a
    /// degraded-but-usable answer into a total failure would take `session list`, every target resolve,
    /// cc-status and cc-history down against a version-skewed Gateway, which is a far worse outcome than
    /// showing the roster and saying completeness is unknown. Reusing the strict method here would have done
    /// exactly that; weakening the strict method would have quietly disarmed the reaper's guard.
    ///
    /// So: reachability is NULL when the Gateway did not supply it - an older Gateway that ignores the
    /// envelope query and answers with the bare array, or one that omits the field. Null means UNKNOWN and
    /// callers must not read it as "complete"; that is the same absent-is-not-empty distinction this issue
    /// exists to fix. A transport failure or a non-2xx is still a real failure and still throws.
    /// </summary>
    public async Task<(List<SessionDto> Sessions, List<DirectorReachabilityDto>? Reachability)>
        ReadFleetSessionsWithOptionalReachabilityAsync(CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot list the fleet.");

        FileLog.Write("[GatewayClient] ReadFleetSessionsWithOptionalReachabilityAsync: GET /sessions?envelope=true");
        using var resp = await _http.GetAsync("sessions?envelope=true", ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, "GET /sessions?envelope=true", ct);

        var body = await resp.Content.ReadAsStringAsync(ct);
        var parsed = ParseFleetBodyForDisplay(body);
        FileLog.Write($"[GatewayClient] ReadFleetSessionsWithOptionalReachabilityAsync: {parsed.Sessions.Count} session(s), {(parsed.Reachability is null ? "NO" : parsed.Reachability.Count.ToString())} reachability record(s)");
        return parsed;
    }

    /// <summary>
    /// Split the /sessions body into rows plus OPTIONAL reachability, tolerating BOTH shapes the route can
    /// answer with (issue #1051). Pure and separated from the request so the shape discrimination is
    /// testable without a live Gateway - it is the part with a real failure mode, because an older Gateway
    /// ignores the unknown envelope query parameter and answers with the plain array it always did.
    ///
    /// Reachability is null for "not supplied", which the caller must treat as UNKNOWN and never as complete.
    /// A missing session list throws: that is a malformed answer, not a degraded one.
    /// </summary>
    internal static (List<SessionDto> Sessions, List<DirectorReachabilityDto>? Reachability)
        ParseFleetBodyForDisplay(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new InvalidOperationException("Gateway GET /sessions?envelope=true returned an empty body.");

        using var doc = JsonDocument.Parse(body);

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            var plain = JsonSerializer.Deserialize<List<SessionDto>>(body, JsonWebOptions) ?? new List<SessionDto>();
            return (plain, null);
        }

        var env = JsonSerializer.Deserialize<SessionsEnvelope>(body, JsonWebOptions);
        if (env?.Sessions is null)
            throw new InvalidOperationException("Gateway GET /sessions?envelope=true omitted the session list.");

        return (env.Sessions, env.Directors);
    }

    /// <summary>Web defaults (camelCase), matching what ReadFromJsonAsync uses elsewhere in this client.</summary>
    private static readonly JsonSerializerOptions JsonWebOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The /sessions?envelope=true response shape: the roster plus per-Director reachability.</summary>
    private sealed class SessionsEnvelope
    {
        public List<SessionDto>? Sessions { get; set; }
        public List<DirectorReachabilityDto>? Directors { get; set; }
    }


    /// <summary>
    /// True when the Gateway does not actually serve this route: an explicit 404, or the Cockpit
    /// single-page-app fallback answering an unknown GET with 200 text/html. Either way the route
    /// is absent on that Gateway version and the caller serves its own local model. A JSON error
    /// or a 5xx is a REAL failure and is never mistaken for absence.
    /// </summary>
    private static bool RouteAbsent(HttpResponseMessage resp)
        => resp.StatusCode == HttpStatusCode.NotFound
           || (resp.IsSuccessStatusCode
               && !string.Equals(resp.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The fleet's repositories (GET /repositories). Returns NULL when the Gateway answers 404 -
    /// an older Gateway that does not know the route yet (version tolerance, the same posture as
    /// the stream push skipping on an old hub); the caller then serves its own local model.
    /// Throws when the Gateway is disabled or genuinely fails.
    /// </summary>
    public async Task<List<RepoStatusDto>?> ListFleetRepositoriesAsync(CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot list fleet repositories.");
        FileLog.Write("[GatewayClient] ListFleetRepositoriesAsync: GET /repositories");
        using var resp = await _http.GetAsync("repositories", ct);
        if (RouteAbsent(resp))
        {
            FileLog.Write("[GatewayClient] GET /repositories: route absent on this Gateway (404 or non-JSON fallback) - caller serves local");
            return null;
        }
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, "GET /repositories", ct);
        var list = await resp.Content.ReadFromJsonAsync<List<RepoStatusDto>>(ct);
        if (list is null)
            throw new InvalidOperationException("Gateway GET /repositories returned an unparsable body.");
        return list;
    }

    /// <summary>
    /// The fleet's worktrees, flattened (GET /worktrees). NULL on 404 (older Gateway - caller
    /// serves its own local model); throws when the Gateway is disabled or genuinely fails.
    /// </summary>
    public async Task<List<FleetWorktreeDto>?> ListFleetWorktreesAsync(CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot list fleet worktrees.");
        FileLog.Write("[GatewayClient] ListFleetWorktreesAsync: GET /worktrees");
        using var resp = await _http.GetAsync("worktrees", ct);
        if (RouteAbsent(resp))
        {
            FileLog.Write("[GatewayClient] GET /worktrees: route absent on this Gateway (404 or non-JSON fallback) - caller serves local");
            return null;
        }
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, "GET /worktrees", ct);
        var list = await resp.Content.ReadFromJsonAsync<List<FleetWorktreeDto>>(ct);
        if (list is null)
            throw new InvalidOperationException("Gateway GET /worktrees returned an unparsable body.");
        return list;
    }

    /// <summary>
    /// The launchers this tenant has registered (GET /launchers) - the list of machines it can search and
    /// start things on. NULL on 404, which on this route means a Gateway that still has the launcher family
    /// denied rather than one that never had it.
    /// </summary>
    public async Task<List<LauncherDto>?> ListMachinesAsync(CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot list machines.");
        FileLog.Write("[GatewayClient] ListMachinesAsync: GET /launchers");
        using var resp = await _http.GetAsync("launchers", ct);
        if (RouteAbsent(resp))
        {
            FileLog.Write("[GatewayClient] GET /launchers: route absent on this Gateway");
            return null;
        }
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, "GET /launchers", ct);
        return await resp.Content.ReadFromJsonAsync<List<LauncherDto>>(ct)
               ?? throw new InvalidOperationException("Gateway GET /launchers returned an unparsable body.");
    }

    /// <summary>
    /// Ask one machine a query - "apps" or "files" - through the Gateway's machine relay.
    ///
    /// The answer is returned as the raw text the launcher produced, along with the status, rather than
    /// deserialised here. This Director is a pass-through for it: it does not read the catalogue or the search
    /// results, and typing them here would mean a launcher a version ahead loses any field this build has not
    /// been taught, on a hop that had no reason to look inside.
    /// </summary>
    public async Task<(int Status, string Body)> QueryMachineAsync(string machine, string verb, string? query,
        int limit, int timeoutMilliseconds, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException($"Gateway is not configured; cannot query machine '{machine}'.");

        var path = $"machines/{Uri.EscapeDataString(machine)}/{verb}" +
                   $"?q={Uri.EscapeDataString(query ?? "")}&limit={limit}&timeoutMilliseconds={timeoutMilliseconds}";
        FileLog.Write($"[GatewayClient] QueryMachineAsync: GET /{path}");

        // A file search may legitimately run for a couple of minutes on a large disk, well past this client's
        // ordinary timeout, so the request carries its own.
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(MachineQueryClientTimeoutMilliseconds));

        using var resp = await _http.SendAsync(request, deadline.Token);
        var body = await resp.Content.ReadAsStringAsync(deadline.Token);
        FileLog.Write($"[GatewayClient] QueryMachineAsync: {verb} on {machine} -> {(int)resp.StatusCode}");
        return ((int)resp.StatusCode, body);
    }

    /// <summary>
    /// Start something on one machine through the Gateway's machine relay. Either <paramref name="path"/> or
    /// <paramref name="app"/> identifies it; the launcher resolves a name against its own catalogue.
    /// <paramref name="confirmProtected"/> is the explicit confirmation the Gateway requires on every launch
    /// (tenant-boundary hardening, CR-5) - forwarded verbatim, never invented on the caller's behalf.
    /// </summary>
    public async Task<(int Status, string Body)> LaunchOnMachineAsync(string machine, string? path, string? app,
        string? args, string? cwd, bool headless, bool confirmProtected = false, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException($"Gateway is not configured; cannot launch on machine '{machine}'.");

        FileLog.Write($"[GatewayClient] LaunchOnMachineAsync: {machine} path={path ?? "(none)"} app={app ?? "(none)"}");
        using var resp = await _http.PostAsJsonAsync($"machines/{Uri.EscapeDataString(machine)}/launch",
            new { path, app, args, cwd, headless, confirmProtected }, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        FileLog.Write($"[GatewayClient] LaunchOnMachineAsync: {machine} -> {(int)resp.StatusCode}");
        return ((int)resp.StatusCode, body);
    }

    /// <summary>
    /// How long this Director waits for a machine query. It exceeds the launcher's own search ceiling and the
    /// Gateway relay's allowance, so a search that ran to its full deadline still returns an answer here
    /// rather than being cut off by the nearest hop.
    /// </summary>
    private const int MachineQueryClientTimeoutMilliseconds = 150_000;

    /// <summary>
    /// Relay a single message to a session anywhere in the fleet via the Gateway's
    /// POST /sessions/{sid}/prompt. Fire-and-forget (WaitForIdle=false). Throws when the
    /// Gateway is disabled or the call fails.
    /// </summary>
    /// <param name="appendEnter">Whether the target presses Enter after the text. Defaults to true,
    /// which is what a fleet MESSAGE wants - it is a delivered message, not a draft. It MUST be carried
    /// rather than assumed: `session prompt --no-submit` stages text in a composer for a human to read
    /// and send, and this hardcoding true meant the same command submitted or did not depending only on
    /// which machine the target session happened to be on - reporting success either way.</param>
    public async Task<PromptResponse> SendPromptToFleetAsync(string toSessionId, string text, bool appendEnter = true, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot reach a remote session.");
        if (string.IsNullOrWhiteSpace(toSessionId))
            throw new ArgumentException("Target session id is required", nameof(toSessionId));

        FileLog.Write($"[GatewayClient] SendPromptToFleetAsync: POST /sessions/{toSessionId}/prompt appendEnter={appendEnter}");
        // AgentDriven: this relay exists only to carry a FLEET message to a session on another Director, so
        // it is by construction one agent prompting another (issue #1636). The target Director cannot tell
        // that from the prompt alone - the marker is what stops the same message counting differently
        // depending on which machine the target happened to be on.
        var body = new PromptRequest { Text = text, AppendEnter = appendEnter, WaitForIdle = false, AgentDriven = true };
        using var resp = await _http.PostAsJsonAsync($"sessions/{toSessionId}/prompt", body, ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, $"prompt to {toSessionId}", ct);

        var parsed = await resp.Content.ReadFromJsonAsync<PromptResponse>(ct);
        if (parsed is null)
            throw new InvalidOperationException("Gateway prompt returned an unparsable body.");
        return parsed;
    }

    /// <summary>
    /// Push captured prompts and replies to the Gateway's prompt log (issue #1551): POST /prompts.
    /// Returns how many the Gateway stored, or null when it could not be reached or refused - the
    /// Director keeps no copy, so a null must be treated as "not recorded" and retried, never as done.
    /// Unlike most calls here this does not throw: a logging failure must not break a turn.
    /// </summary>
    public async Task<int?> PushPromptsAsync(IReadOnlyList<PromptRecord> records, CancellationToken ct = default)
    {
        if (!_config.IsEnabled) return null;
        if (records.Count == 0) return 0;

        try
        {
            var body = new PromptIngestRequest { Records = records };
            using var resp = await _http.PostAsJsonAsync("prompts", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[GatewayClient] PushPromptsAsync: POST /prompts returned HTTP {(int)resp.StatusCode}");
                return null;
            }

            var parsed = await resp.Content.ReadFromJsonAsync<PromptIngestResponse>(ct);
            return parsed?.Written;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayClient] PushPromptsAsync FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Push one activity-event batch to the Gateway ledger (docs/PLAN-trustworthy-working-start-
    /// 2026-07-24.md). Returns the Gateway's acknowledgement, or null when the push did not land
    /// (disabled, HTTP failure, transport fault) - the caller keeps the outbox records and retries.
    /// A duplicate in the response is a SUCCESSFUL replay: the Gateway already durably holds that
    /// event, which acknowledges it exactly as a fresh write does.
    /// </summary>
    public async Task<ActivityEventIngestResponse?> PushActivityEventsAsync(
        IReadOnlyList<ActivityEventRecord> events, CancellationToken ct = default)
    {
        if (!_config.IsEnabled) return null;
        if (events.Count == 0) return new ActivityEventIngestResponse { Written = 0, Duplicates = 0 };

        try
        {
            var body = new ActivityEventIngestRequest { Events = events };
            using var resp = await _http.PostAsJsonAsync("activity-events/batch", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[GatewayClient] PushActivityEventsAsync: POST /activity-events/batch returned HTTP {(int)resp.StatusCode}");
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<ActivityEventIngestResponse>(ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayClient] PushActivityEventsAsync FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Push this Director's repo-state snapshots to the Gateway (issue #2118) - the branches and worktrees
    /// of its registered repositories, which is the one git-hygiene fact the Gateway cannot observe for
    /// itself. Returns the Gateway's acknowledgement, or NULL when the push did not land (Gateway disabled,
    /// HTTP failure, transport fault).
    ///
    /// FAIL-SAFE, exactly like <see cref="PushActivityEventsAsync"/>: a failed push is logged and returns
    /// null, and the caller simply tries again on its next cycle. It never throws into the Director, because
    /// a hygiene feed for a morning email must not be able to disturb the sessions a person is working in.
    /// There is no outbox and no retry queue: this pushes the CURRENT state of the repositories, so a lost
    /// push is not lost data - the next cycle's snapshot supersedes it entirely.
    /// </summary>
    public async Task<RepoStatePushResponse?> PushRepoStateAsync(
        RepoStatePushRequest request, CancellationToken ct = default)
    {
        if (!_config.IsEnabled) return null;
        ArgumentNullException.ThrowIfNull(request);
        if (request.Repositories.Count == 0) return new RepoStatePushResponse { Stored = 0 };

        try
        {
            using var resp = await _http.PostAsJsonAsync("gateway/repostate", request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[GatewayClient] PushRepoStateAsync: POST /gateway/repostate returned HTTP {(int)resp.StatusCode}");
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<RepoStatePushResponse>(ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayClient] PushRepoStateAsync FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Report whether the skills the Gateway serves could actually be READ on this machine.
    ///
    /// The Gateway can see that it served a skill and still be wrong about whether anything can read it -
    /// only the machine observes that. Without this feed, publishing a skill fleet-wide is done blind, and
    /// a machine where nothing lands looks exactly like a machine where everything is fine.
    ///
    /// FAIL-SAFE, exactly like <see cref="PushRepoStateAsync"/>: a failed push is logged and returns null,
    /// and the caller tries again on its next cycle. No outbox and no retry queue - this pushes the CURRENT
    /// outcome, so a lost push is superseded by the next one rather than lost.
    /// </summary>
    public async Task<SkillPlacementPushResponse?> PushSkillPlacementAsync(
        SkillPlacementPushRequest request, CancellationToken ct = default)
    {
        if (!_config.IsEnabled) return null;
        ArgumentNullException.ThrowIfNull(request);
        if (request.Reports.Count == 0) return new SkillPlacementPushResponse { Stored = 0 };

        try
        {
            using var resp = await _http.PostAsJsonAsync("gateway/skills/placement", request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[GatewayClient] PushSkillPlacementAsync: POST /gateway/skills/placement " +
                              $"returned HTTP {(int)resp.StatusCode}");
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<SkillPlacementPushResponse>(ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayClient] PushSkillPlacementAsync FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Rename a session anywhere in the fleet via the Gateway's PATCH /sessions/{sid}, which routes the
    /// rename to the owning Director over the tunnel and returns the updated <see cref="SessionDto"/>.
    /// Issue #1490: the Director's loopback POST /fleet/rename relays here for a non-local target. Throws
    /// when the Gateway is disabled or the call fails.
    /// </summary>
    public async Task<SessionDto> RenameFleetAsync(string toSessionId, string? name, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot reach a remote session.");
        if (string.IsNullOrWhiteSpace(toSessionId))
            throw new ArgumentException("Target session id is required", nameof(toSessionId));

        FileLog.Write($"[GatewayClient] RenameFleetAsync: PATCH /sessions/{toSessionId}");
        var body = new SessionUpdateRequest { Name = name };
        using var resp = await _http.PatchAsJsonAsync($"sessions/{toSessionId}", body, ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, $"rename of {toSessionId}", ct);

        var parsed = await resp.Content.ReadFromJsonAsync<SessionDto>(ct);
        if (parsed is null)
            throw new InvalidOperationException("Gateway rename returned an unparsable body.");
        return parsed;
    }

    /// <summary>
    /// Interrupt a session anywhere in the fleet, through the Gateway's POST /sessions/{sid}/interrupt,
    /// which routes it to the owning Director over the tunnel. Used by the local POST /fleet/interrupt for
    /// a target this Director does not host.
    /// </summary>
    public async Task InterruptFleetAsync(string toSessionId, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot reach a remote session.");
        if (string.IsNullOrWhiteSpace(toSessionId))
            throw new ArgumentException("Target session id is required", nameof(toSessionId));

        FileLog.Write($"[GatewayClient] InterruptFleetAsync: POST /sessions/{toSessionId}/interrupt");
        using var resp = await _http.PostAsync($"sessions/{toSessionId}/interrupt", content: null, ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, $"interrupt of {toSessionId}", ct);
    }

    /// <summary>
    /// Hold (or release) a session anywhere in the fleet, through the Gateway's POST /sessions/{sid}/hold,
    /// which routes it to the owning Director over the tunnel. Used by the local POST /fleet/hold for a
    /// target this Director does not host.
    /// </summary>
    public async Task<HoldResponse> HoldFleetAsync(string toSessionId, bool onHold, int? snoozeMinutes, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot reach a remote session.");
        if (string.IsNullOrWhiteSpace(toSessionId))
            throw new ArgumentException("Target session id is required", nameof(toSessionId));

        FileLog.Write($"[GatewayClient] HoldFleetAsync: POST /sessions/{toSessionId}/hold onHold={onHold}");
        var body = new HoldRequest { OnHold = onHold, SnoozeMinutes = snoozeMinutes };
        using var resp = await _http.PostAsJsonAsync($"sessions/{toSessionId}/hold", body, ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, $"hold of {toSessionId}", ct);

        var parsed = await resp.Content.ReadFromJsonAsync<HoldResponse>(ct);
        if (parsed is null)
            throw new InvalidOperationException("Gateway hold returned an unparsable body.");
        return parsed;
    }

    /// <summary>
    /// Compact a session anywhere in the fleet, through the Gateway's POST /sessions/{sid}/compact-context,
    /// which routes it to the owning Director over the tunnel. Used by the local POST /fleet/compact for a
    /// target this Director does not host (issue #2150).
    ///
    /// Uses a DEDICATED HttpClient: the shared <c>_http</c> gives up after 10 seconds, but a compaction
    /// legitimately runs for minutes, and this call deliberately waits for the FINISH rather than the
    /// submission. Its timeout sits outside the Gateway's own wait for the verb, which sits outside the
    /// Director's compaction wait - so the innermost bound always fires first and names what failed.
    /// </summary>
    public async Task<CompactContextResponse> CompactFleetAsync(string toSessionId, string? continuePrompt, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot reach a remote session.");
        if (string.IsNullOrWhiteSpace(toSessionId))
            throw new ArgumentException("Target session id is required", nameof(toSessionId));

        FileLog.Write($"[GatewayClient] CompactFleetAsync: POST /sessions/{toSessionId}/compact-context " +
                      $"continue={(string.IsNullOrWhiteSpace(continuePrompt) ? "no" : "yes")}");
        using var http = new HttpClient(GatewayHttp.Handler())
        {
            BaseAddress = new Uri(_activeUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(4),
        };
        if (!string.IsNullOrEmpty(_config.Token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);

        var body = new CompactContextRequest { ContinuePrompt = continuePrompt };
        using var resp = await http.PostAsJsonAsync($"sessions/{toSessionId}/compact-context", body, ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, $"compaction of {toSessionId}", ct);

        var parsed = await resp.Content.ReadFromJsonAsync<CompactContextResponse>(ct);
        if (parsed is null)
            throw new InvalidOperationException("Gateway compaction returned an unparsable body.");
        return parsed;
    }

    /// <summary>
    /// Read a session's terminal buffer anywhere in the fleet, through the Gateway's GET
    /// /sessions/{sid}/buffer, which routes it to the owning Director over the tunnel. Used by the local
    /// GET /fleet/buffer for a target this Director does not host.
    /// </summary>
    public async Task<string> GetBufferFleetAsync(string toSessionId, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot reach a remote session.");
        if (string.IsNullOrWhiteSpace(toSessionId))
            throw new ArgumentException("Target session id is required", nameof(toSessionId));

        FileLog.Write($"[GatewayClient] GetBufferFleetAsync: GET /sessions/{toSessionId}/buffer");
        using var resp = await _http.GetAsync($"sessions/{toSessionId}/buffer", ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, $"buffer read of {toSessionId}", ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Set a session's EXPLICIT role anywhere in the fleet, through the Gateway's POST
    /// /sessions/{sid}/role, which routes it to the owning Director over the tunnel. Used by the local
    /// POST /fleet/role for a target this Director does not host.
    /// </summary>
    public async Task<SessionDto> SetRoleFleetAsync(string toSessionId, string? role, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot reach a remote session.");
        if (string.IsNullOrWhiteSpace(toSessionId))
            throw new ArgumentException("Target session id is required", nameof(toSessionId));

        FileLog.Write($"[GatewayClient] SetRoleFleetAsync: POST /sessions/{toSessionId}/role role={role ?? "(cleared)"}");
        var body = new SetRoleRequest { Role = role };
        using var resp = await _http.PostAsJsonAsync($"sessions/{toSessionId}/role", body, ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, $"set-role of {toSessionId}", ct);

        var parsed = await resp.Content.ReadFromJsonAsync<SessionDto>(ct);
        if (parsed is null)
            throw new InvalidOperationException("Gateway set-role returned an unparsable body.");
        return parsed;
    }

    /// <summary>
    /// Flag a session anywhere in the fleet for teardown via the Gateway's POST /sessions/{sid}/request-deletion,
    /// which routes to the owning Director over the tunnel. Issue #1490: the Director's loopback POST /fleet/done
    /// relays here for a non-local target. Throws when the Gateway is disabled or the call fails.
    /// </summary>
    public async Task RequestDeletionFleetAsync(string toSessionId, string? reason, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot reach a remote session.");
        if (string.IsNullOrWhiteSpace(toSessionId))
            throw new ArgumentException("Target session id is required", nameof(toSessionId));

        FileLog.Write($"[GatewayClient] RequestDeletionFleetAsync: POST /sessions/{toSessionId}/request-deletion");
        var body = new SessionDeletionRequest { Reason = reason };
        using var resp = await _http.PostAsJsonAsync($"sessions/{toSessionId}/request-deletion", body, ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, $"deletion request for {toSessionId}", ct);
    }

    /// <summary>
    /// Ask a question to a session anywhere in the fleet and wait for its answer (issue #717), via
    /// the Gateway's POST /sessions/{sid}/prompt with WaitForIdle=true. The Gateway holds the
    /// response open until the target returns to Idle (or the timeout), then returns the captured
    /// output as <see cref="PromptResponse.Output"/> with <see cref="PromptResponse.WaitStatus"/>.
    /// Uses a DEDICATED HttpClient: the shared <c>_http</c> has a 10s timeout, but an ask may
    /// legitimately wait up to <paramref name="timeoutMs"/>. Throws when the Gateway is disabled or
    /// the call fails.
    /// </summary>
    public async Task<PromptResponse> AskFleetAsync(string toSessionId, string text, int timeoutMs, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot reach a remote session.");
        if (string.IsNullOrWhiteSpace(toSessionId))
            throw new ArgumentException("Target session id is required", nameof(toSessionId));

        FileLog.Write($"[GatewayClient] AskFleetAsync: POST /sessions/{toSessionId}/prompt (wait <= {timeoutMs}ms)");
        using var http = new HttpClient(GatewayHttp.Handler())
        {
            BaseAddress = new Uri(_activeUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMilliseconds(timeoutMs + 15_000),
        };
        if (!string.IsNullOrEmpty(_config.Token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);

        var body = new PromptRequest { Text = text, AppendEnter = true, WaitForIdle = true, TimeoutMs = timeoutMs };
        using var resp = await http.PostAsJsonAsync($"sessions/{toSessionId}/prompt", body, ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, $"ask to {toSessionId}", ct);

        var parsed = await resp.Content.ReadFromJsonAsync<PromptResponse>(ct);
        if (parsed is null)
            throw new InvalidOperationException("Gateway ask returned an unparsable body.");
        return parsed;
    }

    /// <summary>
    /// Relay a broadcast to many sessions via the Gateway's POST /fanout. Fire-and-forget
    /// (WaitForIdle=false). Throws when the Gateway is disabled or the call fails.
    /// Issue #1229: carries the sender's id (so the Hub can decide the broadcast's scope from its own
    /// fleet view) plus an optional reason and human-issued grant id for a fleet-wide broadcast. The
    /// Hub may REFUSE on scope grounds, which comes back as a 2xx <see cref="FanoutResponse"/> with
    /// <see cref="FanoutResponse.Denied"/> set - the caller surfaces that, it is not an error here.
    /// </summary>
    public async Task<FanoutResponse> FanoutToFleetAsync(List<string> sessionIds, string text, string? fromSessionId = null, string? reason = null, string? grantId = null, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot broadcast to the fleet.");
        if (sessionIds is null || sessionIds.Count == 0)
            throw new ArgumentException("At least one target session id is required", nameof(sessionIds));

        FileLog.Write($"[GatewayClient] FanoutToFleetAsync: POST /fanout to {sessionIds.Count} session(s)");
        var body = new FanoutRequest
        {
            SessionIds = sessionIds,
            Text = text,
            AppendEnter = true,
            WaitForIdle = false,
            FromSessionId = fromSessionId,
            Reason = reason,
            GrantId = grantId,
        };
        using var resp = await _http.PostAsJsonAsync("fanout", body, ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, "fanout", ct);

        var parsed = await resp.Content.ReadFromJsonAsync<FanoutResponse>(ct);
        if (parsed is null)
            throw new InvalidOperationException("Gateway fanout returned an unparsable body.");
        return parsed;
    }

    /// <summary>
    /// Start a session on ANOTHER machine ("start a session on another computer"), via the Gateway's
    /// POST /machines/{machine}/sessions relay. The Gateway resolves the machine to a Director (launching
    /// one through the launcher when none is running) and creates the session there. Fail-loud like the
    /// other fleet relays: THROWS when the Gateway is disabled, the machine is off / unreachable, or the
    /// create fails - it NEVER falls back to a local spawn. Returns the new session on success.
    ///
    /// Uses a DEDICATED HttpClient with a generous timeout: the shared <c>_http</c> has a 10s ceiling, but
    /// a remote spawn may auto-launch a Director and wait (bounded) for it to register, which legitimately
    /// exceeds 10s (same reasoning as <see cref="AskFleetAsync"/>).
    /// </summary>
    public async Task<SessionDto> SpawnOnMachineAsync(string machine, NewSessionRequest req, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot start a session on another machine.");
        if (string.IsNullOrWhiteSpace(machine))
            throw new ArgumentException("Target machine is required", nameof(machine));
        if (req is null)
            throw new ArgumentNullException(nameof(req));

        FileLog.Write($"[GatewayClient] SpawnOnMachineAsync: POST /machines/{machine}/sessions, repo={req.RepoPath}, agent={req.Agent}");
        using var http = new HttpClient(GatewayHttp.Handler())
        {
            BaseAddress = new Uri(_activeUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(120),
        };
        if (!string.IsNullOrEmpty(_config.Token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);

        using var resp = await http.PostAsJsonAsync($"machines/{Uri.EscapeDataString(machine)}/sessions", req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Gateway could not start a session on '{machine}': HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}".TrimEnd());
        }

        var parsed = await resp.Content.ReadFromJsonAsync<SessionDto>(ct);
        if (parsed is null)
            throw new InvalidOperationException("Gateway start-on-machine returned an unparsable body.");
        FileLog.Write($"[GatewayClient] SpawnOnMachineAsync: started sid={parsed.SessionId} on machine={machine}");
        return parsed;
    }

    /// <summary>
    /// Issue #1548: look a Mission up by id in the GATEWAY's mission store - the source of truth for
    /// Missions, which are a fleet-level concept spanning Directors and machines. A LOCAL spawn resolves
    /// the mission name through here so it stamps the create request exactly the way the Gateway already
    /// stamps a REMOTE spawn in <c>POST /machines/{machine}/sessions</c>, leaving the Director floor to
    /// stamp only what create carries.
    ///
    /// Returns null ONLY when the Gateway genuinely has no mission with that id (404). Throws when the
    /// Gateway is disabled or unreachable - a missing answer must never be reported as "unknown mission",
    /// which is the lie this issue is about. Same fail-loud posture as the fleet relays above.
    /// </summary>
    public async Task<MissionDto?> GetMissionAsync(Guid missionId, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot look up a mission.");

        FileLog.Write($"[GatewayClient] GetMissionAsync: GET /missions/{missionId}");
        using var resp = await _http.GetAsync($"missions/{missionId}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            FileLog.Write($"[GatewayClient] GetMissionAsync {missionId}: the Gateway has no such mission");
            return null;
        }
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Gateway could not look up mission '{missionId}': HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}".TrimEnd());
        }

        var mission = await resp.Content.ReadFromJsonAsync<MissionDto>(ct);
        if (mission is null)
            throw new InvalidOperationException($"Gateway mission lookup for '{missionId}' returned an unparsable body.");
        FileLog.Write($"[GatewayClient] GetMissionAsync {missionId}: resolved name=\"{mission.MissionName}\"");
        return mission;
    }

    /// <summary>
    /// Workflows mission (phase 5b): look a workflow RUN up by id in the Gateway's run store - the
    /// source of truth for runs. A LOCAL seated spawn resolves the run through here so it stamps the
    /// create request exactly the way the Gateway stamps a REMOTE spawn. Returns null ONLY on a
    /// genuine 404; throws when the Gateway is disabled or unreachable (an unreachable Gateway is
    /// never reported as "unknown run" - the GetMissionAsync posture).
    /// </summary>
    public async Task<WorkflowRunDto?> GetWorkflowRunAsync(Guid runId, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot look up a workflow run.");

        FileLog.Write($"[GatewayClient] GetWorkflowRunAsync: GET /gateway/workflow-runs/{runId}");
        using var resp = await _http.GetAsync($"gateway/workflow-runs/{runId}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Gateway could not look up workflow run '{runId}': HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}".TrimEnd());
        }
        var run = await resp.Content.ReadFromJsonAsync<WorkflowRunDto>(ct);
        if (run is null)
            throw new InvalidOperationException($"Gateway workflow-run lookup for '{runId}' returned an unparsable body.");
        return run;
    }

    /// <summary>The newest workflow run anchored to a mission, or null when the mission has none
    /// (a mission predating the run spine). Same fail-loud posture as the id lookup.</summary>
    public async Task<WorkflowRunDto?> GetMissionWorkflowRunAsync(Guid missionId, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot look up a mission's workflow run.");

        FileLog.Write($"[GatewayClient] GetMissionWorkflowRunAsync: GET /gateway/workflow-runs?missionId={missionId}");
        using var resp = await _http.GetAsync($"gateway/workflow-runs?missionId={missionId}&limit=1", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            // A Gateway that predates the run spine has no /gateway/workflow-runs route at all. That
            // is the PRODUCTION state until the Gateway's next deployment, and a mission spawn must
            // keep working against it - the session simply starts unseated, exactly as every session
            // did before this feature. Never conflated with an error: a 5xx or auth failure below
            // still throws.
            FileLog.Write($"[GatewayClient] GetMissionWorkflowRunAsync {missionId}: the Gateway has no " +
                          "workflow-run surface (predates the run spine) -> spawning unseated");
            return null;
        }
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Gateway could not list workflow runs for mission '{missionId}': HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}".TrimEnd());
        }
        var body = await resp.Content.ReadFromJsonAsync<WorkflowRunListResponse>(ct);
        return body?.Runs?.FirstOrDefault();
    }

    /// <summary>Record a session as a participant on a workflow run (persisted run-to-session
    /// membership, issue #1771). Throws on failure - the caller decides whether that fails the
    /// operation or is reported loudly beside an already-successful spawn.</summary>
    public async Task AddWorkflowRunParticipantAsync(
        Guid runId, WorkflowRunParticipantDto participant, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("Gateway is not configured; cannot record a run participant.");

        FileLog.Write($"[GatewayClient] AddWorkflowRunParticipantAsync: PATCH /gateway/workflow-runs/{runId} session={participant.SessionId}");
        using var content = JsonContent.Create(new PatchWorkflowRunRequest
        {
            AddParticipants = new List<WorkflowRunParticipantDto> { participant },
        });
        using var resp = await _http.PatchAsync($"gateway/workflow-runs/{runId}", content, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Gateway refused the run-participant record for '{runId}': HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}".TrimEnd());
        }
    }

    private sealed class WorkflowRunListResponse
    {
        public List<WorkflowRunDto>? Runs { get; set; }
    }

    /// <summary>
    /// Snooze Length mission (Phase 3): record or clear a Gateway-owned snooze/hold for a session by
    /// driving the Gateway's <c>POST /sessions/{sid}/hold</c> with the SAME authenticated client (already
    /// pointed at the resolved Gateway address and carrying the fleet token) the other Director-to-Gateway
    /// relays use - so the desktop reuses the Director's existing Gateway connection, never binding its own
    /// URL or token. The Gateway records the snooze-until AND forwards the hold back DOWN to the owning
    /// Director before answering, so on success <c>Session.OnHold</c> is already set.
    ///
    /// This is the <see cref="IGatewayHold"/> seam - the ONE method the Gateway Cleanup migration re-points
    /// from HTTP to the tunnel. Fail-loud (no fallback): THROWS when the Gateway is disabled or the call
    /// does not confirm, so the desktop shows a clear error and sets no local hold.
    /// </summary>
    public async Task RecordHoldAsync(string sessionId, bool onHold, int? snoozeMinutes = null, CancellationToken ct = default)
    {
        if (!_config.IsEnabled)
            throw new InvalidOperationException("The Gateway is not configured; snooze needs a Gateway connection.");
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id is required", nameof(sessionId));

        FileLog.Write(
            $"[GatewayClient] RecordHoldAsync: POST /sessions/{sessionId}/hold onHold={onHold}, "
            + $"snoozeMinutes={(snoozeMinutes is null ? "default" : snoozeMinutes.ToString())}");
        // A null SnoozeMinutes is what the plain Snooze click sends: the Gateway then applies the user's
        // default length. Only an explicit "Snooze for" choice carries a value.
        var body = new HoldRequest { OnHold = onHold, SnoozeMinutes = onHold ? snoozeMinutes : null };
        using var resp = await _http.PostAsJsonAsync($"sessions/{sessionId}/hold", body, ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, $"hold for {sessionId}", ct);
    }

    /// <summary>
    /// Read the user's snooze lengths and default from the Gateway (<c>GET /gateway/snooze-presets</c>).
    /// Returns null when the Gateway is not configured. Throws when it is configured but the call fails -
    /// the caller decides whether that is fatal; the desktop's cache treats it as "keep the last-known
    /// list" so a right-click never waits on the network.
    /// </summary>
    public async Task<SnoozeOptionsResponse?> GetSnoozeOptionsAsync(CancellationToken ct = default)
    {
        if (!_config.IsEnabled) return null;

        using var resp = await _http.GetAsync("gateway/snooze-presets", ct);
        if (!resp.IsSuccessStatusCode)
            throw await RelayFailureAsync(resp, "snooze lengths", ct);

        var options = await resp.Content.ReadFromJsonAsync<SnoozeOptionsResponse>(ct);
        if (options is null || options.Presets.Length == 0)
            throw new InvalidOperationException("The Gateway returned no snooze lengths.");

        FileLog.Write(
            $"[GatewayClient] GetSnoozeOptionsAsync: presets=[{string.Join(", ", options.Presets)}], "
            + $"default={options.DefaultMinutes}");
        return options;
    }

    // ===== Fleet session numbers (issue #1292) =====
    // The Gateway is the authority for the short three-digit session number (100-999) so it is
    // unique across every Director on every machine. The Director asks here when it creates a
    // session and frees the number when the session ends. Best-effort like the turn-brief fetch:
    // a null / failed allocate means the Director falls back to a local offline number, so these
    // never throw - an unreachable Gateway must not block session creation.

    /// <summary>
    /// Ask the Gateway to hand out this session's fleet-unique three-digit number (issue #1292).
    /// Returns the number when the Gateway answered; null when the Gateway is disabled (not
    /// configured), unreachable, or its pool is exhausted - the caller then assigns a local offline
    /// number instead. Idempotent on the Gateway: asking again for the same session id returns the
    /// same number.
    /// </summary>
    public async Task<int?> AllocateSessionNumberAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_config.IsEnabled || string.IsNullOrWhiteSpace(sessionId)) return null;
        try
        {
            var body = new SessionNumberAllocateRequest { SessionId = sessionId, DirectorId = _directorId };
            using var resp = await _http.PostAsJsonAsync("session-numbers/allocate", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[GatewayClient] AllocateSessionNumberAsync {sessionId}: HTTP {(int)resp.StatusCode}");
                return null;
            }
            var parsed = await resp.Content.ReadFromJsonAsync<SessionNumberAllocateResponse>(ct);
            return parsed?.Number;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayClient] AllocateSessionNumberAsync {sessionId} FAILED (offline fallback): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Tell the Gateway to free this session's number back to the pool when the session ends
    /// (issue #1292). Fire-and-forget: a lost release is reconciled anyway - the Gateway frees a
    /// number when its owning Director is removed from the registry. No-op while the Gateway is
    /// disabled or unregistered.
    /// </summary>
    public void ReleaseSessionNumber(string sessionId)
    {
        if (!_config.IsEnabled || _disposed || string.IsNullOrWhiteSpace(sessionId)) return;
        var cts = _cts;
        _ = Task.Run(async () =>
        {
            try
            {
                using var resp = await _http.DeleteAsync($"session-numbers/{sessionId}", cts?.Token ?? default);
                if (!resp.IsSuccessStatusCode)
                    FileLog.Write($"[GatewayClient] ReleaseSessionNumber {sessionId} -> {(int)resp.StatusCode} (dropped; director-removal reconciles)");
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayClient] ReleaseSessionNumber {sessionId} FAILED (dropped; director-removal reconciles): {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Start the registration lifecycle. Fire-and-forget: the first register attempt
    /// runs in the background so a slow or unreachable Gateway never blocks Director
    /// startup. The heartbeat timer is set up regardless.
    /// </summary>
    public void Start()
    {
        if (!_config.IsEnabled)
        {
            FileLog.Write("[GatewayClient] Start: disabled (no gateway.url), running in local-only mode");
            _monitor?.Reset(gatewayConfigured: false);
            return;
        }
        if (_disposed) throw new ObjectDisposedException(nameof(GatewayClient));
        _monitor?.Reset(gatewayConfigured: true);

        _cts = new CancellationTokenSource();
        FileLog.Write($"[GatewayClient] Start: candidates=[{string.Join(", ", _config.CandidateUrls)}], directorId={_directorId} (tunnel-only: no HTTP register/heartbeat/verify)");

        // Gateway Cleanup mission (tunnel-only): the HTTP register/heartbeat/verify connection loop is GONE.
        // The tunnel (GatewayStreamClient) is the ONLY Gateway connection - its Hello registers this Director
        // with the Gateway and its live state drives the connectivity light. This client survives ONLY as the
        // on-demand caller for the Director's outbound Gateway operations (fleet send/ask/fanout/spawn, hold,
        // session-number). Select the reachable Gateway address once so those calls have a base address; do
        // NOT register, heartbeat, or run the two-way verify handshake (the Gateway no longer dials back).
        _ = Task.Run(async () =>
        {
            try { await SelectActiveUrlAsync(_cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { FileLog.Write($"[GatewayClient] candidate selection failed, using {_activeUrl}: {ex.Message}"); }
        });
    }

    /// <summary>
    /// Gracefully unregister. Best-effort: a failing DELETE is logged but does not
    /// throw - the Gateway will sweep the stale entry within 60 s anyway.
    /// </summary>
    public async Task StopAsync()
    {
        if (_disposed) return;
        if (!_config.IsEnabled) return;

        FileLog.Write($"[GatewayClient] StopAsync: directorId={_directorId}");
        try { _cts?.Cancel(); } catch { }

        if (_registered)
        {
            try
            {
                var resp = await _http.DeleteAsync($"directors/{_directorId}/registration");
                FileLog.Write($"[GatewayClient] DELETE registration -> {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayClient] DELETE registration FAILED (best-effort): {ex.Message}");
            }
        }
        _registered = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _http.Dispose();
    }

    // ===== Internals =====

    /// <summary>
    /// Walk <see cref="GatewayConfig.CandidateUrls"/> in priority order (machine name, then Tailscale,
    /// then IP) and switch the active address to the first that answers GET /healthz (issue #1233).
    /// Setting the shared client's base address here is safe: this runs before the first outbound call.
    /// With a single candidate (older installs, or a manual override with no discovered fallbacks) there
    /// is nothing to choose and the method is a no-op. When nothing answers yet the active address is
    /// left as-is, so the on-demand Gateway calls still attempt it.
    /// Internal so the selection wiring is unit-tested through <see cref="ProbeGatewayCandidate"/>.
    /// </summary>
    internal async Task SelectActiveUrlAsync(CancellationToken ct)
    {
        var candidates = _config.CandidateUrls;
        if (candidates.Count <= 1) return;

        var selection = await GatewayEndpointSelector.SelectAsync(candidates, ProbeGatewayCandidate, ct);
        if (selection.Found)
        {
            if (!string.Equals(selection.ChosenUrl, _activeUrl, StringComparison.OrdinalIgnoreCase))
            {
                FileLog.Write($"[GatewayClient] selected reachable gateway {selection.ChosenUrl} from {candidates.Count} candidate(s)");
                SetActiveUrl(selection.ChosenUrl!);
            }
        }
        else
        {
            FileLog.Write($"[GatewayClient] no gateway candidate answered among {candidates.Count}; will attempt {_activeUrl} and retry");
        }
    }

    // Point the shared client at a newly chosen gateway address. Only call before the first request is
    // sent on _http - HttpClient forbids changing BaseAddress afterwards - which is the Start-time
    // selection above, before RegisterLoop.
    private void SetActiveUrl(string url)
    {
        _activeUrl = url;
        _http.BaseAddress = new Uri(url.TrimEnd('/') + "/");
    }

    // Default candidate probe: GET <url>/healthz with a short timeout. /healthz is unauthenticated so
    // no token is presented. Never throws - a transport failure or timeout becomes a reason string,
    // which makes GatewayEndpointSelector move to the next candidate.
    private static async Task<string?> ProbeGatewayHealthzAsync(string url, CancellationToken ct)
    {
        using var http = new HttpClient(GatewayHttp.Handler()) { Timeout = TimeSpan.FromSeconds(5) };
        return await GatewayEndpointSelector.ProbeHealthzAsync(url, http, ct);
    }

    /// <summary>
    /// The turn-end doorbell (issue #186): announce THAT a session's mechanical state
    /// changed - {sessionId, newState}, nothing else. Issue #330 extends the same channel
    /// with an optional event-vocabulary tag (<see cref="DoorbellEvents"/>): session-created,
    /// session-exited, prompt-detected ride the very same fire-and-forget ping. Failures are
    /// logged and dropped (the heartbeat snapshot reconciles within 15s); not sent while
    /// unregistered (the registration itself triggers the Gateway's catch-up).
    /// </summary>
    /// <param name="eventName">Optional <see cref="DoorbellEvents"/> name when this ping
    /// announces a lifecycle moment; null = a plain activity-transition ping (pre-#330 shape).</param>
    public void NotifySessionState(string sessionId, string newState, string? eventName = null)
    {
        // Gateway Cleanup mission (tunnel-only): no longer gated on HTTP registration (which is gone) - the
        // doorbell is an outbound Director->Gateway front-door notify that fires whenever a Gateway is
        // configured. Failures are dropped; the tunnel's periodic snapshot re-push reconciles roster state.
        if (!_config.IsEnabled || _disposed) return;
        var cts = _cts;
        if (cts is null || cts.IsCancellationRequested) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var req = new DoorbellRequest { SessionId = sessionId, NewState = newState, Event = eventName };
                var resp = await _http.PostAsJsonAsync($"directors/{_directorId}/doorbell", req, cts.Token);
                if (!resp.IsSuccessStatusCode)
                    FileLog.Write($"[GatewayClient] doorbell {sessionId} -> {(int)resp.StatusCode} (dropped; heartbeat reconciles)");
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayClient] doorbell {sessionId} FAILED (dropped; heartbeat reconciles): {ex.Message}");
            }
        });
    }

    // Gateway Cleanup mission (tunnel-only): the two-way verify handshake is GONE from this client.
    // MaybeKickVerify and VerifyAsync used to POST directors/{id}/verify and ask the Gateway to dial this
    // Director back on its advertised endpoint. The Gateway deleted that route - and its whole dial-back -
    // at the cut ("Liveness is now the tunnel connection itself"), and the Director's own GET /verify/{nonce}
    // callback door went with it, so the handshake could never pass again by two independent counts. What it
    // could still do was LIE: on the 404 it told the owner "the Gateway does not support the verify handshake
    // - update the Gateway", which sent him to fix a Gateway that was already correct, and flipped a healthy
    // green light to red for doing nothing but opening diagnostics. Liveness is the tunnel: GatewayStreamClient
    // marks the monitor connected/reconnecting directly.

    // Stamped by BuildRegistrationRequest: true while the last resolution found no tailnet
    // identity. RegisterLoop reads it to keep identity retries at heartbeat cadence (#324).
    private volatile bool _lastResolutionFailed;

    /// <summary>
    /// Build the registration body from a FRESH identity resolution (issue #324 - the
    /// detection ladder runs every time, never a forever-cache).
    ///
    /// The Director binds Kestrel to LOOPBACK only; the address advertised here is the
    /// Tailscale Serve front door (HTTPS, this node's MagicDNS name, THIS Director's own
    /// port, e.g. https://&lt;machine&gt;.&lt;tailnet&gt;.ts.net:7879). It is NEVER loopback - a remote
    /// Gateway or the Cockpit could never reach loopback - and never empty-but-claimed-
    /// reachable: when nothing resolves, <see cref="DirectorRegistrationRequest.TailnetEndpoint"/>
    /// is empty AND <see cref="DirectorRegistrationRequest.EndpointUnreachableReason"/> carries
    /// the reason (the regression the issue-#324 acceptance criteria pin).
    /// </summary>
    internal DirectorRegistrationRequest BuildRegistrationRequest()
    {
        var resolution = ResolveAdvertisedEndpoint();
        _lastResolutionFailed = !resolution.IsResolved;
        return new DirectorRegistrationRequest
        {
            DirectorId = _directorId,
            TailnetEndpoint = resolution.Endpoint,
            EndpointUnreachableReason = resolution.FailureReason,
            Pid = Environment.ProcessId,
            MachineName = Environment.MachineName,
            User = Environment.UserName,
            Version = _version,
            StartedAt = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
        };
    }
}
