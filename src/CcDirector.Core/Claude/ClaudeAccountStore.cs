using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

public class ClaudeAccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly List<ClaudeAccount> _accounts = new();

    public string FilePath { get; }
    public IReadOnlyList<ClaudeAccount> Accounts => _accounts.AsReadOnly();

    /// <summary>
    /// Fired after RefreshActiveTokenFromCredentials successfully updates an account's tokens.
    /// </summary>
    public event Action? TokensRefreshed;

    public ClaudeAccountStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            CcStorage.ToolConfig("director"),
            "accounts.json");
    }

    public void Load()
    {
        FileLog.Write($"[ClaudeAccountStore] Load: path={FilePath}");

        var dir = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException($"Cannot determine directory for path: {FilePath}");
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        if (!File.Exists(FilePath))
        {
            File.WriteAllText(FilePath, "[]");
            FileLog.Write("[ClaudeAccountStore] Load: created empty file");
            return;
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<List<ClaudeAccount>>(json, JsonOptions);
            if (loaded != null)
            {
                _accounts.Clear();
                _accounts.AddRange(loaded);
            }
            FileLog.Write($"[ClaudeAccountStore] Load: loaded {_accounts.Count} accounts");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeAccountStore] Load FAILED: {ex.Message}");
        }
    }

    public void Add(ClaudeAccount account)
    {
        FileLog.Write($"[ClaudeAccountStore] Add: label={account.Label}, subscription={account.SubscriptionType}");
        _accounts.Add(account);
        Save();
    }

    public void Remove(string id)
    {
        FileLog.Write($"[ClaudeAccountStore] Remove: id={id}");
        var index = _accounts.FindIndex(a => a.Id == id);
        if (index >= 0)
        {
            _accounts.RemoveAt(index);
            Save();
        }
    }

    public void UpdateLabel(string id, string newLabel)
    {
        FileLog.Write($"[ClaudeAccountStore] UpdateLabel: id={id}, label={newLabel}");
        var account = _accounts.Find(a => a.Id == id);
        if (account != null)
        {
            account.Label = newLabel;
            Save();
        }
    }

    public void UpdateToken(string id, string accessToken, string refreshToken, long expiresAt)
    {
        FileLog.Write($"[ClaudeAccountStore] UpdateToken: id={id}");
        var account = _accounts.Find(a => a.Id == id);
        if (account != null)
        {
            account.AccessToken = accessToken;
            account.RefreshToken = refreshToken;
            account.ExpiresAt = expiresAt;
            Save();
        }
    }

    public IReadOnlyList<ClaudeAccount> GetAll() => _accounts.AsReadOnly();

    /// <summary>
    /// Reads ~/.claude/.credentials.json, creates or updates matching account.
    /// Returns the account if successful, null if credentials file cannot be read.
    /// </summary>
    public ClaudeAccount? CaptureFromCredentials(string? credentialsPath = null)
    {
        var path = credentialsPath ?? GetDefaultCredentialsPath();
        FileLog.Write($"[ClaudeAccountStore] CaptureFromCredentials: path={path}");

        if (!File.Exists(path))
        {
            FileLog.Write("[ClaudeAccountStore] CaptureFromCredentials: credentials file not found");
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var creds = ParseCredentialsJson(json);
            if (creds == null)
            {
                FileLog.Write("[ClaudeAccountStore] CaptureFromCredentials: failed to parse credentials");
                return null;
            }

            // Check if we already have an account with this token
            var existing = _accounts.Find(a => a.AccessToken == creds.AccessToken);
            if (existing != null)
            {
                FileLog.Write($"[ClaudeAccountStore] CaptureFromCredentials: token matches existing account '{existing.Label}'");
                // Update token details in case they changed
                existing.RefreshToken = creds.RefreshToken;
                existing.ExpiresAt = creds.ExpiresAt;
                existing.SubscriptionType = creds.SubscriptionType;
                existing.RateLimitTier = creds.RateLimitTier;
                SetActiveAccount(existing.Id);
                return existing;
            }

            // New account - caller should set label
            var account = new ClaudeAccount
            {
                Label = "",
                AccessToken = creds.AccessToken,
                RefreshToken = creds.RefreshToken,
                ExpiresAt = creds.ExpiresAt,
                SubscriptionType = creds.SubscriptionType,
                RateLimitTier = creds.RateLimitTier,
                IsActive = true,
            };

            // Mark all others as not active
            foreach (var a in _accounts)
                a.IsActive = false;

            _accounts.Add(account);
            Save();
            FileLog.Write($"[ClaudeAccountStore] CaptureFromCredentials: added new account id={account.Id}");
            return account;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeAccountStore] CaptureFromCredentials FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Updates the active account's token from credentials file (called when FileSystemWatcher detects changes).
    /// </summary>
    public void RefreshActiveTokenFromCredentials(string? credentialsPath = null)
    {
        var path = credentialsPath ?? GetDefaultCredentialsPath();
        FileLog.Write($"[ClaudeAccountStore] RefreshActiveTokenFromCredentials: path={path}");

        if (!File.Exists(path))
            return;

        try
        {
            var json = File.ReadAllText(path);
            var creds = ParseCredentialsJson(json);
            if (creds == null) return;

            // Find matching account: exact token match ONLY
            // Do NOT use fuzzy matching (tier, subscription type) - different accounts
            // can have identical tiers, and fuzzy matching silently overwrites the wrong account.
            var matching = _accounts.Find(a => a.RefreshToken == creds.RefreshToken)
                        ?? _accounts.Find(a => a.AccessToken == creds.AccessToken);

            if (matching != null)
            {
                var tokenChanged = matching.AccessToken != creds.AccessToken
                    || matching.RefreshToken != creds.RefreshToken;

                FileLog.Write($"[ClaudeAccountStore] RefreshActiveTokenFromCredentials: updating account '{matching.Label}', tokenChanged={tokenChanged}");
                matching.AccessToken = creds.AccessToken;
                matching.RefreshToken = creds.RefreshToken;
                matching.ExpiresAt = creds.ExpiresAt;
                matching.SubscriptionType = creds.SubscriptionType;
                matching.RateLimitTier = creds.RateLimitTier;
                SetActiveAccount(matching.Id);

                if (tokenChanged)
                    TokensRefreshed?.Invoke();
            }
            else
            {
                FileLog.Write("[ClaudeAccountStore] RefreshActiveTokenFromCredentials: no matching account found");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeAccountStore] RefreshActiveTokenFromCredentials FAILED: {ex.Message}");
        }
    }

    public void SetActiveAccount(string id)
    {
        FileLog.Write($"[ClaudeAccountStore] SetActiveAccount: id={id}");
        foreach (var a in _accounts)
            a.IsActive = a.Id == id;
        Save();
    }

    private void Save()
    {
        FileLog.Write($"[ClaudeAccountStore] Save: writing {_accounts.Count} accounts");
        var json = JsonSerializer.Serialize(_accounts, JsonOptions);
        File.WriteAllText(FilePath, json);
    }

    /// <summary>
    /// Reads the credentials file and returns status about the current login.
    /// Returns (matchedAccount, credentialsData) - matchedAccount is null if no stored account matches.
    /// </summary>
    public (ClaudeAccount? MatchedAccount, CredentialsData? Creds) ReadCredentialStatus(string? credentialsPath = null)
    {
        var path = credentialsPath ?? GetDefaultCredentialsPath();
        FileLog.Write($"[ClaudeAccountStore] ReadCredentialStatus: path={path}");

        if (!File.Exists(path))
        {
            FileLog.Write("[ClaudeAccountStore] ReadCredentialStatus: file not found");
            return (null, null);
        }

        var json = File.ReadAllText(path);
        var creds = ParseCredentialsJson(json);
        if (creds == null)
        {
            FileLog.Write("[ClaudeAccountStore] ReadCredentialStatus: parse failed");
            return (null, null);
        }

        // Try to match against stored accounts
        var matched = _accounts.Find(a => a.AccessToken == creds.AccessToken)
                   ?? _accounts.Find(a => a.RefreshToken == creds.RefreshToken);

        FileLog.Write($"[ClaudeAccountStore] ReadCredentialStatus: matched={matched?.Label ?? "(none)"}");
        return (matched, creds);
    }

    /// <summary>
    /// Attempts to fetch the user profile from Anthropic's OAuth endpoint.
    /// Returns the display name/email if successful, null if the endpoint is unavailable.
    /// </summary>
    public static async Task<string?> TryFetchProfileLabelAsync(string accessToken)
    {
        FileLog.Write("[ClaudeAccountStore] TryFetchProfileLabelAsync: attempting profile fetch");

        using var client = new System.Net.Http.HttpClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync("https://api.anthropic.com/api/oauth/profile");
        if (!response.IsSuccessStatusCode)
        {
            FileLog.Write($"[ClaudeAccountStore] TryFetchProfileLabelAsync: status={response.StatusCode}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        // Try common profile fields
        if (doc.RootElement.TryGetProperty("name", out var name) && name.GetString() is string n && !string.IsNullOrEmpty(n))
        {
            FileLog.Write($"[ClaudeAccountStore] TryFetchProfileLabelAsync: got name={n}");
            return n;
        }
        if (doc.RootElement.TryGetProperty("email", out var email) && email.GetString() is string e && !string.IsNullOrEmpty(e))
        {
            FileLog.Write($"[ClaudeAccountStore] TryFetchProfileLabelAsync: got email={e}");
            return e;
        }
        if (doc.RootElement.TryGetProperty("organization", out var org) && org.GetString() is string o && !string.IsNullOrEmpty(o))
        {
            FileLog.Write($"[ClaudeAccountStore] TryFetchProfileLabelAsync: got org={o}");
            return o;
        }

        FileLog.Write("[ClaudeAccountStore] TryFetchProfileLabelAsync: no usable fields in response");
        return null;
    }

    public static string GetDefaultCredentialsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            ".credentials.json");
    }

    /// <summary>
    /// Parses the Claude credentials JSON format.
    /// Expected: { "claudeAiOauth": { "accessToken": "...", "refreshToken": "...", ... } }
    /// </summary>
    public static CredentialsData? ParseCredentialsJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
                return null;

            var accessToken = oauth.TryGetProperty("accessToken", out var at) ? at.GetString() ?? "" : "";
            var refreshToken = oauth.TryGetProperty("refreshToken", out var rt) ? rt.GetString() ?? "" : "";
            var expiresAt = oauth.TryGetProperty("expiresAt", out var ea) ? ea.GetInt64() : 0L;
            var subscriptionType = oauth.TryGetProperty("subscriptionType", out var st) ? st.GetString() ?? "" : "";
            var rateLimitTier = oauth.TryGetProperty("rateLimitTier", out var rlt) ? rlt.GetString() ?? "" : "";

            if (string.IsNullOrEmpty(accessToken))
                return null;

            return new CredentialsData
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = expiresAt,
                SubscriptionType = subscriptionType,
                RateLimitTier = rateLimitTier,
            };
        }
        catch
        {
            return null;
        }
    }

    public sealed class CredentialsData
    {
        public string AccessToken { get; init; } = "";
        public string RefreshToken { get; init; } = "";
        public long ExpiresAt { get; init; }
        public string SubscriptionType { get; init; } = "";
        public string RateLimitTier { get; init; } = "";
    }
}
