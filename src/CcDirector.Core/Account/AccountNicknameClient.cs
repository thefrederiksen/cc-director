using CcDirector.Core.Network;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// A small HTTP client for the signed-in account's chosen nickname (issue #1357, cloud contract
/// <c>GET /api/v1/account/nickname</c>; the same value is also returned on <c>/auth/me</c>). The
/// nickname lives server-side in the website/account repo (<c>members.nickname</c>); this client reads
/// it so a session's preamble can name the human by the nickname they chose.
///
/// It mirrors <see cref="AccountCreditsClient"/>: it authenticates with the Bearer access token the
/// Gateway already holds for cloud egress (the SAME credential
/// <see cref="DevThrottleAccountService.GetAccessTokenForForwarding"/> returns - the nickname endpoint
/// is JWT-authed, NOT dt_-key-authed), the base URL resolves the same way the rest of the account egress
/// does (<see cref="AccountTelemetryClient.ApiBaseUrlEnvVar"/> override, else the production default), so
/// this client introduces no new hard-coded host, and the access token is sent only as the
/// Authorization header and is NEVER written to the log (DT-05). The <see cref="HttpClient"/> is
/// injectable so tests drive it against an in-process stub.
///
/// When the account has no nickname set, the endpoint returns it as null/absent and this client returns
/// null - the caller (the preamble) then falls back to the email, which is the documented behaviour.
/// </summary>
public sealed class AccountNicknameClient
{
    /// <summary>The path that returns the signed-in account's chosen nickname.</summary>
    public const string NicknamePath = "/api/v1/account/nickname";

    private readonly HttpClient _client;
    private readonly string _baseUrl;

    /// <param name="client">HTTP client (tests inject a stub); defaults to a short-timeout client.</param>
    /// <param name="baseUrl">API base URL; defaults to the shared account-egress base resolution.</param>
    public AccountNicknameClient(HttpClient? client = null, string? baseUrl = null)
    {
        _client = client ?? new HttpClient(GatewayHttp.Handler()) { Timeout = TimeSpan.FromSeconds(10) };
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
    /// Reads the account's nickname via <c>GET /api/v1/account/nickname</c>. Returns the trimmed nickname
    /// when the account has one set, or null when the account has no nickname (the caller falls back to
    /// the email). Throws on a non-success response (so an unreachable or erroring cloud surfaces as a
    /// clear failure the caller reports, never a fabricated value) or a malformed body. No token is ever
    /// returned or logged.
    /// </summary>
    /// <param name="accessToken">The Bearer access token the Gateway holds. Never logged.</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task<string?> GetNicknameAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token is required", nameof(accessToken));

        var endpoint = $"{_baseUrl}{NicknamePath}";
        FileLog.Write($"[AccountNicknameClient] GetNicknameAsync: GET {endpoint}");

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        FileLog.Write($"[AccountNicknameClient] GetNicknameAsync: response status={(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var nickname = Parse(json);
        FileLog.Write($"[AccountNicknameClient] GetNicknameAsync: nickname={(nickname is null ? "<unset>" : "resolved")}");
        return nickname;
    }

    /// <summary>
    /// Parses the nickname out of the cloud response. Accepts the <c>{ "data": { "nickname": "..." } }</c>
    /// envelope the account API uses for its other reads (see <see cref="AccountCreditsClient"/>) AND a
    /// bare top-level <c>{ "nickname": "..." }</c> shape (as returned inline on <c>/auth/me</c>). Returns
    /// the trimmed nickname when present and non-empty, or null when the field is absent, null, or blank -
    /// an unset nickname is a normal state, not an error. Internal so the parse is unit-testable without a
    /// network. Throws only when the body is not a JSON object at all.
    /// </summary>
    internal static string? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("account nickname response was not a JSON object");

        // Prefer the { data: { nickname } } envelope; fall back to a top-level { nickname }.
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            var fromEnvelope = ReadNickname(data);
            if (fromEnvelope is not null)
                return fromEnvelope;
        }

        return ReadNickname(root);
    }

    /// <summary>Reads a non-empty, trimmed <c>nickname</c> string from an object, or null when absent/blank.</summary>
    private static string? ReadNickname(JsonElement obj)
    {
        if (obj.TryGetProperty("nickname", out var nick) && nick.ValueKind == JsonValueKind.String)
        {
            var value = nick.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return null;
    }
}
