using System.Text.Json;
using System.Text.Json.Serialization;
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

    public ClaudeAccountStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CcDirector",
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
                Save();
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

            // Find matching account by token prefix or refresh token
            var matching = _accounts.Find(a => a.RefreshToken == creds.RefreshToken)
                        ?? _accounts.Find(a => a.AccessToken == creds.AccessToken);

            if (matching != null)
            {
                FileLog.Write($"[ClaudeAccountStore] RefreshActiveTokenFromCredentials: updating account '{matching.Label}'");
                matching.AccessToken = creds.AccessToken;
                matching.RefreshToken = creds.RefreshToken;
                matching.ExpiresAt = creds.ExpiresAt;
                matching.SubscriptionType = creds.SubscriptionType;
                matching.RateLimitTier = creds.RateLimitTier;
                SetActiveAccount(matching.Id);
                Save();
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

    private void SetActiveAccount(string id)
    {
        foreach (var a in _accounts)
            a.IsActive = a.Id == id;
    }

    private void Save()
    {
        FileLog.Write($"[ClaudeAccountStore] Save: writing {_accounts.Count} accounts");
        var json = JsonSerializer.Serialize(_accounts, JsonOptions);
        File.WriteAllText(FilePath, json);
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
