using CcDirector.Core.Account;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Account;

/// <summary>
/// Registers THIS Gateway as a device with the DevThrottle cloud account on sign-in (issue #857): when
/// the Gateway is signed in, it calls the cloud "register this device" endpoint
/// (<see cref="DeviceRegistryClient.RegisterAsync"/>) with the Gateway's stable install identity
/// (<see cref="GatewayInstallId"/>), machine name, and platform, and stores the cloud-issued per-device
/// key locally (<see cref="GatewayDeviceKeyStore"/>). This is what makes "sign into the same account on a
/// new device" join the fleet and populates the account-wide device list.
///
/// Idempotency (issue #857) has two guards:
/// <list type="bullet">
/// <item>An in-run guard: once a registration has succeeded in this process, a second call is a no-op, so
/// the sign-in-completion trigger and the first-heartbeat trigger never register twice in one run.</item>
/// <item>A relaunch guard: if a per-device key is already stored for this install id (a previous run
/// registered it), a fresh process does NOT re-register - it reuses the stored key. Combined with the
/// cloud being idempotent per install id, a relaunch or a second sign-in never creates a duplicate
/// device record.</item>
/// </list>
///
/// Graceful degradation (issue #857, consistent with #651/#664): this service NEVER blocks or gates the
/// Gateway. <see cref="EnsureRegisteredAsync"/> simply returns when the Gateway is not signed in, and lets
/// a cloud failure throw to its caller (the heartbeat tick boundary or the detached sign-in callback),
/// which logs it and retries on the next heartbeat - the Gateway stays signed in and running.
///
/// Security rule DT-05: the per-device key is never written to the log on any path.
/// </summary>
public sealed class GatewayDeviceRegistrationService
{
    /// <summary>The device type this Gateway registers itself as.</summary>
    public const string GatewayDeviceType = "gateway";

    private readonly DevThrottleAccountService _account;
    private readonly DeviceRegistryClient _client;
    private readonly GatewayDeviceKeyStore _keyStore;
    private readonly Func<string> _installIdProvider;
    private readonly Func<IReadOnlyList<string>>? _endpointUrlsProvider;
    private readonly string _machineName;
    private readonly string _platform;
    private readonly string? _appVersion;
    private readonly object _gate = new();

    private string? _installId;
    private bool _registeredThisRun;
    // Issue #1233: the endpoint_urls list we last published to the account this run. Guarded by _gate. Null
    // until the first publish. A heartbeat re-sends the list ONLY when the freshly computed set differs from
    // this - so a routine heartbeat where nothing changed omits the field and leaves the stored value
    // untouched (issue #334 rule: an absent field is not applied), instead of re-writing the same value every
    // few minutes.
    private IReadOnlyList<string>? _lastPublishedEndpointUrls;

    /// <summary>
    /// Creates the registration coordinator.
    /// </summary>
    /// <param name="account">The Gateway-hosted credential service the egress token is read from. Required.</param>
    /// <param name="client">The cloud device-registry client (the injectable egress seam). Required.</param>
    /// <param name="keyStore">The local store the issued per-device key is written to. Required.</param>
    /// <param name="machineName">This machine's name, sent as the device name. Required.</param>
    /// <param name="platform">This device's platform string (for example "windows"). Required.</param>
    /// <param name="appVersion">The reporting app version, or null when omitted.</param>
    /// <param name="installIdProvider">
    /// Resolves the stable Gateway install id; in production the host owns one <see cref="GatewayInstallId"/>
    /// instance and passes its resolver, and when omitted it defaults to a fresh instance over the default
    /// path. Tests inject a fixed provider so they never touch the real config root. Resolved lazily on
    /// first use (never at construction) and cached, so constructing this service does no disk I/O.
    /// </param>
    /// <param name="endpointUrlsProvider">
    /// Resolves THIS Gateway's own reachable front-door URLs in priority order to publish as the device's
    /// <c>endpoint_urls</c> (issue #1233): machine name first, then the Tailscale address (only when Tailscale
    /// is available), then the local network IP - so an installer on another machine discovers the gateway
    /// address from the account and tries them in order instead of the person typing one. The single
    /// <c>endpoint_url</c> (issue #1206) is derived as the first entry. Returns an empty list when no address
    /// can be resolved yet, in which case no address is sent - a heartbeat later backfills it. Null (the
    /// default, and the pre-#1206 behavior) publishes no address at all.
    /// </param>
    public GatewayDeviceRegistrationService(
        DevThrottleAccountService account,
        DeviceRegistryClient client,
        GatewayDeviceKeyStore keyStore,
        string machineName,
        string platform,
        string? appVersion = null,
        Func<string>? installIdProvider = null,
        Func<IReadOnlyList<string>>? endpointUrlsProvider = null)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _machineName = machineName ?? throw new ArgumentNullException(nameof(machineName));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _appVersion = appVersion;
        _installIdProvider = installIdProvider ?? new GatewayInstallId().LoadOrCreate;
        _endpointUrlsProvider = endpointUrlsProvider;
    }

    /// <summary>
    /// THIS Gateway's own reachable front-door URLs in priority order to publish as <c>endpoint_urls</c>
    /// (issue #1233), or an empty list when no provider is wired or none can be resolved yet. Read fresh on
    /// every register and heartbeat so an address that appears (or changes) after start is picked up without a
    /// restart. The resolver never throws (it reports unresolved addresses as an empty list), so this is safe
    /// on any path. Blank entries are dropped so only real addresses are published.
    /// </summary>
    public IReadOnlyList<string> ResolveEndpointUrls()
    {
        var urls = _endpointUrlsProvider?.Invoke();
        if (urls is null || urls.Count == 0)
            return Array.Empty<string>();

        var cleaned = new List<string>(urls.Count);
        foreach (var url in urls)
            if (!string.IsNullOrWhiteSpace(url))
                cleaned.Add(url);
        return cleaned;
    }

    /// <summary>
    /// THIS Gateway's own reachable front-door URL to publish as the single <c>endpoint_url</c> field (issue
    /// #1206) - the FIRST entry of <see cref="ResolveEndpointUrls"/>, so a reader of the single field keeps
    /// working unchanged (non-breaking, issue #1233). Null when no address can be resolved yet.
    /// </summary>
    public string? ResolveEndpointUrl()
    {
        var urls = ResolveEndpointUrls();
        return urls.Count > 0 ? urls[0] : null;
    }

    /// <summary>
    /// The endpoint URLs to publish on THIS heartbeat (issue #1233 / the #334 publish refinement): the
    /// current ordered list ONLY when it differs from what was last published this run (a real change - an IP
    /// change, Tailscale coming or going, a hostname change), otherwise an EMPTY list so the caller omits the
    /// field entirely and the account leaves the stored value untouched. This is what stops a routine
    /// heartbeat from re-writing the same address list every few minutes. When it reports a change it records
    /// the new list as the last-published set, so the very next unchanged heartbeat omits again. An empty
    /// resolution (no address available yet) is never published (we never appear to clear a value) and does
    /// not disturb the recorded set. Thread-safe.
    /// </summary>
    public IReadOnlyList<string> NextEndpointUrlsToPublish()
    {
        var current = ResolveEndpointUrls();
        lock (_gate)
        {
            // Nothing resolvable yet -> publish nothing (never send an empty list; the account would ignore
            // it, and we must never look like we are clearing a hand-set value). Leave the recorded set as-is.
            if (current.Count == 0)
                return Array.Empty<string>();

            // Unchanged since our last publish -> omit, so the stored value is left untouched.
            if (_lastPublishedEndpointUrls is not null && EndpointUrlsEqual(_lastPublishedEndpointUrls, current))
                return Array.Empty<string>();

            _lastPublishedEndpointUrls = current;
            return current;
        }
    }

    /// <summary>
    /// Records <paramref name="urls"/> as the endpoint_urls set last published this run, so a following
    /// heartbeat that computes the same set omits the field. Called after a successful register publish.
    /// A null or empty list clears nothing (leaves the recorded set as-is). Thread-safe.
    /// </summary>
    private void MarkEndpointUrlsPublished(IReadOnlyList<string> urls)
    {
        if (urls.Count == 0)
            return;
        lock (_gate)
            _lastPublishedEndpointUrls = urls;
    }

    /// <summary>Ordered, case-insensitive equality of two endpoint_urls lists. Pure - unit-tested via the
    /// service's publish behaviour. The list order is deterministic (machine name, Tailscale, LAN IP), so an
    /// ordered compare is the same as a set compare here and also catches a genuine reorder.</summary>
    private static bool EndpointUrlsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    /// <summary>This Gateway's stable install id, resolved lazily and cached (no disk I/O at construction).</summary>
    public string InstallId
    {
        get
        {
            lock (_gate)
            {
                _installId ??= _installIdProvider();
                return _installId;
            }
        }
    }

    /// <summary>True when a per-device key is stored for this install id (registered, in this or a prior run).</summary>
    public bool HasDeviceKey => _keyStore.HasKeyForInstall(InstallId);

    /// <summary>
    /// Registers this Gateway as a device when needed, exactly once per run. A no-op when: a registration
    /// already succeeded in this run (in-run guard); the Gateway is not signed in (graceful - logs and
    /// returns, never blocks); or a per-device key is already stored for this install id (relaunch guard -
    /// reuses the stored key, no duplicate device). Otherwise it calls the cloud register endpoint and
    /// stores the issued key. Does NOT swallow a cloud failure - the call throws to its boundary caller,
    /// which logs and retries on the next heartbeat (the per-#857 graceful-degradation contract). The
    /// issued key is never logged (DT-05).
    /// </summary>
    public async Task EnsureRegisteredAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_registeredThisRun)
            {
                FileLog.Write("[GatewayDeviceRegistrationService] EnsureRegisteredAsync: already registered this run -> no-op (in-run idempotency guard)");
                return;
            }
        }

        var token = _account.GetAccessTokenForForwarding();
        if (string.IsNullOrEmpty(token))
        {
            FileLog.Write("[GatewayDeviceRegistrationService] EnsureRegisteredAsync: Gateway not signed in -> skipping device registration (retry on the next heartbeat)");
            return;
        }

        var installId = InstallId;
        if (_keyStore.HasKeyForInstall(installId))
        {
            lock (_gate) { _registeredThisRun = true; }
            FileLog.Write($"[GatewayDeviceRegistrationService] EnsureRegisteredAsync: a per-device key is already stored for install_id={installId} -> skipping re-registration (relaunch idempotency guard)");
            return;
        }

        // Issue #1233 (following #1206): publish this Gateway's own advertised front-door URLs in priority
        // order so an installer on another machine can discover the gateway address from the account and try
        // them in order. The single endpoint_url stays the first entry (non-breaking). Empty when nothing can
        // resolve yet - a heartbeat backfills it later.
        var endpointUrls = ResolveEndpointUrls();
        var endpointUrl = endpointUrls.Count > 0 ? endpointUrls[0] : null;
        FileLog.Write($"[GatewayDeviceRegistrationService] EnsureRegisteredAsync: registering this Gateway as a device, install_id={installId}, name={_machineName}, platform={_platform}, endpoint_url={endpointUrl ?? "(unresolved)"}, endpoint_urls=[{string.Join(", ", endpointUrls)}]");
        var request = new CloudDeviceRegistrationRequest(installId, _platform, _machineName, GatewayDeviceType, _appVersion, endpointUrl, endpointUrls);
        var result = await _client.RegisterAsync(token, request, ct).ConfigureAwait(false);

        _keyStore.Save(installId, result.DeviceKey);
        lock (_gate) { _registeredThisRun = true; }
        // Record what we just published so a following heartbeat with the same set omits endpoint_urls (#1233).
        MarkEndpointUrlsPublished(endpointUrls);
        FileLog.Write($"[GatewayDeviceRegistrationService] EnsureRegisteredAsync: registered device id={result.Device.Id} and stored its per-device key (key value not logged)");
    }

    /// <summary>
    /// Discards the local registration state - clears the in-run guard and removes the stored key - so the
    /// next <see cref="EnsureRegisteredAsync"/> re-registers. Used when the cloud reports it no longer knows
    /// this install (a 404 heartbeat).
    /// </summary>
    public void ResetRegistration()
    {
        lock (_gate) { _registeredThisRun = false; }
        _keyStore.Clear();
        FileLog.Write("[GatewayDeviceRegistrationService] ResetRegistration: cleared local registration state (will re-register on the next attempt)");
    }
}
