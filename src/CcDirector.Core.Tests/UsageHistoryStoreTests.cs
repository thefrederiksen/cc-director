using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests;

public class UsageHistoryStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public UsageHistoryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"UsageHistoryStoreTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "usage-history.jsonl");
    }

    private ClaudeUsageInfo CreateUsageInfo(DateTimeOffset fetchedAt, string accountId = "acc1", double fiveHour = 0.5)
    {
        return new ClaudeUsageInfo
        {
            AccountId = accountId,
            FetchedAt = fetchedAt,
            FiveHourUtilization = fiveHour,
            SevenDayUtilization = 0.3,
            HasData = true,
        };
    }

    [Fact]
    public void Append_WritesEntry()
    {
        var store = new UsageHistoryStore(_filePath);
        var info = CreateUsageInfo(DateTimeOffset.UtcNow);

        store.Append(info);

        Assert.True(File.Exists(_filePath));
        var lines = File.ReadAllLines(_filePath);
        var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.Single(nonEmpty);
    }

    [Fact]
    public void LoadAll_ReturnsEntries()
    {
        var store = new UsageHistoryStore(_filePath);
        store.Append(CreateUsageInfo(DateTimeOffset.UtcNow, "acc1"));
        store.Append(CreateUsageInfo(DateTimeOffset.UtcNow, "acc2"));

        var entries = store.LoadAll();

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void LoadAll_WithMaxAge_FiltersOldEntries()
    {
        var store = new UsageHistoryStore(_filePath);

        // Recent entry
        store.Append(CreateUsageInfo(DateTimeOffset.UtcNow, "recent"));

        // Old entry -- write manually since Append uses info.FetchedAt as the timestamp
        var oldEntry = new UsageHistoryEntry
        {
            Timestamp = DateTimeOffset.UtcNow.AddDays(-10),
            AccountId = "old",
            FiveHourUtilization = 0.1,
        };
        var line = System.Text.Json.JsonSerializer.Serialize(oldEntry,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        File.AppendAllText(_filePath, line + Environment.NewLine);

        var entries = store.LoadAll(maxAge: TimeSpan.FromDays(1));

        Assert.Single(entries);
        Assert.Equal("recent", entries[0].AccountId);
    }

    [Fact]
    public void Prune_RemovesOldEntries()
    {
        var store = new UsageHistoryStore(_filePath);

        // Write a recent entry via Append
        store.Append(CreateUsageInfo(DateTimeOffset.UtcNow, "recent"));

        // Write an old entry manually (older than 30 days)
        var oldEntry = new UsageHistoryEntry
        {
            Timestamp = DateTimeOffset.UtcNow.AddDays(-60),
            AccountId = "old",
            FiveHourUtilization = 0.1,
        };
        var line = System.Text.Json.JsonSerializer.Serialize(oldEntry,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        File.AppendAllText(_filePath, line + Environment.NewLine);

        // Verify both present before prune
        var allBefore = store.LoadAll();
        Assert.Equal(2, allBefore.Count);

        store.Prune();

        var allAfter = store.LoadAll();
        Assert.Single(allAfter);
        Assert.Equal("recent", allAfter[0].AccountId);
    }

    [Fact]
    public void Append_NoDirectory_CreatesIt()
    {
        var nestedDir = Path.Combine(_tempDir, "sub", "deep");
        var nestedPath = Path.Combine(nestedDir, "usage.jsonl");

        var store = new UsageHistoryStore(nestedPath);
        store.Append(CreateUsageInfo(DateTimeOffset.UtcNow));

        Assert.True(Directory.Exists(nestedDir));
        Assert.True(File.Exists(nestedPath));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }
}
