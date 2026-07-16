using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The Gateway self-update swap is a Windows-only operation (a File.Replace dance plus a detached
/// helper exe). The managed update loop already only calls it when Managed and on Windows; this fact
/// pins the belt-and-suspenders guard inside the updater itself, so no future caller can ever trigger
/// a swap on macOS or Linux. It runs only off Windows (on Windows the method proceeds to the real
/// staging path, which reaches the network); the macOS and Linux runs are where it executes.
/// </summary>
public sealed class GatewayUpdaterOffWindowsTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public GatewayUpdaterOffWindowsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-gwoffwin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task CheckStageAndLaunch_IsNoOpOffWindows()
    {
        if (OperatingSystem.IsWindows()) return;

        // Off Windows the guard returns null before it reads the release or source arguments or touches
        // the network, so passing nulls here is safe and deliberate - the point is that it never gets
        // that far.
        var updater = new GatewayUpdater(_layout);
        var result = await updater.CheckStageAndLaunchAsync(release: null!, source: null!);
        Assert.Null(result);
    }
}
