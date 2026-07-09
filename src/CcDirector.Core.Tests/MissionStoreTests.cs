using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests for <see cref="MissionStore"/> - the durable record store behind the first-class Mission
/// (mission-as-first-class-unit-of-work). Mirrors <see cref="SessionStateStoreTests"/>: each test uses a
/// throwaway temp file and asserts the create / get / list / delete API plus survival across a fresh store
/// instance (the "survives a restart" guarantee), and the session-side MissionId/MissionName round-trip
/// through <see cref="PersistedSession"/>.
/// </summary>
public class MissionStoreTests
{
    [Fact]
    public void Create_MintsIdAndPersists_GetReturnsIt()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_missions_{Guid.NewGuid()}.json");
        try
        {
            var store = new MissionStore(tempFile);

            var mission = store.Create("Session Lifecycle");

            Assert.NotEqual(Guid.Empty, mission.MissionId);
            Assert.Equal("Session Lifecycle", mission.MissionName);
            Assert.Null(mission.ParentMissionId);

            var fetched = store.Get(mission.MissionId);
            Assert.NotNull(fetched);
            Assert.Equal(mission.MissionId, fetched.MissionId);
            Assert.Equal("Session Lifecycle", fetched.MissionName);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Create_WithParent_NestsUnderIt()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_missions_{Guid.NewGuid()}.json");
        try
        {
            var store = new MissionStore(tempFile);
            var parent = store.Create("Parent Mission");

            var child = store.Create("Child Mission", parent.MissionId);

            Assert.Equal(parent.MissionId, child.ParentMissionId);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Create_BlankName_Throws()
    {
        var store = new MissionStore(Path.Combine(Path.GetTempPath(), $"test_missions_{Guid.NewGuid()}.json"));
        Assert.Throws<ArgumentException>(() => store.Create("   "));
    }

    [Fact]
    public void List_ReturnsEveryMission_OldestFirst()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_missions_{Guid.NewGuid()}.json");
        try
        {
            var store = new MissionStore(tempFile);
            var first = store.Create("First");
            var second = store.Create("Second");

            var all = store.List();

            Assert.Equal(2, all.Count);
            Assert.Equal(first.MissionId, all[0].MissionId);
            Assert.Equal(second.MissionId, all[1].MissionId);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Get_UnknownId_ReturnsNull()
    {
        var store = new MissionStore(Path.Combine(Path.GetTempPath(), $"test_missions_{Guid.NewGuid()}.json"));
        Assert.Null(store.Get(Guid.NewGuid()));
    }

    [Fact]
    public void Missions_SurviveANewStoreInstance_PersistReload()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_missions_{Guid.NewGuid()}.json");
        try
        {
            var created = new MissionStore(tempFile).Create("Durable Mission");

            // A brand-new store instance against the same file models a Director restart.
            var reopened = new MissionStore(tempFile);
            var fetched = reopened.Get(created.MissionId);

            Assert.NotNull(fetched);
            Assert.Equal("Durable Mission", fetched.MissionName);
            Assert.Single(reopened.List());
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Delete_RemovesMission_AndReturnsTrue()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_missions_{Guid.NewGuid()}.json");
        try
        {
            var store = new MissionStore(tempFile);
            var mission = store.Create("Doomed Mission");

            var removed = store.Delete(mission.MissionId);

            Assert.True(removed);
            Assert.Null(store.Get(mission.MissionId));
            Assert.Empty(store.List());
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Delete_UnknownId_ReturnsFalse()
    {
        var store = new MissionStore(Path.Combine(Path.GetTempPath(), $"test_missions_{Guid.NewGuid()}.json"));
        Assert.False(store.Delete(Guid.NewGuid()));
    }

    [Fact]
    public void PersistedSession_MissionAttachment_SurvivesRoundTrip()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_store_{Guid.NewGuid()}.json");
        try
        {
            var store = new SessionStateStore(tempFile);
            var missionId = Guid.NewGuid();

            var session = new PersistedSession
            {
                Id = Guid.NewGuid(),
                RepoPath = @"C:\test\repo",
                WorkingDirectory = @"C:\test\repo",
                ClaudeSessionId = "test-session-mission",
                MissionId = missionId,
                MissionName = "Session Lifecycle",
                ActivityState = ActivityState.Idle,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            store.Save(new[] { session });
            var result = store.Load();

            Assert.True(result.Success);
            Assert.Single(result.Sessions);
            Assert.Equal(missionId, result.Sessions[0].MissionId);
            Assert.Equal("Session Lifecycle", result.Sessions[0].MissionName);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
}
