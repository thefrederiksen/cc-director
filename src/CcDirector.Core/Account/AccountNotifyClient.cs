using CcDirector.Core.Network;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>One attachment for an owner email (issue #1318 consumer). <see cref="ContentBase64"/> is the
/// file's bytes base64-encoded; the cloud decodes it and hands it to Resend.</summary>
/// <param name="Filename">The attachment's file name as it appears in the email.</param>
/// <param name="ContentBase64">The file bytes, base64-encoded.</param>
/// <param name="ContentType">Optional MIME type (e.g. text/html); null lets the provider infer it.</param>
public sealed record NotifyAttachment(string Filename, string ContentBase64, string? ContentType);

/// <summary>The outcome of an owner-email send through the cloud (issue #1318 consumer).</summary>
/// <param name="Sent">True when the cloud accepted and sent the email.</param>
/// <param name="ProviderId">The provider (Resend) message id when the cloud returned one.</param>
/// <param name="Error">A clear, fixable failure reason when <see cref="Sent"/> is false; else null.</param>
/// <param name="StatusCode">The cloud HTTP status (0 when the request never got a response).</param>
public sealed record CloudNotifyResult(bool Sent, string? ProviderId, string? Error, int StatusCode);

/// <summary>
/// A small HTTP client for the DevThrottle "email the account owner" primitive (issue #1318 consumer,
/// cloud contract <c>POST /api/v1/account/notify-owner</c> shipped in devthrottle_internal #338). It sends
/// the signed-in account owner an email (subject + text/html + optional attachments), authenticated with
/// the Bearer access token the Gateway already holds for cloud egress (the SAME credential
/// <see cref="DevThrottleAccountService.GetAccessTokenForForwarding"/> returns). The recipient is resolved
/// SERVER-SIDE from the token - this client never sends a recipient, so it is single-recipient by
/// construction, matching the endpoint.
///
/// The base URL resolves the same way the rest of the account egress does
/// (<see cref="AccountTelemetryClient.ApiBaseUrlEnvVar"/> override, else the production default), so this
/// client introduces no new hard-coded host. The access token is sent only as the Authorization header and
/// is NEVER written to the log (DT-05); the subject/body/attachment content is never logged either. The
/// <see cref="HttpClient"/> is injectable so tests drive it against an in-process stub.
/// </summary>
public sealed class AccountNotifyClient
{
    /// <summary>The path that emails the signed-in account owner (single recipient, resolved server-side).</summary>
    public const string NotifyOwnerPath = "/api/v1/account/notify-owner";

    private readonly HttpClient _client;
    private readonly string _baseUrl;

    /// <param name="client">HTTP client (tests inject a stub); defaults to a short-timeout client.</param>
    /// <param name="baseUrl">API base URL; defaults to the shared account-egress base resolution.</param>
    public AccountNotifyClient(HttpClient? client = null, string? baseUrl = null)
    {
        _client = client ?? new HttpClient(GatewayHttp.Handler()) { Timeout = TimeSpan.FromSeconds(30) };
        _baseUrl = ResolveBaseUrl(baseUrl);
    }

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
    /// Emails the signed-in account owner via <c>POST /api/v1/account/notify-owner</c>. Returns a result:
    /// <see cref="CloudNotifyResult.Sent"/> true on a 2xx, otherwise a clear error carrying the cloud's
    /// status + message (never a fabricated success - no-fallback rule). Throws only for a transport
    /// failure the caller reports as unreachable. No token, subject, body, or attachment content is logged.
    /// </summary>
    /// <param name="accessToken">The Bearer access token the Gateway holds. Never logged.</param>
    public async Task<CloudNotifyResult> SendOwnerAsync(
        string accessToken, string subject, string? text, string? html,
        IReadOnlyList<NotifyAttachment>? attachments, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token is required", nameof(accessToken));

        var endpoint = $"{_baseUrl}{NotifyOwnerPath}";
        var json = BuildBody(subject, text, html, attachments);
        FileLog.Write($"[AccountNotifyClient] SendOwnerAsync: POST {endpoint} (attachments={attachments?.Count ?? 0})");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        FileLog.Write($"[AccountNotifyClient] SendOwnerAsync: response status={(int)response.StatusCode}");

        if (response.IsSuccessStatusCode)
            return ParseSuccess(body);

        return new CloudNotifyResult(false, null, ParseError(body, (int)response.StatusCode), (int)response.StatusCode);
    }

    /// <summary>Build the cloud request body. Internal for unit testing the wire shape (recipient never appears).</summary>
    internal static string BuildBody(string subject, string? text, string? html, IReadOnlyList<NotifyAttachment>? attachments)
    {
        var payload = new JsonObject { ["subject"] = subject };
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

    /// <summary>Parse the cloud <c>{ "data": { "sent": true, "id": "..." } }</c> success envelope.</summary>
    internal static CloudNotifyResult ParseSuccess(string json)
    {
        var data = (JsonNode.Parse(json) as JsonObject)?["data"] as JsonObject;
        var providerId = (data?["id"] as JsonValue)?.GetValue<string>();
        return new CloudNotifyResult(true, providerId, null, 200);
    }

    /// <summary>Extract a human-readable message from the cloud <c>{ "error": { "message": ... } }</c> body.</summary>
    internal static string ParseError(string json, int statusCode)
    {
        try
        {
            var error = (JsonNode.Parse(json) as JsonObject)?["error"] as JsonObject;
            var message = (error?["message"] as JsonValue)?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(message))
                return message!;
        }
        catch (JsonException)
        {
            // fall through to a generic message below
        }
        return $"the DevThrottle account service returned {statusCode}.";
    }
}
