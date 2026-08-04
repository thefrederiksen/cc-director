using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The installer must wait for the launcher to be READY, not for a clock to run out.
///
/// This is the guard for issue #1152. On a clean Windows 11 machine the installer allowed about twenty
/// seconds for the launcher to answer, then painted a red ERROR and a Failed row on the last screen of
/// a first install. The launcher had not failed: while that error was on screen, the very process the
/// installer had started was healthy. cc-launcher.exe is a ~134 MB single-file binary that unpacks
/// itself on first run, so it is slow exactly once - on the machine where a first-time user is
/// watching. Pressing Retry completed the install with no error at all.
///
/// A bigger fixed number would be the same defect with a longer fuse, so what is pinned here is the
/// SHAPE: keep waiting while the started process is alive and the registration has not certified, and
/// stop at once when the started process is gone, because then no registration is ever coming. The
/// transport is now the registration FILE the launcher writes (its listener is deleted -
/// remove-the-network-port mission, phase 6); every rule survives the transport unchanged.
/// </summary>
public sealed class LauncherReadinessWaitTests : IDisposable
{
    private const string HealthyBody = """{"pid":11216,"version":"1.9.0","startedAtUtc":"2026-08-03T00:00:00Z"}""";
    private static readonly TimeSpan Instant = TimeSpan.FromMilliseconds(1);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "launcher-readiness-tests", Guid.NewGuid().ToString("N"));
    private string RegistrationPath => Path.Combine(_dir, "launcher.json");

    public LauncherReadinessWaitTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// The incident, as data. The launcher stays unregistered for sixty polls - at the installer's
    /// one-second cadence that is a minute, well past the thirty-seven seconds measured in the
    /// incident and three times what the old twenty-second allowance permitted - and then registers as
    /// the process the installer started. The wait must report HEALTHY.
    ///
    /// Stated plainly, because a test that cannot fail is worse than no test: the poll interval is
    /// compressed here, so this pins the SHAPE (a late registration is still a healthy one, and the
    /// loop does not stop early on some clock of its own). What pins the incident's actual number is
    /// <see cref="TheInstallersCeiling_IsFarBeyondTheColdStartThatWasCalledDead"/>, which reads the
    /// ceiling the installer really uses.
    /// </summary>
    [Fact]
    public async Task ASlowFirstStartThatEventuallyRegisters_IsHealthy_NotFailed()
    {
        var uncertifiedPolls = 0;

        var result = await LauncherHealthProbe.WaitForReadyAsync(
            RegistrationPath, expectedVersion: "1.9.0", expectedPid: 11216,
            starterIsRunning: () => true, ceiling: TimeSpan.FromMinutes(5), ct: default,
            pollInterval: Instant,
            onStillWaiting: _ =>
            {
                // Registration appears only after 60 empty polls - the slow cold start, as a script.
                if (++uncertifiedPolls == 60) File.WriteAllText(RegistrationPath, HealthyBody);
            },
            processIsAlive: _ => true);

        Assert.Equal(LauncherWaitStop.Healthy, result.Stop);
        Assert.Equal(11216, result.Health!.Pid);
        Assert.Equal(60, uncertifiedPolls);
    }

    /// <summary>
    /// The other half of the rule: a launcher that is merely slow keeps being waited for. While the
    /// process is alive and the ceiling has not elapsed, the wait keeps polling - it does not stop on
    /// any smaller clock of its own.
    /// </summary>
    [Fact]
    public async Task WhileTheStartedProcessIsAlive_ThePollingContinuesToTheCeiling()
    {
        var polls = 0;

        var result = await LauncherHealthProbe.WaitForReadyAsync(
            RegistrationPath, expectedVersion: "1.9.0", expectedPid: 11216,
            starterIsRunning: () => true, ceiling: TimeSpan.FromMilliseconds(1500), ct: default,
            pollInterval: Instant, onStillWaiting: _ => polls++);

        Assert.Equal(LauncherWaitStop.CeilingReached, result.Stop);
        Assert.Null(result.Health);
        Assert.True(polls > 20,
            $"Only {polls} polls before the wait ended - it stopped on something other than the ceiling.");
    }

    /// <summary>
    /// "Give up only when something is genuinely wrong." A process that has exited will never register,
    /// so the wait ends immediately rather than burning a five-minute ceiling on a certainty.
    ///
    /// Revert-proof: drop the liveness term and this hangs for the whole ceiling, so it goes red on the
    /// elapsed-time assertion rather than passing slowly.
    /// </summary>
    [Fact]
    public async Task WhenTheStartedProcessExits_TheWaitStopsAtOnce()
    {
        var polls = 0;

        var started = DateTime.UtcNow;
        var result = await LauncherHealthProbe.WaitForReadyAsync(
            RegistrationPath, expectedVersion: "1.9.0", expectedPid: 11216,
            starterIsRunning: () => ++polls < 3, ceiling: TimeSpan.FromSeconds(30), ct: default,
            pollInterval: Instant);
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(LauncherWaitStop.StarterExited, result.Stop);
        Assert.Null(result.Health);
        Assert.True(elapsed < TimeSpan.FromSeconds(5),
            $"The wait took {elapsed} for a process that had already exited - it waited out the ceiling instead of the condition.");
    }

    /// <summary>
    /// A launcher that registers and then exits was still observed healthy. Liveness of the STARTER is
    /// read AFTER the poll, so the certifying registration wins over a starter handle that has since
    /// gone away.
    /// </summary>
    [Fact]
    public async Task ARegistrationThatCertifies_WinsOverAStarterThatHasSinceExited()
    {
        File.WriteAllText(RegistrationPath, HealthyBody);

        var result = await LauncherHealthProbe.WaitForReadyAsync(
            RegistrationPath, expectedVersion: "1.9.0", expectedPid: 11216,
            starterIsRunning: () => false, ceiling: TimeSpan.FromMinutes(5), ct: default,
            pollInterval: Instant, processIsAlive: _ => true);

        Assert.Equal(LauncherWaitStop.Healthy, result.Stop);
    }

    /// <summary>
    /// Waiting longer must not weaken the identity rule from issue #2042: a pre-existing launcher's
    /// registration is still polled past (the real launcher may yet rewrite it) and still reported as
    /// what was found, never as a certified install.
    /// </summary>
    [Fact]
    public async Task APreExistingLaunchersRegistration_NeverCertifies_AndIsReportedAsWhatWasFound()
    {
        File.WriteAllText(RegistrationPath,
            """{"pid":34084,"version":"1.9.0","startedAtUtc":"2026-07-29T05:00:00Z"}""");

        var result = await LauncherHealthProbe.WaitForReadyAsync(
            RegistrationPath, expectedVersion: "1.9.0", expectedPid: 11216,
            starterIsRunning: () => true, ceiling: TimeSpan.FromMilliseconds(300), ct: default,
            pollInterval: Instant, processIsAlive: _ => true);

        Assert.Equal(LauncherWaitStop.CeilingReached, result.Stop);
        Assert.Equal(34084, result.Health!.Pid);
    }

    [Fact]
    public async Task ACancelledInstall_SaysSo_RatherThanBlamingTheLauncher()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var result = await LauncherHealthProbe.WaitForReadyAsync(
            RegistrationPath, expectedVersion: "1.9.0", expectedPid: 11216,
            starterIsRunning: () => true, ceiling: TimeSpan.FromMinutes(10), ct: cts.Token,
            pollInterval: Instant);

        Assert.Equal(LauncherWaitStop.Cancelled, result.Stop);
    }

    /// <summary>
    /// The caller can tell the user this is a slow start rather than a frozen screen: every poll that
    /// did not certify reports the elapsed time back.
    /// </summary>
    [Fact]
    public async Task WhileWaiting_TheCallerIsToldHowLongItHasBeen()
    {
        var notes = new List<TimeSpan>();

        await LauncherHealthProbe.WaitForReadyAsync(
            RegistrationPath, expectedVersion: "1.9.0", expectedPid: 11216,
            starterIsRunning: () => true, ceiling: TimeSpan.FromMinutes(5), ct: default,
            pollInterval: Instant,
            onStillWaiting: elapsed =>
            {
                notes.Add(elapsed);
                if (notes.Count == 5) File.WriteAllText(RegistrationPath, HealthyBody);
            },
            processIsAlive: _ => true);

        Assert.Equal(5, notes.Count);
    }

    /// <summary>
    /// The ceiling the Windows installer actually uses. It is a backstop for a wedged launcher, so it
    /// must sit far above any plausible cold first start - the twenty seconds that failed a clean
    /// install must not be able to creep back in as "generous enough".
    /// </summary>
    [Fact]
    public void TheInstallersCeiling_IsFarBeyondTheColdStartThatWasCalledDead() =>
        Assert.True(LauncherTrayInstaller.FirstStartHealthCeiling >= TimeSpan.FromMinutes(3),
            $"The launcher readiness ceiling is {LauncherTrayInstaller.FirstStartHealthCeiling}. A clean "
            + "install was already failed at 20 seconds while the launcher was healthy (#1152).");
}
