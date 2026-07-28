using CcDirector.Core.Network;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The outcome of a TENANT-ADDRESSED owner-email send (devthrottle_internal #986).
/// </summary>
/// <param name="Sent">True only when the cloud accepted and sent. Never inferred from a status alone.</param>
/// <param name="ProviderId">The provider message id when the cloud returned one; may be null even on a send.</param>
/// <param name="Error">The cloud's own human-readable message, to be surfaced VERBATIM. Null on success.</param>
/// <param name="StatusCode">The cloud HTTP status (0 when the request never got a response).</param>
/// <param name="ErrorCode">
/// The cloud's machine-readable code (<c>tenant_not_found</c>, <c>owner_not_resolvable</c>,
/// <c>subject_mismatch</c>, <c>notify_rate_limited</c>, <c>tenant_lookup_not_installed</c>, ...). Used for
/// LOGGING and for the retry rule only - never for composing a user-facing sentence, which is what
/// <see cref="Error"/> already is.
/// </param>
/// <param name="RetryAfterSeconds">
/// The cloud's <c>Retry-After</c> in seconds on a 429. Authoritative: the caller waits at least this long and
/// applies no backoff of its own, because the cloud computes it from when the oldest hit in the window
/// actually expires.
/// </param>
public sealed record TenantNotifyResult(
    bool Sent, string? ProviderId, string? Error, int StatusCode, string? ErrorCode, int? RetryAfterSeconds);

/// <summary>
/// The Gateway's client for the TENANT-ADDRESSED owner-email primitive
/// (<c>POST /api/v1/account/notify-owner-by-tenant</c>, devthrottle_internal #986). It exists because the
/// hosted Gateway holds NO account access token for its tenants and must never start holding one: hosted
/// enrollment validates the account token, mints a tenant and a device key, and stores neither. So the
/// existing JWT-authenticated <see cref="AccountNotifyClient"/> cannot be used on hosted at all - that gap is
/// what made <c>POST /account/email</c> report a signed-in user as signed out (issue #984).
///
/// THE SAFETY PROPERTY THIS TYPE IS BUILT AROUND: <b>this client cannot address anyone.</b> There is no
/// recipient parameter, no recipient field on the wire, and no code path that could add one. The Gateway
/// names a TENANT; the cloud resolves that tenant to its account subject and then to the owner's address
/// through the same authority the JWT route uses. A recipient field in the body is refused by the cloud with
/// a hard 400 rather than ignored, so neither side can quietly believe it addressed someone. That is belt
/// and braces on purpose - the braces are here (no field can be constructed), the belt is there (a field
/// would be rejected).
///
/// <see cref="AccountSubject"/> is sent as a CROSS-CHECK ONLY. The cloud compares it byte-for-byte against
/// the subject stored on that tenant row and answers 403 <c>subject_mismatch</c> when it differs. It is never
/// used to RESOLVE the recipient, and it must not be: a subject that selects the target is a subject that
/// could select somebody else's. Sending it turns a wrong tenant id into a refusal instead of a
/// correctly-delivered message to the wrong person.
///
/// Auth is a single shared secret in the <c>X-DevThrottle-Gateway-Token</c> header and NO Authorization
/// header - presenting both is refused with a 400, because a route whose auth mode depends on which header
/// happened to arrive is a route that will eventually pick the wrong one. The secret is read from
/// <see cref="ServiceTokenEnvVar"/> and is NEVER logged, echoed, or placed in any response (DT-05).
///
/// The base URL resolves the same way the rest of the account egress does
/// (<see cref="DevThrottleApi.BaseUrlEnvVar"/> override, else the production default), so this introduces no
/// new hard-coded host and a preview deployment can be targeted by environment variable without a rebuild.
/// </summary>
public sealed class AccountNotifyByTenantClient
{
    /// <summary>The tenant-addressed send path (devthrottle_internal #986). Separate from the JWT route,
    /// which is unchanged and still serves the self-host path.</summary>
    public const string NotifyOwnerByTenantPath = "/api/v1/account/notify-owner-by-tenant";

    /// <summary>The header carrying the Gateway service credential. Deliberately NOT <c>Authorization</c>.</summary>
    public const string ServiceTokenHeader = "X-DevThrottle-Gateway-Token";

    /// <summary>
    /// The app setting holding the shared service secret. Its ABSENCE is meaningful: the Gateway must fail
    /// closed rather than call unauthenticated, so that a 401 from the cloud always means the secret is
    /// WRONG and never that it was missing. That distinction is what makes a misconfiguration diagnosable
    /// from one end.
    /// </summary>
    public const string ServiceTokenEnvVar = "NOTIFY_OWNER_SERVICE_TOKEN";

    private readonly HttpClient _client;
    private readonly string _baseUrl;

    public AccountNotifyByTenantClient(HttpClient? client = null, string? baseUrl = null)
    {
        _client = client ?? new HttpClient(GatewayHttp.Handler()) { Timeout = TimeSpan.FromSeconds(30) };
        _baseUrl = DevThrottleApi.ResolveBaseUrl(baseUrl);
    }

    /// <summary>The configured service secret, or null when unset. Never logged.</summary>
    public static string? ResolveServiceToken()
    {
        var token = Environment.GetEnvironmentVariable(ServiceTokenEnvVar);
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    /// <summary>
    /// Sends the owner of <paramref name="tenantId"/> an email, with the recipient resolved entirely
    /// cloud-side. Returns a result: <see cref="TenantNotifyResult.Sent"/> is true only on an explicit
    /// success envelope, never inferred from a status code. Throws only on a transport failure, which the
    /// caller reports as unreachable. No token, subject, body, or attachment content is logged.
    /// </summary>
    /// <param name="serviceToken">The shared service secret. Never logged.</param>
    /// <param name="tenantId">The caller tenant's id, exactly as stored. Compared byte-for-byte cloud-side.</param>
    /// <param name="accountSubject">The subject on that tenant row, sent as a cross-check. Optional.</param>
    public async Task<TenantNotifyResult> SendOwnerForTenantAsync(
        string serviceToken, string tenantId, string? accountSubject,
        string subject, string? text, string? html,
        IReadOnlyList<NotifyAttachment>? attachments, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serviceToken))
            throw new ArgumentException("A Gateway service token is required", nameof(serviceToken));
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("A tenant id is required", nameof(tenantId));

        var endpoint = $"{_baseUrl}{NotifyOwnerByTenantPath}";
        var json = BuildBody(tenantId, accountSubject, subject, text, html, attachments);
        // The tenant id and account subject are account-identifying and are NOT logged; only the shape is.
        FileLog.Write($"[AccountNotifyByTenantClient] SendOwnerForTenantAsync: POST {endpoint} (attachments={attachments?.Count ?? 0}, crossCheck={(accountSubject is null ? "absent" : "present")})");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        // The service credential goes in its OWN header. No Authorization header is set here, ever -
        // presenting both modes is a 400 by contract, and this client must not be the thing that does it.
        request.Headers.Add(ServiceTokenHeader, serviceToken);

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            var success = ParseSuccess(body, status);
            FileLog.Write($"[AccountNotifyByTenantClient] SendOwnerForTenantAsync: status={status}, sent={success.Sent}");
            return success;
        }

        int? retryAfter = null;
        if (response.Headers.RetryAfter?.Delta is { } delta)
            retryAfter = (int)Math.Ceiling(delta.TotalSeconds);
        else if (response.Headers.TryGetValues("Retry-After", out var values)
                 && int.TryParse(values.FirstOrDefault(), out var seconds))
            retryAfter = seconds;

        var (message, code) = ParseError(body, status);
        FileLog.Write($"[AccountNotifyByTenantClient] SendOwnerForTenantAsync: NOT sent, status={status}, code={code ?? "<none>"}, retryAfter={(retryAfter is null ? "<none>" : retryAfter.ToString())}");
        return new TenantNotifyResult(false, null, message, status, code, retryAfter);
    }

    /// <summary>
    /// Builds the request body. Internal so a test can assert the wire shape - specifically that NO
    /// recipient-shaped key can appear, which is the property the whole design rests on.
    /// </summary>
    internal static string BuildBody(
        string tenantId, string? accountSubject, string subject, string? text, string? html,
        IReadOnlyList<NotifyAttachment>? attachments)
    {
        var payload = new JsonObject
        {
            ["tenant_id"] = tenantId,
            ["subject"] = subject,
        };
        // Cross-check only. Omitted rather than sent empty when unresolvable - a blank subject would be
        // compared byte-for-byte and refused, turning a missing cross-check into a failed send.
        if (!string.IsNullOrWhiteSpace(accountSubject))
            payload["account_subject"] = accountSubject;
        if (!string.IsNullOrEmpty(text)) payload["text"] = text;
        if (!string.IsNullOrEmpty(html)) payload["html"] = html;
        if (attachments is { Count: > 0 })
        {
            var arr = new JsonArray();
            foreach (var a in attachments)
            {
                var obj = new JsonObject { ["filename"] = a.Filename, ["content"] = a.ContentBase64 };
                if (!string.IsNullOrEmpty(a.ContentType)) obj["contentType"] = a.ContentType;
                arr.Add(obj);
            }
            payload["attachments"] = arr;
        }
        return payload.ToJsonString();
    }

    /// <summary>
    /// Parses the <c>{ "data": { "sent": true, "id": ... } }</c> success envelope. A 2xx whose body does NOT
    /// say sent is reported as NOT sent - a status code is not a delivery receipt, and reporting a send that
    /// did not happen is the one failure this whole change exists to prevent.
    /// </summary>
    internal static TenantNotifyResult ParseSuccess(string json, int status)
    {
        JsonObject? data = null;
        try { data = (JsonNode.Parse(json) as JsonObject)?["data"] as JsonObject; }
        catch (JsonException) { /* handled as not-sent below */ }

        var sent = (data?["sent"] as JsonValue)?.TryGetValue<bool>(out var s) == true && s;
        if (!sent)
        {
            return new TenantNotifyResult(false, null,
                "The DevThrottle account service answered without confirming the email was sent.",
                status, null, null);
        }

        var providerId = (data?["id"] as JsonValue)?.TryGetValue<string>(out var id) == true ? id : null;
        return new TenantNotifyResult(true, providerId, null, status, null, null);
    }

    /// <summary>
    /// Extracts the human message and machine code from the cloud's
    /// <c>{ "error": { "type", "code", "message" } }</c> envelope. The message is written to be read by a
    /// human and is surfaced VERBATIM; only the code is for us.
    /// </summary>
    internal static (string Message, string? Code) ParseError(string json, int statusCode)
    {
        try
        {
            var error = (JsonNode.Parse(json) as JsonObject)?["error"] as JsonObject;
            var message = (error?["message"] as JsonValue)?.TryGetValue<string>(out var m) == true ? m : null;
            var code = (error?["code"] as JsonValue)?.TryGetValue<string>(out var c) == true ? c : null;
            if (!string.IsNullOrWhiteSpace(message))
                return (message!, code);
            if (!string.IsNullOrWhiteSpace(code))
                return ($"the DevThrottle account service refused the send ({code}).", code);
        }
        catch (JsonException)
        {
            // fall through to the generic message below
        }
        return ($"the DevThrottle account service returned {statusCode}.", null);
    }
}
