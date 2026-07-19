using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// Reports a Director-startup event to the configured CC Director Gateway on launch (Gateway
/// Centralization Phase 1, issue #632): <c>POST &lt;gateway.url&gt;/telemetry/director-startup</c> with a
/// <c>{ director_id, machine_name, app_version }</c> body. Modeled on
/// <see cref="DevThrottleLoginTelemetryReporter"/> - same <c>gateway.url</c> resolution, the same
/// environment override seam, and the same best-effort, no-op-when-unconfigured pattern.
///
/// Phase 1 is transitional: when no Gateway URL is configured the reporter is a NO-OP that logs a skip
/// line - it never crashes and never falls back to a direct cloud call. The caller fires this detached
/// (off the user-interface thread) so a slow or failing report never delays the main window appearing.
///
/// Issue #1855: the report is AUTHENTICATED, with the same <c>gateway.token</c> Bearer every other
/// Director-to-Gateway call carries. It previously sent no credential at all, so a Gateway with its
/// host-wide gate on refused it 401 - and because the caller swallows failures by design, the only symptom
/// was startup telemetry that silently never arrived. The credential rides ONLY to this Director's own
/// configured Gateway, never to an address the <see cref="EndpointEnvVar"/> override names.
/// </summary>
public sealed class DevThrottleDirectorStartupTelemetryReporter : IDirectorStartupTelemetryReporter
{
    /// <summary>
    /// Environment seam to point the report at an explicit URL (tests, proof, staging). When set it
    /// overrides the Gateway-derived target. Unset uses the configured Gateway.
    /// </summary>
    public const string EndpointEnvVar = "DEVTHROTTLE_STARTUP_TELEMETRY_URL";

    /// <summary>The Gateway path the Director POSTs its startup event to, appended to <c>gateway.url</c>.</summary>
    public const string GatewayStartupPath = "/telemetry/director-startup";

    // A single shared client (best practice - avoids socket exhaustion). The short timeout keeps a
    // best-effort report from lingering; the caller fires it detached anyway.
    private static readonly HttpClient SharedClient = new(CreateDefaultHandler()) { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>
    /// The handler this reporter's own client uses. REDIRECTS ARE DISABLED, and that is a security guard, not
    /// a performance choice (issue #1855).
    ///
    /// The Bearer is attached only when the target host matches the configured Gateway
    /// (<see cref="TargetIsOwnGateway"/>), but a redirect would move the destination AFTER that check has
    /// passed: a request that legitimately starts at this Director's own Gateway could be bounced anywhere,
    /// carrying a key that authenticates every Director-to-Gateway call. Refusing to follow redirects at all
    /// makes an off-host header leak STRUCTURALLY IMPOSSIBLE rather than merely unlikely, and a fire-and-forget
    /// telemetry POST has no legitimate reason to follow one.
    ///
    /// This deliberately does NOT rest on the fact that .NET strips the Authorization header on a cross-origin
    /// redirect. That is true today, but it is framework internals: a security guarantee should be explicit and
    /// visible to a reviewer in this file, not dependent on behaviour a future framework version could change
    /// and nobody here would notice.
    ///
    /// Internal so a test can assert the guard and drive the real no-redirect behaviour.
    /// </summary>
    internal static HttpMessageHandler CreateDefaultHandler() => new SocketsHttpHandler { AllowAutoRedirect = false };

    private readonly HttpClient _client;
    private readonly string _machineName;
    private readonly string? _appVersion;
    private readonly string _gatewayUrl;
    private readonly string _gatewayToken;

    /// <summary>
    /// Creates the reporter. <paramref name="client"/> defaults to a shared <see cref="HttpClient"/>;
    /// tests inject one over a fake handler. <paramref name="machineName"/> defaults to
    /// <see cref="Environment.MachineName"/>. <paramref name="appVersion"/> is sent as the optional
    /// <c>app_version</c> body field when present (defaults to <see cref="AppVersion.Semver"/>).
    /// <paramref name="gatewayUrl"/> is the Gateway base URL the event is POSTed to; it defaults to
    /// <c>gateway.url</c> from config.json (<see cref="GatewayConfig.Load"/>). An empty Gateway URL with
    /// no <see cref="EndpointEnvVar"/> override makes the reporter a logged no-op.
    /// </summary>
    /// <param name="gatewayToken">
    /// The Gateway credential this Director already holds - <c>gateway.token</c> from config.json, which is
    /// the per-device key enrollment wrote there (issue #1855). Sent as the Bearer on the Gateway-derived
    /// target so this report authenticates like every other Director-to-Gateway call. Defaults to the
    /// configured value; tests pass it explicitly.
    /// </param>
    public DevThrottleDirectorStartupTelemetryReporter(HttpClient? client = null, string? machineName = null, string? appVersion = null, string? gatewayUrl = null, string? gatewayToken = null)
    {
        _client = client ?? SharedClient;
        _machineName = string.IsNullOrWhiteSpace(machineName) ? Environment.MachineName : machineName.Trim();
        _appVersion = appVersion ?? AppVersion.Semver;
        // config.json is read ONLY when the target was not supplied. A caller that names the Gateway is
        // configuring this reporter explicitly - which every test does, so a test never picks up the machine's
        // own gateway.token and the credential under test is always the one the test chose.
        var config = gatewayUrl is null ? GatewayConfig.Load() : null;
        _gatewayUrl = (gatewayUrl ?? config!.Url).Trim();
        _gatewayToken = (gatewayToken ?? config?.Token ?? "").Trim();
    }

    /// <summary>
    /// Resolves the URL the startup event is POSTed to: the <see cref="EndpointEnvVar"/> environment
    /// override when set (trimmed, non-empty), otherwise <c>&lt;gateway.url&gt;/telemetry/director-startup</c>.
    /// Returns null when neither is configured - the signal for the no-op path.
    /// </summary>
    private string? ResolveTargetUrl()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EndpointEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();

        if (string.IsNullOrWhiteSpace(_gatewayUrl))
            return null;

        return $"{_gatewayUrl.TrimEnd('/')}{GatewayStartupPath}";
    }

    /// <summary>
    /// True when the target is THIS Director's own configured Gateway - the only host the Gateway credential
    /// may be sent to (issue #1855).
    ///
    /// <see cref="EndpointEnvVar"/> can point the report at an ARBITRARY address for a test, proof or staging
    /// run. Attaching the credential unconditionally would hand this machine's per-device Gateway key to
    /// whatever host that variable names - a key that authenticates every Director-to-Gateway call, handed
    /// over by setting one environment variable. So the Bearer rides only on the Gateway-derived target, and
    /// an override that happens to point back at the configured Gateway still qualifies because it IS that
    /// Gateway.
    /// </summary>
    private bool TargetIsOwnGateway(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(_gatewayUrl))
            return false;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var target)
            || !Uri.TryCreate(_gatewayUrl, UriKind.Absolute, out var gateway))
            return false;

        // Same host, port and scheme. A different scheme is not the same destination: sending the key over
        // plain http to a host we know as https is exactly the downgrade this check exists to refuse.
        return string.Equals(target.Scheme, gateway.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(target.Host, gateway.Host, StringComparison.OrdinalIgnoreCase)
            && target.Port == gateway.Port;
    }

    public async Task ReportStartupAsync(string directorId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(directorId))
            throw new ArgumentException("Director id is required", nameof(directorId));

        var endpoint = ResolveTargetUrl();
        if (endpoint is null)
        {
            // Phase 1 transitional no-op: no Gateway configured means no egress target. We do NOT fall
            // back to calling the cloud directly. The Gateway is mandatory in Phase 3.
            FileLog.Write("[DevThrottleDirectorStartupTelemetryReporter] ReportStartupAsync: no gateway.url configured, skipping director-startup telemetry (Phase 1 no-op)");
            return;
        }

        var body = new JsonObject
        {
            ["director_id"] = directorId,
            ["machine_name"] = _machineName,
        };
        if (!string.IsNullOrWhiteSpace(_appVersion))
            body["app_version"] = _appVersion;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        // Issue #1855: AUTHENTICATE THE REPORT. This request used to carry no Authorization header at all,
        // while every other Director-to-Gateway call sends `Bearer gateway.token`. The Gateway gate is
        // host-wide, so the report was refused 401 by any Gateway with auth on - and the failure was
        // swallowed, so the only symptom was an absence, which looks exactly like nobody having started a
        // Director. It surfaced on hosted because hosted is authenticated by construction. The credential is
        // the same per-device key enrollment already wrote to gateway.token and that the tunnel and the
        // cockpit reads use in the same boot.
        //
        // What this does NOT do: attribute the event to an account. The receiving endpoint does not read the
        // request's tenant - it writes a process-global record line and enqueues the raw body globally - so
        // sending the credential makes the report ARRIVE and be recorded, nothing more. Claiming otherwise
        // was an error in the first version of this change.
        var authenticated = !string.IsNullOrEmpty(_gatewayToken) && TargetIsOwnGateway(endpoint);
        if (authenticated)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _gatewayToken);

        FileLog.Write($"[DevThrottleDirectorStartupTelemetryReporter] ReportStartupAsync: POSTing director-startup event to gateway {endpoint} (director_id={directorId}, machine_name={_machineName}, authenticated={authenticated})");
        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        FileLog.Write($"[DevThrottleDirectorStartupTelemetryReporter] ReportStartupAsync: response status={(int)response.StatusCode}");

        // A REFUSED CREDENTIAL IS NOT A TRANSIENT, and it must not read like one. The caller swallows every
        // failure here by design - a telemetry report must never delay or fail startup - so a 401 or 403 that
        // logged like any other error left a permanently broken configuration indistinguishable from a blip.
        // Say plainly what was refused and what to do, and say it once, at the moment it happens.
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            FileLog.Write(
                $"[DevThrottleDirectorStartupTelemetryReporter] ReportStartupAsync: the Gateway REFUSED this Director's credential " +
                $"({(int)response.StatusCode}) for {endpoint}. This is a configuration fault, not a transient failure - it will not " +
                $"recover on its own, and startup telemetry from this machine will be MISSING until it is fixed. " +
                (authenticated
                    ? "The credential sent was gateway.token from config.json; re-enroll this machine to obtain a valid one."
                    : "NO credential was sent, because gateway.token is not set in config.json or the target is not this Director's own configured Gateway."));
        }

        response.EnsureSuccessStatusCode();
    }
}
