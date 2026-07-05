using System.Text.Json;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Issue #961: claimed crash journals must not accumulate forever. Anything older than the
/// retention window is swept from disk at startup, and the recovery read surface hides stale
/// entries even before the sweep runs, so the Interrupted list only shows genuinely recent crashes.
/// </summary>
public sealed class DirectorCrashJournalSweepTests : IDisposable
{
    private readonly string _dir;

    public DirectorCrashJournalSweepTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crash-journal-sweep-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private string WriteDirtyJournal(string directorId, int pid, DateTimeOffset lastUpdated)
    {
        var data = new DirectorCrashJournalData
        {
            DirectorId = directorId,
            Pid = pid,
            MachineName = "TESTBOX",
            User = "tester",
            StartedAtUtc = lastUpdated.AddHours(-1),
            LastUpdatedUtc = lastUpdated,
            Sessions = new List<DirectorCrashJournalSession>
            {
                new() { SessionId = Guid.NewGuid().ToString(), Name = "s", RepoPath = @"C:\r" },
            },
        };
        var path = Path.Combine(_dir, $"{directorId}.{pid}.dirty.json");
        File.WriteAllText(path, JsonSerializer.Serialize(data));
        return path;
    }

    [Fact]
    public void SweepExpired_removes_old_journals_and_keeps_recent_ones()
    {
        var oldPath = WriteDirtyJournal("old-dir", 111, DateTimeOffset.UtcNow.AddDays(-30));
        var recentPath = WriteDirtyJournal("recent-dir", 222, DateTimeOffset.UtcNow.AddDays(-1));

        var deleted = DirectorCrashJournal.SweepExpired(directory: _dir);

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(oldPath));
        Assert.True(File.Exists(recentPath));
    }

    [Fact]
    public void SweepExpired_honors_a_custom_max_age()
    {
        WriteDirtyJournal("two-day", 333, DateTimeOffset.UtcNow.AddDays(-2));

        // A one-day window makes the two-day-old journal expired.
        var deleted = DirectorCrashJournal.SweepExpired(maxAge: TimeSpan.FromDays(1), directory: _dir);

        Assert.Equal(1, deleted);
    }

    [Fact]
    public void ListPendingRecoveries_hides_journals_older_than_retention()
    {
        WriteDirtyJournal("old-dir", 111, DateTimeOffset.UtcNow.AddDays(-30));
        WriteDirtyJournal("recent-dir", 222, DateTimeOffset.UtcNow.AddDays(-1));

        var pending = DirectorCrashJournal.ListPendingRecoveries(_dir);

        // The 30-day-old journal is hidden even though it is still on disk (the sweep may not
        // have run); only the recent one shows.
        Assert.Single(pending);
        Assert.Equal("recent-dir", pending[0].DirectorId);
    }
}
