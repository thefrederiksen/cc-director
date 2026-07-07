using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Resolves the DevThrottle API key a Director uses for transcription, the wingman, and text-to-speech,
/// honoring the two-source design (docs/architecture/gateway/GATEWAY_KEY_VAULT.md):
///
///   * Connected to a Gateway -> pull the key from the Gateway's central vault
///     (GET /vault/keys/DEVTHROTTLE_API_KEY) and cache it in memory. Never written to local disk.
///   * Standalone (no gateway configured) -> read the key from the LOCAL key vault file (issue #839).
///
/// The key vault is the single key store (issue #839): there is no config.json key copy. When neither
/// the Gateway vault nor the local vault yields a key, transcription is unavailable and
/// <see cref="UnavailableMessage"/> tells the user where to set it.
///
/// The gateway config is re-read on every resolve (not snapshotted at construction), so a
/// Director that booted standalone and later had a <c>gateway.url</c> added to config.json
/// self-heals into Gateway mode without a restart.
///
/// One resolver is meant to be long-lived (the in-memory key cache spans dictation sessions); a
/// fetch is retried after <see cref="InvalidateCache"/>, which callers invoke when the
/// provider rejects the key (rotation).
/// </summary>
public sealed class TranscriptionKeyResolver
{
    /// <summary>The vault key name the DevThrottle credential is stored under.</summary>
    public const string KeyName = TranscriptionEndpointResolver.DevThrottleKeyName;

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly Func<GatewayConfig> _gatewayProvider;
    private readonly HttpClient _http;
    private readonly KeyVault _localVault;
    private readonly object _gate = new();
    // Cache is keyed by vault key name.
    private readonly Dictionary<string, string> _cachedGatewayKeys = new(StringComparer.Ordinal);

    // Set by the on-Gateway routing resolve when the attached Gateway is too old to expose the
    // /transcription/routing endpoint (issue #506). Surfaced via UnavailableMessage so the user is
    // told to update the Gateway rather than the routing silently falling back to a baked-in URL.
    private volatile bool _gatewayMissingRoutingEndpoint;

    /// <summary>
    /// Primary constructor. <paramref name="gatewayProvider"/> is invoked fresh every time the
    /// key is resolved, so a config.json change (e.g. a gateway.url added after the Director booted)
    /// is honored without a restart. Production passes <see cref="GatewayConfig.Load"/>; tests pass a
    /// closure they can flip.
    /// </summary>
    /// <param name="gatewayProvider">Supplies the current gateway config on demand.</param>
    /// <param name="http">HTTP client for the vault fetch (tests inject a stub).</param>
    /// <param name="localVault">The standalone (no-Gateway) key store - the local key vault file
    /// (issue #839). Defaults to the shared local <see cref="KeyVault"/>; tests inject a temp-file vault.</param>
    public TranscriptionKeyResolver(Func<GatewayConfig> gatewayProvider, HttpClient? http = null, KeyVault? localVault = null)
    {
        _gatewayProvider = gatewayProvider ?? throw new ArgumentNullException(nameof(gatewayProvider));
        _http = http ?? SharedHttp;
        _localVault = localVault ?? new KeyVault();
    }

    /// <summary>
    /// Convenience constructor pinning a FIXED gateway config (tests). When <paramref name="gateway"/>
    /// is null, falls back to the dynamic <see cref="GatewayConfig.Load"/>.
    /// </summary>
    /// <param name="gateway">A fixed gateway config, or null to read it live from config.json.</param>
    /// <param name="http">HTTP client for the vault fetch (tests inject a stub).</param>
    public TranscriptionKeyResolver(GatewayConfig? gateway = null, HttpClient? http = null)
        : this(gateway is null ? GatewayConfig.Load : () => gateway, http)
    {
    }

    /// <summary>True when this Director pulls keys from a Gateway (vs. the local standalone key).</summary>
    public bool UsesGateway => _gatewayProvider().IsEnabled;

    /// <summary>
    /// The message to show when no key is available, so the user knows where to set one.
    /// </summary>
    public string UnavailableMessage
    {
        get
        {
            if (_gatewayProvider().IsEnabled)
            {
                // Older Gateway that predates the routing endpoint (issue #506): we will not guess
                // a base URL, so tell the user to update the Gateway rather than fail obscurely.
                if (_gatewayMissingRoutingEndpoint)
                    return "Transcription routing is unavailable: this Gateway is out of date. Update your Gateway to the latest version.";

                return "DevThrottle key is not set. Open the Cockpit Account page and sign in to your DevThrottle account.";
            }

            return "DevThrottle key is not set. Open Settings > Account and sign in to your DevThrottle account.";
        }
    }

    /// <summary>
    /// Resolve the routing target: the DevThrottle base URL, the model, and the credential. Returns
    /// null when no routing is available (transcription should then be reported unavailable via
    /// <see cref="UnavailableMessage"/>, never failed with a raw provider error).
    ///
    /// On a Gateway (issue #506) the WHOLE target is served by the Gateway in one call
    /// (GET /transcription/routing): the Director no longer resolves the URL from compile-time
    /// constants, so changing the URL is a Gateway-side setting with no Director rebuild or restart.
    /// Standalone (no Gateway) still resolves locally, unchanged.
    /// </summary>
    public async Task<ResolvedTranscription?> ResolveEndpointAsync(CancellationToken ct = default)
    {
        var gateway = _gatewayProvider();
        if (gateway.IsEnabled)
            return await ResolveEndpointFromGatewayAsync(gateway, ct);

        // Standalone: resolve the URL/model/key locally. A null key below means the user has not signed
        // in / set a DevThrottle key yet.
        var endpoint = TranscriptionEndpointResolver.Resolve();

        var key = await ResolveKeyAsync(endpoint.KeyName, ct);
        if (string.IsNullOrWhiteSpace(key))
            return null;

        return new ResolvedTranscription
        {
            BaseUrl = endpoint.BaseUrl,
            ApiKey = key,
            Model = endpoint.Model,
        };
    }

    /// <summary>
    /// Resolve only the key (the legacy single-value API). Returns null when none is available. Kept
    /// so existing callers that only need the key compile unchanged.
    /// </summary>
    public async Task<string?> ResolveAsync(CancellationToken ct = default)
    {
        return await ResolveKeyAsync(TranscriptionEndpointResolver.Resolve().KeyName, ct);
    }

    /// <summary>Forget any cached Gateway keys so the next resolve re-fetches (e.g. after rotation).</summary>
    public void InvalidateCache()
    {
        lock (_gate) _cachedGatewayKeys.Clear();
    }

    private async Task<string?> ResolveKeyAsync(string keyName, CancellationToken ct)
    {
        var gateway = _gatewayProvider();
        if (gateway.IsEnabled)
            return await ResolveFromGatewayAsync(gateway, keyName, ct);

        // Standalone (no Gateway): read the key from the LOCAL key vault file - the same single store
        // the Gateway owns (issue #839).
        var local = _localVault.Get(keyName);
        return string.IsNullOrWhiteSpace(local) ? null : local.Trim();
    }

    private async Task<string?> ResolveFromGatewayAsync(GatewayConfig gateway, string keyName, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_cachedGatewayKeys.TryGetValue(keyName, out var cached) && !string.IsNullOrEmpty(cached))
                return cached;
        }

        var url = gateway.Url.TrimEnd('/') + "/vault/keys/" + keyName;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(gateway.Token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token);

            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return null; // gateway reachable, key simply not set yet
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[TranscriptionKeyResolver] vault GET {url} -> {(int)resp.StatusCode}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var value = doc.RootElement.TryGetProperty("value", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(value))
                return null;

            lock (_gate) _cachedGatewayKeys[keyName] = value;
            return value;
        }
        catch (Exception ex)
        {
            // Gateway configured but unreachable: transcription is unavailable for now. We do not
            // silently use a local key here - on a Gateway, the Gateway is the source of truth.
            FileLog.Write($"[TranscriptionKeyResolver] vault fetch failed ({url}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fetch the WHOLE routing target from the Gateway (issue #506): base URL + model + key, composed
    /// server-side. Returns null when routing is unavailable. The on-Gateway path never reads a
    /// compile-time URL constant. An older Gateway that lacks the route is detected by the absence of
    /// the X-Transcription-Routing marker header on its 404 and flips
    /// <see cref="_gatewayMissingRoutingEndpoint"/> so the "update your Gateway" message shows -
    /// no silent fallback to a baked-in URL.
    /// </summary>
    private async Task<ResolvedTranscription?> ResolveEndpointFromGatewayAsync(GatewayConfig gateway, CancellationToken ct)
    {
        _gatewayMissingRoutingEndpoint = false;
        var url = gateway.Url.TrimEnd('/') + "/transcription/routing";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(gateway.Token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token);

            using var resp = await _http.SendAsync(req, ct);

            // The routing route stamps every response with X-Transcription-Routing. Its absence on
            // a 404 means an older Gateway that never mapped the route (vs. "key not set yet").
            var fromRoutingRoute = resp.Headers.Contains("X-Transcription-Routing");

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                if (!fromRoutingRoute)
                {
                    _gatewayMissingRoutingEndpoint = true;
                    FileLog.Write($"[TranscriptionKeyResolver] routing GET {url} -> 404 with no routing marker; Gateway is out of date");
                }
                return null; // older Gateway, or key simply not set
            }
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[TranscriptionKeyResolver] routing GET {url} -> {(int)resp.StatusCode}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var baseUrl = root.TryGetProperty("baseUrl", out var b) ? b.GetString() : null;
            var key = root.TryGetProperty("key", out var k) ? k.GetString() : null;
            var model = root.TryGetProperty("model", out var m) ? m.GetString() : null;

            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key)
                || string.IsNullOrWhiteSpace(model))
            {
                FileLog.Write($"[TranscriptionKeyResolver] routing GET {url} -> incomplete payload (baseUrl/key/model)");
                return null;
            }

            return new ResolvedTranscription
            {
                BaseUrl = baseUrl,
                ApiKey = key,
                Model = model,
            };
        }
        catch (Exception ex)
        {
            // Gateway configured but unreachable: transcription is unavailable for now. We do not
            // silently use a local URL/key here - on a Gateway, the Gateway is the source of truth.
            FileLog.Write($"[TranscriptionKeyResolver] routing fetch failed ({url}): {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// The resolved transcription routing target: the DevThrottle base URL plus the credential to present.
/// </summary>
public sealed record ResolvedTranscription
{
    /// <summary>The OpenAI-compatible base URL, e.g. <c>https://devthrottle.com/api/v1</c>.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>The credential to present (a <c>dt_</c> key).</summary>
    public required string ApiKey { get; init; }

    /// <summary>The transcription model to use - <c>whisper-large-v3</c>. Part of the routing target
    /// the Gateway serves (issue #506).</summary>
    public required string Model { get; init; }
}
