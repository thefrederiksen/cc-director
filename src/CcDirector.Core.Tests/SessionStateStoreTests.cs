using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

public class SessionStateStoreTests
{
    [Fact]
    public void SaveAndLoad_SingleSession_RoundTrips()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            var store = new SessionStateStore(tempFile);

            var session = new PersistedSession
            {
                Id = Guid.NewGuid(),
                RepoPath = @"C:\test\repo",
                WorkingDirectory = @"C:\test\repo",
                ClaudeSessionId = "test-claude-session-123",
                CustomName = "Test Session",
                CustomColor = "#FF0000",
                ActivityState = ActivityState.Idle,
                CreatedAt = DateTimeOffset.UtcNow
            };

            // Save
            store.Save(new[] { session });

            // Verify file exists and has content
            Assert.True(File.Exists(tempFile), "File should exist after save");
            var json = File.ReadAllText(tempFile);
            Assert.Contains("test-claude-session-123", json);

            // Load
            var loaded = store.Load();

            // Verify
            Assert.Single(loaded);
            Assert.Equal(session.Id, loaded[0].Id);
            Assert.Equal("test-claude-session-123", loaded[0].ClaudeSessionId);
            Assert.Equal("Test Session", loaded[0].CustomName);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Load_EmptyFile_ReturnsEmptyList()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(tempFile, "[]");
            var store = new SessionStateStore(tempFile);

            var loaded = store.Load();

            Assert.Empty(loaded);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Load_NonExistentFile_ReturnsEmptyList()
    {
        var store = new SessionStateStore(@"C:\nonexistent\path\sessions.json");

        var loaded = store.Load();

        Assert.Empty(loaded);
    }
}
