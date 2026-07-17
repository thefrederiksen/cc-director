using CcDirector.Core.Network;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>One credit-ledger entry as the cloud returns it (issue #884). A debit's
/// <see cref="AmountMicros"/> is negative; a top-up is positive.</summary>
/// <param name="Kind">"debit" or "credit".</param>
/// <param name="AmountMicros">The signed amount in micro-dollars (a debit is negative).</param>
/// <param name="CreatedAt">When the entry was recorded, or null when the cloud omits it.</param>
public sealed record CloudCreditTransaction(string Kind, long AmountMicros, string? CreatedAt);

/// <summary>The signed-in account's credit balance plus the most recent ledger entries (issue #884).</summary>
/// <param name="BalanceMicros">The current balance in micro-dollars (1_000_000 = $1).</param>
/// <param name="Recent">The most recent ledger entries, newest first (may be empty).</param>
public sealed record CloudAccountCredits(long BalanceMicros, IReadOnlyList<CloudCreditTransaction> Recent);

/// <summary>
/// A small HTTP client for the DevThrottle account credit balance (issue #884, cloud contract
/// <c>GET /api/v1/account/credits</c>). It reads the signed-in account's balance and recent ledger
/// entries, authenticated with the Bearer access token the Gateway already holds for cloud egress (the
/// SAME credential <see cref="DevThrottleAccountService.GetAccessTokenForForwarding"/> returns) - the
/// credits endpoint is JWT-authed, NOT dt_-key-authed. The base URL resolves the same way the rest of the
/// account egress does (<see cref="AccountTelemetryClient.ApiBaseUrlEnvVar"/> override, else the
/// production default), so this client introduces no new hard-coded host.
///
/// The access token is sent only as the Authorization header and is NEVER written to the log (DT-05).
/// The <see cref="HttpClient"/> is injectable so tests drive it against an in-process stub.
/// </summary>
public sealed class AccountCreditsClient
{
    /// <summary>The path that returns the signed-in account's credit balance + recent ledger.</summary>
    public const string CreditsPath = "/api/v1/account/credits";

    private readonly HttpClient _client;
    private readonly string _baseUrl;

    /// <param name="client">HTTP client (tests inject a stub); defaults to a short-timeout client.</param>
    /// <param name="baseUrl">API base URL; defaults to the shared account-egress base resolution.</param>
    public AccountCreditsClient(HttpClient? client = null, string? baseUrl = null)
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
    /// Reads the account's credit balance via <c>GET /api/v1/account/credits</c>. Throws on a non-success
    /// response (so an unreachable or erroring cloud surfaces as a clear failure the caller reports, never
    /// a fabricated balance) or a malformed body. No token is ever returned or logged.
    /// </summary>
    /// <param name="accessToken">The Bearer access token the Gateway holds. Never logged.</param>
    /// <param name="ct">Cancels the request.</param>
    public async Task<CloudAccountCredits> GetCreditsAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token is required", nameof(accessToken));

        var endpoint = $"{_baseUrl}{CreditsPath}";
        FileLog.Write($"[AccountCreditsClient] GetCreditsAsync: GET {endpoint}");

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        FileLog.Write($"[AccountCreditsClient] GetCreditsAsync: response status={(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Parse(json);
    }

    /// <summary>
    /// Parses the cloud <c>{ "data": { "balance_micros": N, "transactions": [ ... ] } }</c> envelope.
    /// Internal so the parse is unit-testable without a network. Throws on a body that is not that shape.
    /// </summary>
    internal static CloudAccountCredits Parse(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("account credits response was not a JSON object");
        var data = root["data"] as JsonObject
            ?? throw new InvalidOperationException("account credits response had no object 'data' envelope");

        if (data["balance_micros"] is not JsonValue balanceNode || !balanceNode.TryGetValue<long>(out var balance))
            throw new InvalidOperationException("account credits response had no numeric 'data.balance_micros'");

        var recent = new List<CloudCreditTransaction>();
        if (data["transactions"] is JsonArray txs)
        {
            foreach (var tx in txs)
            {
                if (tx is not JsonObject obj)
                    continue;
                var kind = (obj["kind"] as JsonValue)?.GetValue<string>() ?? "";
                var amount = (obj["amount_micros"] as JsonValue) is JsonValue a && a.TryGetValue<long>(out var amt) ? amt : 0L;
                var createdAt = (obj["created_at"] as JsonValue)?.GetValue<string>();
                recent.Add(new CloudCreditTransaction(kind, amount, createdAt));
            }
        }

        return new CloudAccountCredits(balance, recent);
    }
}
