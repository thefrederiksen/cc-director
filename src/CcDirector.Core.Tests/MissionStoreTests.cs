using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Tests for <see cref="MissionStore"/> - the durable record store behind the first-class Mission
/// (mission-as-first-class-unit-of-work). Mirrors <see cref="SessionStateStoreTests"/>: each test uses a
/// throwaway temp file and asserts the create / get / list / delete API plus survival across a fresh store
/// instance (the "survives a restart" guarantee), and the session-side MissionId/MissionName round-trip
/// through <see cref="PersistedSession"/>.
///
/// These are the SINGLE-TENANT behaviours - one owner, everything Local - which is what a Director and a
/// self-host Gateway are. The partitioning the store gained in #1039 is exercised separately in
/// <see cref="MissionStoreTenantPartitionTests"/>.
/// </summary>
public class MissionStoreTests
{
    /// <summary>A single-tenant store: one owner, so unattributed rows are that owner's.</summary>
    private static MissionStore NewStore(string path) => new(path, adoptUnattributedAs: TenantId.Local);

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"test_missions_{Guid.NewGuid()}.json");

    [Fact]
    public void Create_MintsIdAndPersists_GetReturnsIt()
    {
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);

            var mission = store.Create(TenantId.Local, "Session Lifecycle");

            Assert.NotEqual(Guid.Empty, mission.MissionId);
            Assert.Equal("Session Lifecycle", mission.MissionName);
            Assert.Null(mission.ParentMissionId);

            var fetched = store.Get(TenantId.Local, mission.MissionId);
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
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);
            var parent = store.Create(TenantId.Local, "Parent Mission");

            var child = store.Create(TenantId.Local, "Child Mission", parent.MissionId);

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
        var store = NewStore(TempPath());
        Assert.Throws<ArgumentException>(() => store.Create(TenantId.Local, "   "));
    }

    [Fact]
    public void List_ReturnsEveryMission_OldestFirst()
    {
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);
            var first = store.Create(TenantId.Local, "First");
            var second = store.Create(TenantId.Local, "Second");

            var all = store.List(TenantId.Local);

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
        var store = NewStore(TempPath());
        Assert.Null(store.Get(TenantId.Local, Guid.NewGuid()));
    }

    [Fact]
    public void Missions_SurviveANewStoreInstance_PersistReload()
    {
        var tempFile = TempPath();
        try
        {
            var created = NewStore(tempFile).Create(TenantId.Local, "Durable Mission");

            // A brand-new store instance against the same file models a Director restart.
            var reopened = NewStore(tempFile);
            var fetched = reopened.Get(TenantId.Local, created.MissionId);

            Assert.NotNull(fetched);
            Assert.Equal("Durable Mission", fetched.MissionName);
            Assert.Single(reopened.List(TenantId.Local));
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
        var tempFile = TempPath();
        try
        {
            var store = NewStore(tempFile);
            var mission = store.Create(TenantId.Local, "Doomed Mission");

            var removed = store.Delete(TenantId.Local, mission.MissionId);

            Assert.True(removed);
            Assert.Null(store.Get(TenantId.Local, mission.MissionId));
            Assert.Empty(store.List(TenantId.Local));
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
        var store = NewStore(TempPath());
        Assert.False(store.Delete(TenantId.Local, Guid.NewGuid()));
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
