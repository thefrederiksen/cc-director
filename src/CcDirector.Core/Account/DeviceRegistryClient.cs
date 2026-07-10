using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// One MASKED device record as it lives on the DevThrottle account device registry.
/// Every field here is safe to surface to a caller: the registry never
/// returns the device's key hash or any raw key, only the prefix/last4 of the key for display. This
/// record carries NO account access or refresh token (security rule DT-05) - it describes a device,
/// not a credential.
/// </summary>
/// <param name="Id">The device's stable identifier (used to revoke it).</param>
/// <param name="Name">The human-readable device name (typically the machine name at registration).</param>
/// <param name="Platform">The operating-system platform string, or null when the cloud omits it.</param>
/// <param name="DeviceType">The device type (for example "gateway" or "phone"), or null when omitted.</param>
/// <param name="AppVersion">The app version last reported by the device, or null when omitted.</param>
/// <param name="KeyPrefix">The masked key prefix for display, or null when omitted.</param>
/// <param name="KeyLast4">The masked key last-four for display, or null when omitted.</param>
/// <param name="CreatedAt">When the device was registered, or null when omitted.</param>
/// <param name="LastSeenAt">When the device was last seen, or null when omitted.</param>
/// <param name="EndpointUrl">The device's reachable front-door URL (for example a gateway's advertised
/// tailnet address), or null when the device has none. For a "gateway" device this is what the installer
/// discovers to enroll a Workstation against, so the person never types the gateway address (issue #1206).
/// Kept equal to the first entry of <see cref="EndpointUrls"/> so a reader of this single field keeps
/// working unchanged (issue #1233, non-breaking).</param>
/// <param name="EndpointUrls">The device's reachable front-door URLs in priority order (issue #1233), or
/// null when the device published none. A Gateway publishes machine name first, then its Tailscale address
/// (only when Tailscale is available), then its local network IP - so a joining machine can try them in
/// order and use the first that answers. <see cref="EndpointUrl"/> holds the first entry.</param>
public sealed record CloudDeviceRecord(
    string Id,
    string Name,
    string? Platform,
    string? DeviceType,
    string? AppVersion,
    string? KeyPrefix,
    string? KeyLast4,
    string? CreatedAt,
    string? LastSeenAt,
    string? EndpointUrl,
    IReadOnlyList<string>? EndpointUrls = null);

/// <summary>
/// The request body for registering THIS device with the DevThrottle account:
/// <c>POST /api/v1/devices/register</c>. The cloud is idempotent per
/// (member, <see cref="InstallId"/>) - re-registering the same install rotates the device key and
/// updates the record rather than creating a second row - so the caller MUST always send the same
/// stable install id to avoid duplicate device records (issue #857).
/// </summary>
/// <param name="InstallId">The Gateway's stable, per-machine install identifier (the idempotency key). Required.</param>
/// <param name="Platform">The operating-system platform string (for example "windows"). Required.</param>
/// <param name="Name">A human-readable device name (typically the machine name), or null to let the cloud default it.</param>
/// <param name="DeviceType">The device type (for example "gateway"), or null when omitted.</param>
/// <param name="AppVersion">The reporting app version, or null when omitted.</param>
/// <param name="EndpointUrl">This device's reachable front-door URL, or null when it has none. A Gateway
/// publishes its own advertised address here so an installer on another machine can discover it (issue
/// #1206); other device types leave it null. Kept equal to the first entry of <see cref="EndpointUrls"/>.</param>
/// <param name="EndpointUrls">This device's reachable front-door URLs in priority order (issue #1233), or
/// null/empty when it has none. A Gateway publishes machine name first, then its Tailscale address (only
/// when Tailscale is available), then its local network IP; other device types leave it null.</param>
public sealed record CloudDeviceRegistrationRequest(
    string InstallId,
    string Platform,
    string? Name,
    string? DeviceType,
    string? AppVersion,
    string? EndpointUrl = null,
    IReadOnlyList<string>? EndpointUrls = null);

/// <summary>
/// The result of a device registration: the per-device key the cloud issues ONCE (in plain text, only
/// in this response - it is never returned again and is never written to the log, security rule DT-05)
/// plus the masked <see cref="CloudDeviceRecord"/> describing the registered device.
/// </summary>
/// <param name="DeviceKey">The plain per-device key, returned exactly once. Stored locally, never logged.</param>
/// <param name="Device">The masked device record (no raw key) for display and identification.</param>
public sealed record CloudDeviceRegistrationResult(
    string DeviceKey,
    CloudDeviceRecord Device);

/// <summary>
/// A small HTTP client for the DevThrottle account device registry.
/// It lists the signed-in account's active devices with
/// <c>GET /api/v1/devices</c> and revokes one with <c>DELETE /api/v1/devices/{id}</c>, both authenticated
/// with the Bearer access token the Gateway already holds for cloud egress (the same credential
/// <see cref="DevThrottleAccountService.GetAccessTokenForForwarding"/> returns for telemetry forwarding).
///
/// The endpoint base is resolved from <see cref="AccountTelemetryClient.ApiBaseUrlEnvVar"/> when set (so
/// development and QA can point at a local stub), otherwise the documented production default
/// <see cref="AccountTelemetryClient.DefaultApiBaseUrl"/> - the SAME cloud base the Gateway already
/// targets for the rest of its account egress, so this client introduces no new hard-coded URL.
///
/// The access token is sent only as the Authorization header and is NEVER written to the log (security
/// rule DT-05): this client logs only the request shape and the response outcome, never the token. The
/// returned <see cref="CloudDeviceRecord"/> values are masked by the cloud and carry no token either.
///
/// It also registers THIS device with <c>POST /api/v1/devices/register</c> and advances its last-seen
/// with <c>POST /api/v1/devices/heartbeat</c> (cloud device-registry contract), the egress
/// behind the Gateway's "sign in = register this device" flow (issue #857).
///
/// The <see cref="HttpClient"/> is injectable so tests drive these calls against an in-process stub
/// handler (the proof seam for issues #854 / #857).
/// </summary>
public sealed class DeviceRegistryClient
{
    /// <summary>The path that lists the signed-in account's active devices.</summary>
    public const string DevicesPath = "/api/v1/devices";

    /// <summary>The path that registers (or re-registers, idempotent per install id) this device.</summary>
    public const string RegisterPath = "/api/v1/devices/register";

    /// <summary>The path that advances this device's last-seen timestamp.</summary>
    public const string HeartbeatPath = "/api/v1/devices/heartbeat";

    /// <summary>
    /// The path that confirms a presented per-device key belongs to the caller's own account
    /// (issue #908). Account-scoped: the cloud matches the key's hash against a non-revoked device
    /// under the caller's member, so it never confirms another account's key.
    /// </summary>
    public const string VerifyPath = "/api/v1/devices/verify";

    private readonly HttpClient _client;
    private readonly string _baseUrl;

    /// <summary>
    /// Creates the client. <paramref name="client"/> defaults to a short-timeout
    /// <see cref="HttpClient"/>; tests inject one over a stub handler. <paramref name="baseUrl"/>
    /// defaults to the <see cref="AccountTelemetryClient.ApiBaseUrlEnvVar"/> override when set, otherwise
    /// the production default - the same resolution the rest of the account egress uses.
    /// </summary>
    public DeviceRegistryClient(HttpClient? client = null, string? baseUrl = null)
    {
        _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _baseUrl = ResolveBaseUrl(baseUrl);
    }

    /// <summary>
    /// Resolves the API base URL: the explicit <paramref name="baseUrl"/> argument when given
    /// (trimmed, non-empty), otherwise the <see cref="AccountTelemetryClient.ApiBaseUrlEnvVar"/>
    /// environment override, otherwise the production default. The trailing slash is removed so the path
    /// concatenation never double-slashes.
    /// </summary>
    private static string ResolveBaseUrl(string? baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
            return baseUrl.Trim().TrimEnd('/');

        var fromEnv = Environment.GetEnvironmentVariable(AccountTelemetryClient.ApiBaseUrlEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim().TrimEnd('/');

        return AccountTelemetryClient.DefaultApiBaseUrl;
    }

    /// <summary>
    /// Lists the signed-in account's active devices via <c>GET /api/v1/devices</c>. Throws on a
    /// non-success response (so an unreachable or erroring cloud surfaces as a clear failure the caller
    /// reports, never a fabricated empty list) or a malformed body. The returned records are the cloud's
    /// masked shape; no token is ever returned or logged.
    /// </summary>
    /// <param name="accessToken">The Bearer access token the Gateway holds. Never logged.</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task<IReadOnlyList<CloudDeviceRecord>> ListDevicesAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token is required", nameof(accessToken));

        var endpoint = $"{_baseUrl}{DevicesPath}";
        FileLog.Write($"[DeviceRegistryClient] ListDevicesAsync: GET {endpoint}");

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        FileLog.Write($"[DeviceRegistryClient] ListDevicesAsync: response status={(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        // The cloud wraps the list under a top-level "data" envelope, so the array is
        // read from data, never the root.
        var data = DataArray(json, "devices");

        var devices = new List<CloudDeviceRecord>(data.Count);
        foreach (var item in data)
        {
            if (item is not JsonObject obj)
                throw new InvalidOperationException("devices response contained a non-object entry");
            devices.Add(ParseRecord(obj));
        }

        FileLog.Write($"[DeviceRegistryClient] ListDevicesAsync: parsed {devices.Count} device(s)");
        return devices;
    }

    /// <summary>
    /// Revokes one device via <c>DELETE /api/v1/devices/{id}</c>. Returns true when the cloud revoked it
    /// (200), false when the id is not the caller's device (404). Throws on any other non-success status
    /// (so an unreachable or erroring cloud surfaces as a clear failure, never a silent success).
    /// </summary>
    /// <param name="accessToken">The Bearer access token the Gateway holds. Never logged.</param>
    /// <param name="id">The device id to revoke.</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task<bool> RevokeDeviceAsync(string accessToken, string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token is required", nameof(accessToken));
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Device id is required", nameof(id));

        var endpoint = $"{_baseUrl}{DevicesPath}/{Uri.EscapeDataString(id)}";
        FileLog.Write($"[DeviceRegistryClient] RevokeDeviceAsync: DELETE {endpoint}");

        using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        FileLog.Write($"[DeviceRegistryClient] RevokeDeviceAsync: id={id}, response status={(int)response.StatusCode}");

        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;

        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// Registers (or re-registers) THIS device with <c>POST /api/v1/devices/register</c> and returns the
    /// per-device key the cloud issues once, plus the masked device record. The cloud is idempotent per
    /// (member, install id): re-registering the same <see cref="CloudDeviceRegistrationRequest.InstallId"/>
    /// rotates the key and updates the record rather than creating a duplicate device (issue #857), so the
    /// caller must always send the same stable install id. Throws on a non-success response or a malformed
    /// body (so an unreachable or erroring cloud surfaces as a clear failure the caller handles, never a
    /// fabricated success). The plain key in the result is never written to the log (security rule DT-05):
    /// this method logs only the request shape and the registered device id.
    /// </summary>
    /// <param name="accessToken">The Bearer access token the Gateway holds. Never logged.</param>
    /// <param name="request">The registration request (install id, platform, optional name/type/version).</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task<CloudDeviceRegistrationResult> RegisterAsync(string accessToken, CloudDeviceRegistrationRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token is required", nameof(accessToken));
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.InstallId))
            throw new ArgumentException("Install id is required", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Platform))
            throw new ArgumentException("Platform is required", nameof(request));

        var endpoint = $"{_baseUrl}{RegisterPath}";
        FileLog.Write($"[DeviceRegistryClient] RegisterAsync: POST {endpoint}, install_id={request.InstallId}, platform={request.Platform}");

        var body = new JsonObject
        {
            ["install_id"] = request.InstallId,
            ["platform"] = request.Platform,
        };
        if (!string.IsNullOrWhiteSpace(request.Name))
            body["name"] = request.Name;
        if (!string.IsNullOrWhiteSpace(request.DeviceType))
            body["device_type"] = request.DeviceType;
        if (!string.IsNullOrWhiteSpace(request.AppVersion))
            body["app_version"] = request.AppVersion;
        // Issue #1206: a Gateway publishes its own reachable front-door URL so an installer on another
        // machine can discover it. Sent only when present, so a device with no address never sends the field.
        if (!string.IsNullOrWhiteSpace(request.EndpointUrl))
            body["endpoint_url"] = request.EndpointUrl;
        // Issue #1233: a Gateway also publishes the WHOLE ordered list of the ways it can be reached (machine
        // name, then Tailscale when present, then local network IP) so a joining machine tries them in order.
        // Sent only when non-empty, so a device with no addresses never sends the field. The single
        // endpoint_url above stays the first entry (non-breaking for readers of the single field).
        var endpointUrlsArray = BuildEndpointUrlsArray(request.EndpointUrls);
        if (endpointUrlsArray is not null)
            body["endpoint_urls"] = endpointUrlsArray;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _client.SendAsync(httpRequest, ct).ConfigureAwait(false);
        FileLog.Write($"[DeviceRegistryClient] RegisterAsync: response status={(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        // The cloud wraps the issued key and the masked record under a top-level "data" envelope,
        // so both are read from data, never the root.
        var data = DataObject(json, "device register");

        var deviceKey = StringField(data, "device_key")
            ?? throw new InvalidOperationException("device register response had no string 'data.device_key'");
        var record = data["record"] as JsonObject
            ?? throw new InvalidOperationException("device register response had no object 'data.record'");
        var device = ParseRecord(record);

        // DT-05: log the registered device id only - NEVER the issued key.
        FileLog.Write($"[DeviceRegistryClient] RegisterAsync: registered device id={device.Id} (per-device key received, not logged)");
        return new CloudDeviceRegistrationResult(deviceKey, device);
    }

    /// <summary>
    /// Advances this device's last-seen with <c>POST /api/v1/devices/heartbeat</c>. Returns true when the
    /// cloud advanced last-seen (200), false when it does not know this install id (404) - the signal that
    /// the device must be (re-)registered. Throws on any other non-success status (so an unreachable or
    /// erroring cloud surfaces as a clear failure the best-effort caller logs, never a silent success).
    ///
    /// Issue #1206: an optional <paramref name="endpointUrl"/> is sent so an already-installed Gateway (which
    /// registers only once per install, then only heartbeats) keeps its published front-door address current
    /// and backfills it after updating to this version. The cloud never CLEARS a hand-set value when the
    /// field is omitted, so a device that has no address simply sends nothing.
    /// </summary>
    /// <param name="accessToken">The Bearer access token the Gateway holds. Never logged.</param>
    /// <param name="installId">This device's stable install id (identifies the row to advance). Required.</param>
    /// <param name="appVersion">The reporting app version, or null when omitted.</param>
    /// <param name="endpointUrl">This device's reachable front-door URL to publish, or null to omit it.</param>
    /// <param name="endpointUrls">This device's reachable front-door URLs in priority order to publish (issue
    /// #1233), or null/empty to omit them. When present, the first entry equals <paramref name="endpointUrl"/>.</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task<bool> HeartbeatAsync(string accessToken, string installId, string? appVersion = null, string? endpointUrl = null, IReadOnlyList<string>? endpointUrls = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token is required", nameof(accessToken));
        if (string.IsNullOrWhiteSpace(installId))
            throw new ArgumentException("Install id is required", nameof(installId));

        var endpoint = $"{_baseUrl}{HeartbeatPath}";
        FileLog.Write($"[DeviceRegistryClient] HeartbeatAsync: POST {endpoint}, install_id={installId}");

        var body = new JsonObject { ["install_id"] = installId };
        if (!string.IsNullOrWhiteSpace(appVersion))
            body["app_version"] = appVersion;
        if (!string.IsNullOrWhiteSpace(endpointUrl))
            body["endpoint_url"] = endpointUrl;
        // Issue #1233: keep the whole ordered address list current on every heartbeat (the single
        // endpoint_url above stays the first entry). Omitted when empty, so the cloud never clears a value.
        var endpointUrlsArray = BuildEndpointUrlsArray(endpointUrls);
        if (endpointUrlsArray is not null)
            body["endpoint_urls"] = endpointUrlsArray;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        FileLog.Write($"[DeviceRegistryClient] HeartbeatAsync: install_id={installId}, response status={(int)response.StatusCode}");

        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;

        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// Confirms a presented per-device key belongs to the signed-in account via
    /// <c>POST /api/v1/devices/verify</c>, returning the cloud device id when it does and null when it
    /// does not (issue #908). This is how a Gateway admits a phone that enrolled on devthrottle.com and
    /// received its per-device key, WITHOUT ever handling the account session: the phone hands the
    /// Gateway only its device key, and the Gateway confirms - account-scoped, by key hash - that the key
    /// is a live device on the Gateway's OWN account before issuing a local key. The verify is
    /// account-scoped by the caller's Bearer, so a key belonging to a different account returns null (not
    /// a match), and a masked-roster prefix/last-four compare is deliberately NOT used (too few bits to
    /// resist a guess). Throws on a non-success response or a malformed body (an unreachable or erroring
    /// cloud surfaces as a clear failure the caller handles, never a fabricated match). The device key is
    /// sent only in the request body and is NEVER written to the log (security rule DT-05).
    /// </summary>
    /// <param name="accessToken">The Bearer access token the Gateway holds. Never logged.</param>
    /// <param name="deviceKey">The presented per-device key to confirm. Never logged.</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task<string?> VerifyDeviceKeyAsync(string accessToken, string deviceKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token is required", nameof(accessToken));
        if (string.IsNullOrWhiteSpace(deviceKey))
            throw new ArgumentException("Device key is required", nameof(deviceKey));

        var endpoint = $"{_baseUrl}{VerifyPath}";
        FileLog.Write($"[DeviceRegistryClient] VerifyDeviceKeyAsync: POST {endpoint} (device key not logged)");

        var body = new JsonObject { ["device_key"] = deviceKey };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        FileLog.Write($"[DeviceRegistryClient] VerifyDeviceKeyAsync: response status={(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var data = DataObject(json, "device verify");

        var valid = data.TryGetPropertyValue("valid", out var validNode)
            && validNode is JsonValue validValue
            && validValue.TryGetValue<bool>(out var b) && b;
        if (!valid)
        {
            FileLog.Write("[DeviceRegistryClient] VerifyDeviceKeyAsync: key is not a live device on this account -> null");
            return null;
        }

        var id = StringField(data, "id")
            ?? throw new InvalidOperationException("device verify response had valid=true but no string 'data.id'");
        FileLog.Write($"[DeviceRegistryClient] VerifyDeviceKeyAsync: verified device id={id}");
        return id;
    }

    /// <summary>
    /// Unwraps the cloud's <c>{ "data": { ... } }</c> envelope and returns the inner object. Every device
    /// endpoint in the cloud contract wraps
    /// its success payload under a single top-level "data" key, so callers parse from this inner object,
    /// never the raw root. Throws when the body is not a JSON object or carries no "data" object (so a
    /// malformed or contract-violating response surfaces as a clear failure, never a silent misparse).
    /// </summary>
    private static JsonObject DataObject(string json, string what)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException($"{what} response was not a JSON object");
        return root["data"] as JsonObject
            ?? throw new InvalidOperationException($"{what} response had no object 'data' envelope");
    }

    /// <summary>
    /// Unwraps the cloud's <c>{ "data": [ ... ] }</c> envelope and returns the inner array. The list
    /// endpoint returns its records under the same top-level "data" key as the
    /// object-returning endpoints. Throws when the body is not a JSON object or carries no "data" array.
    /// </summary>
    private static JsonArray DataArray(string json, string what)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException($"{what} response was not a JSON object");
        return root["data"] as JsonArray
            ?? throw new InvalidOperationException($"{what} response had no array 'data' envelope");
    }

    /// <summary>
    /// Parses one masked device record from the cloud response object. The id and name are required; the
    /// remaining display fields are optional and read as null when absent. No token field exists in this
    /// shape, so none can be parsed (security rule DT-05).
    /// </summary>
    private static CloudDeviceRecord ParseRecord(JsonObject obj)
    {
        var id = StringField(obj, "id")
            ?? throw new InvalidOperationException("device record had no string 'id'");
        var name = StringField(obj, "name")
            ?? throw new InvalidOperationException($"device record '{id}' had no string 'name'");

        return new CloudDeviceRecord(
            id,
            name,
            StringField(obj, "platform"),
            StringField(obj, "device_type"),
            StringField(obj, "app_version"),
            StringField(obj, "key_prefix"),
            StringField(obj, "key_last4"),
            StringField(obj, "created_at"),
            StringField(obj, "last_seen_at"),
            StringField(obj, "endpoint_url"),
            StringArrayField(obj, "endpoint_urls"));
    }

    /// <summary>
    /// Builds the JSON array to send for <c>endpoint_urls</c> (issue #1233), skipping null/blank entries so
    /// only real addresses are published. Returns null when the source is null or holds no usable address, so
    /// the caller omits the field entirely rather than sending an empty array.
    /// </summary>
    private static JsonArray? BuildEndpointUrlsArray(IReadOnlyList<string>? urls)
    {
        if (urls is null || urls.Count == 0)
            return null;

        var array = new JsonArray();
        foreach (var url in urls)
            if (!string.IsNullOrWhiteSpace(url))
                array.Add((JsonNode)url);

        return array.Count == 0 ? null : array;
    }

    /// <summary>
    /// Reads a string-array field from the object as an ordered list (issue #1233), skipping any non-string
    /// or blank entry. Returns null when the field is absent or not an array, and null when it holds no
    /// usable string, so a caller reads "no addresses" as null (never an empty list, never a throw).
    /// </summary>
    private static IReadOnlyList<string>? StringArrayField(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node is not JsonArray array)
            return null;

        var values = new List<string>(array.Count);
        foreach (var item in array)
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                values.Add(text);

        return values.Count == 0 ? null : values;
    }

    /// <summary>Reads a string field from the object, or null when absent or not a string value.</summary>
    private static string? StringField(JsonObject obj, string name)
    {
        if (obj.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        return null;
    }
}
