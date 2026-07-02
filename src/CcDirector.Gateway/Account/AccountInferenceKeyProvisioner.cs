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

    public async Task<MintedInferenceKey?> MintAsync(string accessToken, string label, CancellationToken ct = default)
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
            if (key is null)
            {
                FileLog.Write($"[AccountInferenceKeyProvisioner] MintAsync POST /keys -> {(int)resp.StatusCode} but no key in the body");
                return null;
            }
            var id = ExtractKeyId(text);
            FileLog.Write($"[AccountInferenceKeyProvisioner] MintAsync POST /keys -> {(int)resp.StatusCode}, key minted, id present={(id is not null)}");
            return new MintedInferenceKey(key, id);
        }
        catch (Exception ex)
        {
            // Best-effort: a network error here must never break sign-in - the caller falls back to the
            // add-credits / manual-key state and the user can still paste a key.
            FileLog.Write($"[AccountInferenceKeyProvisioner] MintAsync failed (ignored, best-effort): {ex.Message}");
            return null;
        }
    }

    public async Task<bool> RevokeAsync(string accessToken, string keyId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(keyId))
        {
            FileLog.Write("[AccountInferenceKeyProvisioner] RevokeAsync: missing token or key id -> not revoking");
            return false;
        }

        var url = _baseUrl.TrimEnd('/') + "/keys/" + Uri.EscapeDataString(keyId);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var resp = await _http.SendAsync(req, ct);
            // A 404 means it is already gone - treat that as revoked (idempotent).
            var ok = resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.NotFound;
            FileLog.Write($"[AccountInferenceKeyProvisioner] RevokeAsync DELETE /keys/{{id}} -> {(int)resp.StatusCode}, revoked={ok}");
            return ok;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountInferenceKeyProvisioner] RevokeAsync failed (ignored, best-effort): {ex.Message}");
            return false;
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

    /// <summary>
    /// Pulls the key's stable id from the mint response (needed to revoke it later). The live shape puts
    /// it at <c>data.record.id</c>; falls back to <c>data.id</c> / root <c>id</c>. Returns null when absent.
    /// </summary>
    internal static string? ExtractKeyId(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("record", out var rec) && rec.ValueKind == JsonValueKind.Object
                    && rec.TryGetProperty("id", out var recId) && recId.ValueKind == JsonValueKind.String)
                    return recId.GetString();
                if (data.TryGetProperty("id", out var dataId) && dataId.ValueKind == JsonValueKind.String)
                    return dataId.GetString();
            }
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var rootId) && rootId.ValueKind == JsonValueKind.String)
                return rootId.GetString();
        }
        catch (JsonException)
        {
            // No parseable id: revoke-on-sign-out simply won't have one to use.
        }
        return null;
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
