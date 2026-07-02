using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Account;

/// <summary>
/// Mints a DevThrottle inference key via the cloud account API (issue #881): <c>POST {base}/keys</c>
/// with the account JWT as the bearer, which returns a fresh <c>dt_live_</c> key ONCE. The base URL is
/// the same DevThrottle host the hosted transcription targets
/// (<see cref="TranscriptionEndpointResolver.DevThrottleBaseUrl"/>), so the account and the inference
/// credential live on one host.
///
/// The key value is NEVER written to the log (security rule DT-05): this class logs only the outcome
/// (status code, whether a key came back), never the key or the JWT.
/// </summary>
public sealed class AccountInferenceKeyProvisioner : IInferenceKeyMinter
{
    /// <summary>
    /// Matches a DevThrottle API key so the key can be located in a tolerant response shape. The key
    /// body is base64url-ish, so the charset includes '-' and '_' (a narrower charset truncates the
    /// real key at the first such character, yielding an invalid key - the exact bug this pattern fixes).
    /// </summary>
    private static readonly Regex DtKeyPattern = new(@"dt_(live|test)_[A-Za-z0-9_\-]+", RegexOptions.Compiled);

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    /// <param name="http">HTTP client for the mint POST (tests inject a stub). A shared 20s client when null.</param>
    /// <param name="baseUrl">The DevThrottle API base URL; defaults to the hosted transcription base.</param>
    public AccountInferenceKeyProvisioner(HttpClient? http = null, string? baseUrl = null)
    {
        _http = http ?? SharedHttp;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? TranscriptionEndpointResolver.DevThrottleBaseUrl : baseUrl!;
    }

    public async Task<string?> MintAsync(string accessToken, string label, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            FileLog.Write("[AccountInferenceKeyProvisioner] MintAsync: no access token -> cannot mint");
            return null;
        }

        var url = _baseUrl.TrimEnd('/') + "/keys";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var body = JsonSerializer.Serialize(new { name = string.IsNullOrWhiteSpace(label) ? "cc-director" : label });
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[AccountInferenceKeyProvisioner] MintAsync POST /keys -> {(int)resp.StatusCode} (no key minted)");
                return null;
            }

            var key = ExtractKey(text);
            FileLog.Write($"[AccountInferenceKeyProvisioner] MintAsync POST /keys -> {(int)resp.StatusCode}, key minted={(key is not null)}");
            return key;
        }
        catch (Exception ex)
        {
            // Best-effort: a network error here must never break sign-in - the caller falls back to the
            // add-credits / manual-key state and the user can still paste a key.
            FileLog.Write($"[AccountInferenceKeyProvisioner] MintAsync failed (ignored, best-effort): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Pulls the <c>dt_</c> key out of the mint response. Prefers the common JSON field names; falls
    /// back to a pattern scan so a minor shape change (key vs api_key vs secret) still works. Returns
    /// null when the body carries no recognisable key.
    /// </summary>
    internal static string? ExtractKey(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // The live mint response wraps the full key at data.key, alongside a data.record with a
            // MASKED value (dt_live_...last4) that also looks like a key - so read the structured
            // full-key field first and never fall for the masked display value.
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                var nested = KeyFromFields(data);
                if (nested is not null) return nested;
            }
            var top = KeyFromFields(root);
            if (top is not null) return top;
        }
        catch (JsonException)
        {
            // Not JSON, or an unexpected shape: fall through to the pattern scan below.
        }

        var m = DtKeyPattern.Match(body);
        return m.Success ? m.Value : null;
    }

    /// <summary>Reads the first full-key-shaped string from the common field names on one JSON object.</summary>
    private static string? KeyFromFields(JsonElement obj)
    {
        foreach (var name in new[] { "key", "api_key", "apiKey", "secret", "value", "token", "plaintext" })
        {
            if (obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                // Require a full key (no "..." mask) that matches the whole string.
                if (!string.IsNullOrWhiteSpace(s) && !s.Contains("...") && DtKeyPattern.IsMatch(s))
                    return s!.Trim();
            }
        }
        return null;
    }
}
