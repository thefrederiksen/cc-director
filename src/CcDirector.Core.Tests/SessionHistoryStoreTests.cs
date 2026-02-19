using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

public class SessionHistoryStoreTests : IDisposable
{
    private readonly string _tempDir;

    public SessionHistoryStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SessionHistoryStoreTests_{Guid.NewGuid():N}");
    }

    [Fact]
    public void Save_CreatesDirectoryIfNotExists()
    {
        var folder = Path.Combine(_tempDir, "sub", "sessions");
        var store = new SessionHistoryStore(folder);

        var entry = MakeEntry("Test Session");
        store.Save(entry);

        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void Save_AndLoad_RoundTrips()
    {
        var store = new SessionHistoryStore(_tempDir);
        var entry = MakeEntry("My Workspace");
        entry.CustomColor = "#FF0000";
        entry.ClaudeSessionId = "claude-abc-123";
        entry.FirstPromptSnippet = "Help me fix this bug";

        store.Save(entry);
        var loaded = store.Load(entry.Id);

        Assert.NotNull(loaded);
        Assert.Equal(entry.Id, loaded.Id);
        Assert.Equal("My Workspace", loaded.CustomName);
        Assert.Equal("#FF0000", loaded.CustomColor);
        Assert.Equal(entry.RepoPath, loaded.RepoPath);
        Assert.Equal("claude-abc-123", loaded.ClaudeSessionId);
        Assert.Equal(entry.CreatedAt, loaded.CreatedAt);
        Assert.Equal(entry.LastUsedAt, loaded.LastUsedAt);
        Assert.Equal("Help me fix this bug", loaded.FirstPromptSnippet);
    }

    [Fact]
    public void Load_ReturnsNullForNonExistentId()
    {
        var store = new SessionHistoryStore(_tempDir);
        Assert.Null(store.Load(Guid.NewGuid()));
    }

    [Fact]
    public void LoadAll_ReturnsEmptyForEmptyFolder()
    {
        var store = new SessionHistoryStore(_tempDir);
        var result = store.LoadAll();
        Assert.Empty(result);
    }

    [Fact]
    public void LoadAll_ReturnsEmptyForNonExistentFolder()
    {
        var store = new SessionHistoryStore(Path.Combine(_tempDir, "nonexistent"));
        var result = store.LoadAll();
        Assert.Empty(result);
    }

    [Fact]
    public void LoadAll_SortsByLastUsedAtDescending()
    {
        var store = new SessionHistoryStore(_tempDir);

        var oldest = MakeEntry("Oldest");
        oldest.LastUsedAt = DateTimeOffset.UtcNow.AddHours(-3);
        store.Save(oldest);

        var newest = MakeEntry("Newest");
        newest.LastUsedAt = DateTimeOffset.UtcNow;
        store.Save(newest);

        var middle = MakeEntry("Middle");
        middle.LastUsedAt = DateTimeOffset.UtcNow.AddHours(-1);
        store.Save(middle);

        var all = store.LoadAll();

        Assert.Equal(3, all.Count);
        Assert.Equal("Newest", all[0].CustomName);
        Assert.Equal("Middle", all[1].CustomName);
        Assert.Equal("Oldest", all[2].CustomName);
    }

    [Fact]
    public void LoadAll_SkipsCorruptFiles()
    {
        var store = new SessionHistoryStore(_tempDir);

        // Save a valid entry
        var valid = MakeEntry("Valid");
        store.Save(valid);

        // Write a corrupt file
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(Path.Combine(_tempDir, $"{Guid.NewGuid():N}.json"), "not valid json!!!");

        var all = store.LoadAll();

        Assert.Single(all);
        Assert.Equal("Valid", all[0].CustomName);
    }

    [Fact]
    public void Save_OverwritesExistingEntry()
    {
        var store = new SessionHistoryStore(_tempDir);

        var entry = MakeEntry("Original Name");
        store.Save(entry);

        entry.CustomName = "Updated Name";
        entry.ClaudeSessionId = "new-claude-id";
        store.Save(entry);

        var loaded = store.Load(entry.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Updated Name", loaded.CustomName);
        Assert.Equal("new-claude-id", loaded.ClaudeSessionId);

        // Should still be only one file
        Assert.Single(store.LoadAll());
    }

    [Fact]
    public void FindByClaudeSessionId_FindsMatch()
    {
        var store = new SessionHistoryStore(_tempDir);

        var entry1 = MakeEntry("Session A");
        entry1.ClaudeSessionId = "claude-aaa";
        store.Save(entry1);

        var entry2 = MakeEntry("Session B");
        entry2.ClaudeSessionId = "claude-bbb";
        store.Save(entry2);

        var found = store.FindByClaudeSessionId("claude-bbb");

        Assert.NotNull(found);
        Assert.Equal("Session B", found.CustomName);
        Assert.Equal("claude-bbb", found.ClaudeSessionId);
    }

    [Fact]
    public void FindByClaudeSessionId_ReturnsNullWhenNotFound()
    {
        var store = new SessionHistoryStore(_tempDir);

        var entry = MakeEntry("Only Session");
        entry.ClaudeSessionId = "claude-xyz";
        store.Save(entry);

        Assert.Null(store.FindByClaudeSessionId("nonexistent-id"));
    }

    [Fact]
    public void FindByClaudeSessionId_ReturnsNullForNullOrEmpty()
    {
        var store = new SessionHistoryStore(_tempDir);
        Assert.Null(store.FindByClaudeSessionId(null!));
        Assert.Null(store.FindByClaudeSessionId(""));
    }

    [Fact]
    public void FindByClaudeSessionId_ReturnsMostRecentWhenDuplicates()
    {
        var store = new SessionHistoryStore(_tempDir);

        var older = MakeEntry("Older Resume");
        older.ClaudeSessionId = "same-claude-id";
        older.LastUsedAt = DateTimeOffset.UtcNow.AddDays(-5);
        store.Save(older);

        var newer = MakeEntry("Newer Resume");
        newer.ClaudeSessionId = "same-claude-id";
        newer.LastUsedAt = DateTimeOffset.UtcNow;
        store.Save(newer);

        var found = store.FindByClaudeSessionId("same-claude-id");

        Assert.NotNull(found);
        Assert.Equal("Newer Resume", found.CustomName);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        var store = new SessionHistoryStore(_tempDir);

        var entry = MakeEntry("To Delete");
        store.Save(entry);
        Assert.NotNull(store.Load(entry.Id));

        var deleted = store.Delete(entry.Id);

        Assert.True(deleted);
        Assert.Null(store.Load(entry.Id));
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void Delete_ReturnsFalseForNonExistent()
    {
        var store = new SessionHistoryStore(_tempDir);
        Assert.False(store.Delete(Guid.NewGuid()));
    }

    private SessionHistoryEntry MakeEntry(string name)
    {
        return new SessionHistoryEntry
        {
            Id = Guid.NewGuid(),
            CustomName = name,
            RepoPath = _tempDir,
            CreatedAt = DateTimeOffset.UtcNow,
            LastUsedAt = DateTimeOffset.UtcNow,
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort cleanup */ }
    }
}
