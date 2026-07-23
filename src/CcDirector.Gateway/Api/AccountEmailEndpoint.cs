using System.Text.Json.Serialization;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Gateway relay for "DevThrottle emails me" (issue #1318 consumer). A session or scheduled run on any
/// Director calls <c>POST /account/email</c> with a subject + body (+ optional attachments); the Gateway
/// injects the account access token it already holds (<see cref="DevThrottleAccountService.GetAccessTokenForForwarding"/>,
/// the SAME egress credential it uses for other account operations) and forwards to the cloud primitive
/// (<c>POST /api/v1/account/notify-owner</c>, devthrottle_internal #338) via <see cref="AccountNotifyClient"/>.
///
/// The Gateway holds NO Resend key and runs NO email code - the send happens entirely in the cloud, which
/// resolves the recipient from the token. The request has no recipient field, so it is single-recipient by
/// construction end to end. Wire contract:
///   POST /account/email  { "subject": string, "bodyText"?: string, "bodyHtml"?: string,
///                          "attachments"?: [ { "filename": string, "content": base64, "contentType"?: string } ] }
///        200 -> { "sent": true, "providerId"?: string }
///        4xx -> { "sent": false, "error": string }   (bad input, forwarded from the cloud)
///        401 -> { "sent": false, "error": string }   (Gateway not signed in - no account to send from)
///        502 -> { "sent": false, "error": string }   (cloud unreachable / send failure - never a fake success)
///
/// Inherits the host-wide Gateway token middleware like the other <c>/account/*</c> endpoints (not on the
/// public-paths allow-list). The account token is never returned or logged (DT-05).
/// </summary>
internal static class AccountEmailEndpoint
{
    public static void Map(IEndpointRouteBuilder app, DevThrottleAccountService? account, AccountNotifyClient notify)
    {
        if (notify is null) throw new ArgumentNullException(nameof(notify));

        app.MapPost("/account/email", async (AccountEmailRequest? request, HttpContext http) =>
        {
            // Entry point: the delegate is the boundary, so the only try-catch lives here.
            if (request is null || string.IsNullOrWhiteSpace(request.Subject))
                return Results.BadRequest(new AccountEmailResponse(false, "subject is required.", null));

            var token = account?.GetAccessTokenForForwarding();
            if (string.IsNullOrEmpty(token))
            {
                FileLog.Write("[AccountEmailEndpoint] POST /account/email: no account credential -> not signed in");
                return Results.Json(
                    new AccountEmailResponse(false,
                        "not signed in to DevThrottle - there is no account to email from. Sign in from the Gateway tray, then retry.",
                        null),
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            try
            {
                var attachments = request.Attachments?
                    .Select(a => new NotifyAttachment(a.Filename ?? "", a.Content ?? "", a.ContentType))
                    .ToList();

                var result = await notify
                    .SendOwnerAsync(token, request.Subject!, request.BodyText, request.BodyHtml, attachments, http.RequestAborted)
                    .ConfigureAwait(false);

                if (!result.Sent)
                {
                    // A 4xx from the cloud is a caller input problem (forward it as a 400); anything else is
                    // an upstream/send failure (502). Never a fabricated success.
                    var status = result.StatusCode is >= 400 and < 500
                        ? StatusCodes.Status400BadRequest
                        : StatusCodes.Status502BadGateway;
                    FileLog.Write($"[AccountEmailEndpoint] POST /account/email: not sent (cloud status {result.StatusCode})");
                    return Results.Json(new AccountEmailResponse(false, result.Error, null), statusCode: status);
                }

                FileLog.Write("[AccountEmailEndpoint] POST /account/email: sent to owner.");
                return Results.Json(new AccountEmailResponse(true, null, result.ProviderId));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[AccountEmailEndpoint] POST /account/email FAILED: {ex.Message}");
                return Results.Json(
                    new AccountEmailResponse(false,
                        "Could not reach the DevThrottle account service to send the email. Try again shortly.", null),
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }

    /// <summary>
    /// The relay request body. Subject + body + optional attachments - deliberately NO recipient field, so
    /// the send can only ever reach the account owner (single-recipient by construction, issue #1318).
    /// </summary>
    internal sealed class AccountEmailRequest
    {
        [JsonPropertyName("subject")] public string? Subject { get; set; }
        [JsonPropertyName("bodyText")] public string? BodyText { get; set; }
        [JsonPropertyName("bodyHtml")] public string? BodyHtml { get; set; }
        [JsonPropertyName("attachments")] public List<AccountEmailAttachment>? Attachments { get; set; }
    }

    /// <summary>One attachment: a file name plus its base64-encoded bytes and an optional MIME type.</summary>
    internal sealed class AccountEmailAttachment
    {
        [JsonPropertyName("filename")] public string? Filename { get; set; }
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("contentType")] public string? ContentType { get; set; }
    }

    private sealed record AccountEmailResponse(
        [property: JsonPropertyName("sent")] bool Sent,
        [property: JsonPropertyName("error")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Error,
        [property: JsonPropertyName("providerId")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ProviderId);
}
