using CcDirector.Core.Update;
using CcDirector.Launcher;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Launcher.Tests;

public class DirectorUpdateGuardianTests
{
    private sealed class World
    {
        public DirectorUpdateGuardian.DirectorHealth? Health;
        public string? LatestVersion = "1.1.0";
        public UpdaterState UpdaterState = new();
        public DirectorRestartConfig Config = new(Enabled: true, WindowStartHour: 0, WindowEndHour: 0); // whole day
        public int Restarts;
        public int Starts;
        public int Restores;
        public string? Pinned;
        public DirectorUpdateGuardian.DirectorHealth? HealthAfterRestart;

        public DirectorUpdateGuardian Build()
        {
            var restarted = false;
            return new DirectorUpdateGuardian(
                loadConfig: () => Config,
                probeDirector: _ => Task.FromResult(restarted ? HealthAfterRestart : Health),
                fetchLatestVersion: _ => Task.FromResult(LatestVersion),
                loadUpdaterState: () => UpdaterState,
                restartDirector: _ => { Restarts++; restarted = true; return Task.CompletedTask; },
                startDirector: () => { Starts++; restarted = true; },
                restoreDirectorBackup: () => { Restores++; return true; },
                pinBadVersion: v => Pinned = v,
                localNow: () => new DateTime(2026, 7, 11, 3, 0, 0),
                healthTimeout: TimeSpan.FromMilliseconds(100));
        }
    }

    [Fact]
    public async Task Check_DirectorDown_StartsIt()
    {
        var world = new World { Health = null };
        var result = await world.Build().CheckOnceAsync();
        Assert.Equal(GuardianOutcome.StartedDirector, result.Outcome);
        Assert.Equal(1, world.Starts);
        Assert.Equal(0, world.Restarts);
    }

    [Fact]
    public async Task Check_UpToDate_DoesNothing()
    {
        var world = new World { Health = new("1.1.0", 0) };
        var result = await world.Build().CheckOnceAsync();
        Assert.Equal(GuardianOutcome.UpToDate, result.Outcome);
        Assert.Equal(0, world.Restarts);
    }

    [Fact]
    public async Task Check_NewerButNotStaged_Waits()
    {
        var world = new World { Health = new("1.0.0", 0) }; // nothing staged in UpdaterState
        var result = await world.Build().CheckOnceAsync();
        Assert.Equal(GuardianOutcome.WaitingForStage, result.Outcome);
        Assert.Equal(0, world.Restarts);
    }

    [Fact]
    public async Task Check_PinnedVersion_NeverRestarts()
    {
        var world = new World
        {
            Health = new("1.0.0", 0),
            UpdaterState = new UpdaterState { PinnedBadVersion = "1.1.0", StagedVersion = "1.1.0" },
        };
        var result = await world.Build().CheckOnceAsync();
        Assert.Equal(GuardianOutcome.PinnedBad, result.Outcome);
        Assert.Equal(0, world.Restarts);
        Assert.NotNull(DirectorUpdateGuardian.PendingUpdateNotice);
    }

    [Fact]
    public async Task Check_BusySessions_BlocksAndNotifies()
    {
        var world = new World
        {
            Health = new("1.0.0", BusySessions: 1),
            UpdaterState = new UpdaterState { StagedVersion = "1.1.0" },
        };
        var result = await world.Build().CheckOnceAsync();
        Assert.Equal(GuardianOutcome.Blocked, result.Outcome);
        Assert.Equal(0, world.Restarts);
        Assert.Contains("actively working", DirectorUpdateGuardian.PendingUpdateNotice!);
    }

    [Fact]
    public async Task Check_OutsideWindow_BlocksAndNotifies()
    {
        var world = new World
        {
            Health = new("1.0.0", 0),
            UpdaterState = new UpdaterState { StagedVersion = "1.1.0" },
            Config = new DirectorRestartConfig(Enabled: true, WindowStartHour: 22, WindowEndHour: 23), // now is 03:00
        };
        var result = await world.Build().CheckOnceAsync();
        Assert.Equal(GuardianOutcome.Blocked, result.Outcome);
        Assert.Contains("maintenance window", DirectorUpdateGuardian.PendingUpdateNotice!);
    }

    [Fact]
    public async Task Check_StagedIdleInsideWindow_RestartsAndSucceeds()
    {
        var world = new World
        {
            Health = new("1.0.0", 0),
            UpdaterState = new UpdaterState { StagedVersion = "1.1.0" },
            HealthAfterRestart = new("1.1.0", 0),
        };
        var result = await world.Build().CheckOnceAsync();
        Assert.Equal(GuardianOutcome.Updated, result.Outcome);
        Assert.Equal(1, world.Restarts);
        Assert.Null(DirectorUpdateGuardian.PendingUpdateNotice);
    }

    [Fact]
    public async Task Check_HealthyButStillOldAfterRestart_ReportsWithoutRescue()
    {
        // The Director's own startup logic declined or rolled back the update; the guardian
        // must not fight it.
        var world = new World
        {
            Health = new("1.0.0", 0),
            UpdaterState = new UpdaterState { StagedVersion = "1.1.0" },
            HealthAfterRestart = new("1.0.0", 0),
        };
        var result = await world.Build().CheckOnceAsync();
        Assert.Equal(GuardianOutcome.ApplyDidNotStick, result.Outcome);
        Assert.Equal(0, world.Restores);
        Assert.Null(world.Pinned);
    }

    [Fact]
    public async Task Check_DeadAfterRestart_RestoresBackupAndPins()
    {
        var world = new World
        {
            Health = new("1.0.0", 0),
            UpdaterState = new UpdaterState { StagedVersion = "1.1.0" },
            HealthAfterRestart = null, // never comes back, even after the second start
        };
        var result = await world.Build().CheckOnceAsync();
        Assert.Equal(GuardianOutcome.Dead, result.Outcome); // restore happened but health stayed dead in this fake
        Assert.Equal(1, world.Restores);
        Assert.Equal("1.1.0", world.Pinned);
        Assert.True(world.Starts >= 1); // the second-chance start plus the start after restore
    }
}
