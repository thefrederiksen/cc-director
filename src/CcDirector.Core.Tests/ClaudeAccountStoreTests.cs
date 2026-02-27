using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests;

public class ClaudeAccountStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public ClaudeAccountStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AccountStoreTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "accounts.json");
    }

    [Fact]
    public void Load_CreatesFileIfNotExists()
    {
        var store = new ClaudeAccountStore(_filePath);
        store.Load();

        Assert.True(File.Exists(_filePath));
        Assert.Empty(store.Accounts);
    }

    [Fact]
    public void Load_ReadsExistingAccounts()
    {
        File.WriteAllText(_filePath, """
            [
                {
                    "Id": "abc123",
                    "Label": "Personal",
                    "SubscriptionType": "max",
                    "RateLimitTier": "default_claude_max_20x",
                    "AccessToken": "sk-ant-oat01-test",
                    "RefreshToken": "sk-ant-ort01-test",
                    "ExpiresAt": 1772177228166,
                    "IsActive": true
                }
            ]
            """);

        var store = new ClaudeAccountStore(_filePath);
        store.Load();

        Assert.Single(store.Accounts);
        Assert.Equal("Personal", store.Accounts[0].Label);
        Assert.Equal("max", store.Accounts[0].SubscriptionType);
        Assert.Equal("default_claude_max_20x", store.Accounts[0].RateLimitTier);
        Assert.True(store.Accounts[0].IsActive);
    }

    [Fact]
    public void Add_PersistsToDisk()
    {
        var store = new ClaudeAccountStore(_filePath);
        store.Load();

        var account = new ClaudeAccount
        {
            Label = "Work",
            SubscriptionType = "pro",
            AccessToken = "sk-test",
        };
        store.Add(account);

        Assert.Single(store.Accounts);

        // Verify persisted by loading fresh instance
        var store2 = new ClaudeAccountStore(_filePath);
        store2.Load();
        Assert.Single(store2.Accounts);
        Assert.Equal("Work", store2.Accounts[0].Label);
    }

    [Fact]
    public void Remove_DeletesAccount()
    {
        var store = new ClaudeAccountStore(_filePath);
        store.Load();

        var account = new ClaudeAccount { Label = "ToRemove", AccessToken = "sk-test" };
        store.Add(account);
        Assert.Single(store.Accounts);

        store.Remove(account.Id);
        Assert.Empty(store.Accounts);
    }

    [Fact]
    public void Remove_NonExistentId_NoOp()
    {
        var store = new ClaudeAccountStore(_filePath);
        store.Load();
        store.Add(new ClaudeAccount { Label = "Keep", AccessToken = "sk-test" });

        store.Remove("nonexistent-id");
        Assert.Single(store.Accounts);
    }

    [Fact]
    public void UpdateLabel_ChangesLabel()
    {
        var store = new ClaudeAccountStore(_filePath);
        store.Load();

        var account = new ClaudeAccount { Label = "Old", AccessToken = "sk-test" };
        store.Add(account);

        store.UpdateLabel(account.Id, "New Label");
        Assert.Equal("New Label", store.Accounts[0].Label);
    }

    [Fact]
    public void UpdateToken_ChangesTokenFields()
    {
        var store = new ClaudeAccountStore(_filePath);
        store.Load();

        var account = new ClaudeAccount
        {
            Label = "Test",
            AccessToken = "old-token",
            RefreshToken = "old-refresh",
            ExpiresAt = 100,
        };
        store.Add(account);

        store.UpdateToken(account.Id, "new-token", "new-refresh", 999);

        Assert.Equal("new-token", store.Accounts[0].AccessToken);
        Assert.Equal("new-refresh", store.Accounts[0].RefreshToken);
        Assert.Equal(999, store.Accounts[0].ExpiresAt);
    }

    [Fact]
    public void ParseCredentialsJson_ValidJson_ReturnsParsedData()
    {
        var json = """
            {
                "claudeAiOauth": {
                    "accessToken": "sk-ant-oat01-test123",
                    "refreshToken": "sk-ant-ort01-test456",
                    "expiresAt": 1772177228166,
                    "scopes": ["user:inference"],
                    "subscriptionType": "max",
                    "rateLimitTier": "default_claude_max_20x"
                }
            }
            """;

        var result = ClaudeAccountStore.ParseCredentialsJson(json);

        Assert.NotNull(result);
        Assert.Equal("sk-ant-oat01-test123", result.AccessToken);
        Assert.Equal("sk-ant-ort01-test456", result.RefreshToken);
        Assert.Equal(1772177228166, result.ExpiresAt);
        Assert.Equal("max", result.SubscriptionType);
        Assert.Equal("default_claude_max_20x", result.RateLimitTier);
    }

    [Fact]
    public void ParseCredentialsJson_MissingOauthKey_ReturnsNull()
    {
        var json = """{ "someOtherKey": {} }""";
        var result = ClaudeAccountStore.ParseCredentialsJson(json);
        Assert.Null(result);
    }

    [Fact]
    public void ParseCredentialsJson_EmptyAccessToken_ReturnsNull()
    {
        var json = """
            {
                "claudeAiOauth": {
                    "accessToken": "",
                    "refreshToken": "test",
                    "expiresAt": 123
                }
            }
            """;

        var result = ClaudeAccountStore.ParseCredentialsJson(json);
        Assert.Null(result);
    }

    [Fact]
    public void ParseCredentialsJson_InvalidJson_ReturnsNull()
    {
        var result = ClaudeAccountStore.ParseCredentialsJson("not json");
        Assert.Null(result);
    }

    [Fact]
    public void ParseCredentialsJson_MissingOptionalFields_ReturnsDefaults()
    {
        var json = """
            {
                "claudeAiOauth": {
                    "accessToken": "sk-test"
                }
            }
            """;

        var result = ClaudeAccountStore.ParseCredentialsJson(json);

        Assert.NotNull(result);
        Assert.Equal("sk-test", result.AccessToken);
        Assert.Equal("", result.RefreshToken);
        Assert.Equal(0, result.ExpiresAt);
        Assert.Equal("", result.SubscriptionType);
        Assert.Equal("", result.RateLimitTier);
    }

    [Fact]
    public void CaptureFromCredentials_ValidFile_AddsAccount()
    {
        var credPath = Path.Combine(_tempDir, ".credentials.json");
        File.WriteAllText(credPath, """
            {
                "claudeAiOauth": {
                    "accessToken": "sk-ant-oat01-capture",
                    "refreshToken": "sk-ant-ort01-capture",
                    "expiresAt": 1772177228166,
                    "subscriptionType": "max",
                    "rateLimitTier": "default_claude_max_20x"
                }
            }
            """);

        var store = new ClaudeAccountStore(_filePath);
        store.Load();

        var account = store.CaptureFromCredentials(credPath);

        Assert.NotNull(account);
        Assert.Equal("sk-ant-oat01-capture", account.AccessToken);
        Assert.Equal("max", account.SubscriptionType);
        Assert.True(account.IsActive);
        Assert.Single(store.Accounts);
    }

    [Fact]
    public void CaptureFromCredentials_DuplicateToken_UpdatesExisting()
    {
        var credPath = Path.Combine(_tempDir, ".credentials.json");
        File.WriteAllText(credPath, """
            {
                "claudeAiOauth": {
                    "accessToken": "sk-ant-oat01-dup",
                    "refreshToken": "sk-ant-ort01-dup",
                    "expiresAt": 1772177228166,
                    "subscriptionType": "max",
                    "rateLimitTier": "default_claude_max_20x"
                }
            }
            """);

        var store = new ClaudeAccountStore(_filePath);
        store.Load();

        // First capture
        var account1 = store.CaptureFromCredentials(credPath);
        Assert.NotNull(account1);
        store.UpdateLabel(account1.Id, "Personal");

        // Second capture with same token
        var account2 = store.CaptureFromCredentials(credPath);
        Assert.NotNull(account2);
        Assert.Equal(account1.Id, account2.Id);
        Assert.Single(store.Accounts);
    }

    [Fact]
    public void CaptureFromCredentials_MissingFile_ReturnsNull()
    {
        var store = new ClaudeAccountStore(_filePath);
        store.Load();

        var account = store.CaptureFromCredentials(Path.Combine(_tempDir, "nonexistent.json"));
        Assert.Null(account);
        Assert.Empty(store.Accounts);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }
}
