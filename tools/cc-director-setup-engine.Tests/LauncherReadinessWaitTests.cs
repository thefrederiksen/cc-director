using System.Net;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The installer must wait for the launcher to be READY, not for a clock to run out.
///
/// This is the guard for issue #1152. On a clean Windows 11 machine the installer allowed about twenty
/// seconds for the launcher to answer, then painted a red ERROR and a Failed row on the last screen of
/// a first install. The launcher had not failed: while that error was on screen, port 7900 answered
/// 200 with ok:true from the very process the installer had started. cc-launcher.exe is a ~134 MB
/// single-file binary that unpacks itself on first run, so it is slow exactly once - on the machine
/// where a first-time user is watching. Pressing Retry completed the install with no error at all.
///
/// A bigger fixed number would be the same defect with a longer fuse, so what is pinned here is the
/// SHAPE: keep waiting while the started process is alive and the port has not answered, and stop at
/// once when the started process is gone, because then no answer is ever coming.
/// </summary>
public sealed class LauncherReadinessWaitTests
{
    private const string HealthyBody = """{"ok":true,"version":"1.9.0","pid":11216,"uptimeS":4}""";
    private const string Url = "http://127.0.0.1:7900/healthz";
    private static readonly TimeSpan Instant = TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// The incident, as data. The launcher stays silent for far more polls than the old wait allowed,
    /// then answers from the process the installer started - and the wait must report it HEALTHY.
    ///
    /// Revert-proof: put a fixed clock back in front of the condition and this goes red, because the
    /// answer arrives after the point at which the old code had already given up.
    /// </summary>
    [Fact]
    public async Task ASlowFirstStartThatEventuallyAnswers_IsHealthy_NotFailed()
    {
        // Silent for 60 polls - three times the old twenty-second allowance at its one-second cadence.
        var handler = new ScriptedHealth(silentPolls: 60, thenBody: HealthyBody);
        using var http = new HttpClient(handler);

        var result = await LauncherHealthProbe.WaitForReadyAsync(
            http, Url, expectedVersion: "1.9.0", expectedPid: 11216,
            starterIsRunning: () => true, ceiling: TimeSpan.FromMinutes(5), ct: default,
            pollInterval: Instant);

        Assert.Equal(LauncherWaitStop.Healthy, result.Stop);
        Assert.Equal(11216, result.Health!.Pid);
        Assert.Equal(61, handler.Attempts);
    }

    /// <summary>
    /// The other half of the rule: a launcher that is merely slow keeps being waited for. While the
    /// process is alive and the ceiling has not elapsed, the wait keeps polling - it does not stop on
    /// any smaller clock of its own.
    /// </summary>
    [Fact]
    public async Task WhileTheStartedProcessIsAlive_ThePollingContinuesToTheCeiling()
    {
        var handler = new ScriptedHealth(silentPolls: int.MaxValue, thenBody: HealthyBody);
        using var http = new HttpClient(handler);

        var result = await LauncherHealthProbe.WaitForReadyAsync(
            http, Url, expectedVersion: "1.9.0", expectedPid: 11216,
            starterIsRunning: () => true, ceiling: TimeSpan.FromMilliseconds(1500), ct: default,
            pollInterval: Instant);

        Assert.Equal(LauncherWaitStop.CeilingReached, result.Stop);
        Assert.Null(result.Health);
        Assert.True(handler.Attempts > 20,
            $"Only {handler.Attempts} polls before the wait ended - it stopped on something other than the ceiling.");
    }

    /// <summary>
    /// "Give up only when something is genuinely wrong." A process that has exited will never answer,
    /// so the wait ends immediately rather than burning a five-minute ceiling on a certainty.
    ///
    /// Revert-proof: drop the liveness term and this hangs for the whole ceiling, so it goes red on the
    /// elapsed-time assertion rather than passing slowly.
    /// </summary>
    [Fact]
    public async Task WhenTheStartedProcessExits_TheWaitStopsAtOnce()
    {
        var handler = new ScriptedHealth(silentPolls: int.MaxValue, thenBody: HealthyBody);
        using var http = new HttpClient(handler);
        var polls = 0;

        var started = DateTime.UtcNow;
        var result = await LauncherHealthProbe.WaitForReadyAsync(
            http, Url, expectedVersion: "1.9.0", expectedPid: 11216,
            starterIsRunning: () => ++polls < 3, ceiling: TimeSpan.FromMinutes(10), ct: default,
            pollInterval: Instant);
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal(LauncherWaitStop.StarterExited, result.Stop);
        Assert.Null(result.Health);
        Assert.True(elapsed < TimeSpan.FromSeconds(20),
            $"The wait took {elapsed} for a process that had already exited - it waited out the ceiling instead of the condition.");
    }

    /// <summary>
    /// A launcher that answers and then exits was still healthy. Liveness is read AFTER the poll, so
    /// the certifying answer wins over a process that has since gone away.
    /// </summary>
    [Fact]
    public async Task AnAnswerThatCertifies_WinsOverAProcessThatHasSinceExited()
    {
        var handler = new ScriptedHealth(silentPolls: 0, thenBody: HealthyBody);
        using var http = new HttpClient(handler);

        var result = await LauncherHealthProbe.WaitForReadyAsync(
            http, Url, expectedVersion: "1.9.0", expectedPid: 11216,
            starterIsRunning: () => false, ceiling: TimeSpan.FromMinutes(5), ct: default,
            pollInterval: Instant);

        Assert.Equal(LauncherWaitStop.Healthy, result.Stop);
    }

    /// <summary>
    /// Waiting longer must not weaken the identity rule from issue #2042: a stranger on the port is
    /// still polled past (the real launcher may yet take the port) and still reported as what answered,
    /// never as a certified install.
    /// </summary>
    [Fact]
    public async Task AStrangerOnThePort_NeverCertifies_AndIsReportedAsWhatAnswered()
    {
        var handler = new ScriptedHealth(silentPolls: 0,
            thenBody: """{"ok":true,"version":"1.9.0","pid":34084,"uptimeS":9461}""");
        using var http = new HttpClient(handler);

        var result = await LauncherHealthProbe.WaitForReadyAsync(
            http, Url, expectedVersion: "1.9.0", expectedPid: 11216,
            starterIsRunning: () => true, ceiling: TimeSpan.FromMilliseconds(300), ct: default,
            pollInterval: Instant);

        Assert.Equal(LauncherWaitStop.CeilingReached, result.Stop);
        Assert.Equal(34084, result.Health!.Pid);
    }

    [Fact]
    public async Task ACancelledInstall_SaysSo_RatherThanBlamingTheLauncher()
    {
        var handler = new ScriptedHealth(silentPolls: int.MaxValue, thenBody: HealthyBody);
        using var http = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var result = await LauncherHealthProbe.WaitForReadyAsync(
            http, Url, expectedVersion: "1.9.0", expectedPid: 11216,
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
        var handler = new ScriptedHealth(silentPolls: 5, thenBody: HealthyBody);
        using var http = new HttpClient(handler);
        var notes = new List<TimeSpan>();

        await LauncherHealthProbe.WaitForReadyAsync(
            http, Url, expectedVersion: "1.9.0", expectedPid: 11216,
            starterIsRunning: () => true, ceiling: TimeSpan.FromMinutes(5), ct: default,
            pollInterval: Instant, onStillWaiting: notes.Add);

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

    /// <summary>A launcher that answers only after a scripted number of silent polls, counting attempts.</summary>
    private sealed class ScriptedHealth(int silentPolls, string thenBody) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;
            // A port nothing is listening on refuses the connection; that is what a cold start looks like.
            if (Attempts <= silentPolls) throw new HttpRequestException("connection refused");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(thenBody),
            });
        }
    }
}
