using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class LauncherRescuerTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;
    private readonly string _binary;
    private readonly string _fresh;

    public LauncherRescuerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-lnrs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
        Directory.CreateDirectory(_layout.LauncherDir);
        _binary = _layout.PathFor(ComponentRegistry.Launcher);
        var freshDir = Path.Combine(_dir, "downloads");
        Directory.CreateDirectory(freshDir);
        _fresh = Path.Combine(freshDir, "cc-launcher-downloaded");
        File.WriteAllText(_fresh, "launcher-FRESH");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private void RecordInstalled(string version)
    {
        var manifest = InstalledManifest.Load(_layout);
        manifest.Set(ComponentRegistry.Launcher.Id, version);
        manifest.Save(_layout);
    }

    private LauncherRescuer Build(
        bool healthy,
        Func<bool>? startSucceeds = null,
        TimeSpan? deadThreshold = null,
        Func<DateTime>? utcNow = null,
        int[]? startCounter = null)
    {
        return new LauncherRescuer(
            _layout,
            deadThreshold: deadThreshold ?? TimeSpan.Zero,
            isHealthy: _ => Task.FromResult(healthy),
            startInstalled: _ =>
            {
                if (startCounter is not null) startCounter[0]++;
                var ok = startSucceeds?.Invoke() ?? false;
                return Task.FromResult(new LauncherInstallResult(ok, ok ? "started" : "did not start", new List<string>()));
            },
            fetchFreshBinary: _ => Task.FromResult<(string, string)?>((_fresh, "0.9.9")),
            utcNow: utcNow ?? (() => DateTime.UtcNow));
    }

    [Fact]
    public async Task Check_HealthyLauncher_DoesNothing()
    {
        var result = await Build(healthy: true).CheckAndRescueAsync();
        Assert.Equal(LauncherRescueOutcome.Healthy, result.Outcome);
    }

    [Fact]
    public async Task Check_NeverInstalled_SkipsWithoutInstalling()
    {
        // Rescue only, never install: no installed record means the machine opted out.
        var result = await Build(healthy: false).CheckAndRescueAsync();
        Assert.Equal(LauncherRescueOutcome.Skipped, result.Outcome);
        Assert.False(File.Exists(_binary));
    }

    [Fact]
    public async Task Check_DeadWithBinary_RestartBringsItBack_NoReplacement()
    {
        RecordInstalled("0.9.0");
        File.WriteAllText(_binary, "launcher-INSTALLED");

        var result = await Build(healthy: false, startSucceeds: () => true).CheckAndRescueAsync();

        Assert.Equal(LauncherRescueOutcome.Restarted, result.Outcome);
        Assert.Equal("launcher-INSTALLED", File.ReadAllText(_binary)); // bytes untouched
    }

    [Fact]
    public async Task Check_DeadBelowThreshold_Observes()
    {
        RecordInstalled("0.9.0");
        File.WriteAllText(_binary, "launcher-INSTALLED");

        var rescuer = Build(healthy: false, startSucceeds: () => false, deadThreshold: TimeSpan.FromMinutes(10));
        var result = await rescuer.CheckAndRescueAsync();

        Assert.Equal(LauncherRescueOutcome.Observing, result.Outcome);
        Assert.Equal("launcher-INSTALLED", File.ReadAllText(_binary)); // nothing replaced yet
    }

    [Fact]
    public async Task Check_DeadPastThreshold_ReplacesAndKeepsBackup()
    {
        RecordInstalled("0.9.0");
        File.WriteAllText(_binary, "launcher-INSTALLED");

        // First start attempt fails (the dead launcher), the start after placement succeeds.
        var starts = new int[1];
        var rescuer = Build(
            healthy: false,
            startSucceeds: () => starts[0] >= 2,
            deadThreshold: TimeSpan.Zero,
            startCounter: starts);
        var result = await rescuer.CheckAndRescueAsync();

        Assert.Equal(LauncherRescueOutcome.Replaced, result.Outcome);
        Assert.Equal("launcher-FRESH", File.ReadAllText(_binary));
        Assert.Equal("launcher-INSTALLED", File.ReadAllText(_binary + ".old")); // backup kept
        Assert.Equal("0.9.9", InstalledManifest.Load(_layout).Get(ComponentRegistry.Launcher.Id));
    }

    [Fact]
    public async Task Check_MissingBinary_ReplacesImmediately()
    {
        RecordInstalled("0.9.0"); // installed record, but the binary is gone (damaged install)

        var rescuer = Build(healthy: false, startSucceeds: () => true, deadThreshold: TimeSpan.FromHours(1));
        var result = await rescuer.CheckAndRescueAsync();

        // The one-hour threshold does not delay a missing binary: there is nothing to observe.
        Assert.Equal(LauncherRescueOutcome.Replaced, result.Outcome);
        Assert.Equal("launcher-FRESH", File.ReadAllText(_binary));
    }

    [Fact]
    public async Task Check_FreshBuildDeadToo_RollsBackAndPins()
    {
        RecordInstalled("0.9.0");
        File.WriteAllText(_binary, "launcher-INSTALLED");

        var rescuer = Build(healthy: false, startSucceeds: () => false, deadThreshold: TimeSpan.Zero);
        var result = await rescuer.CheckAndRescueAsync();

        Assert.Equal(LauncherRescueOutcome.RolledBack, result.Outcome);
        Assert.Equal("launcher-INSTALLED", File.ReadAllText(_binary)); // .old restored
        Assert.True(PinStore.Load(_layout).IsPinned(ComponentRegistry.Launcher.Id, "0.9.9"));
    }
}
