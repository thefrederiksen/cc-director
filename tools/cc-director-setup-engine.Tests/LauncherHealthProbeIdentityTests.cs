using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The installer must certify its install against the launcher IT STARTED, not against whatever
/// launcher's registration happens to be on the machine.
///
/// This is the guard for the failure that bricked a Mac. The installer started process 35158; the
/// health answer came from process 34084, an orphan that had been running for seventy-three minutes
/// from a path the installer had just overwritten. The whole launcher step took twenty-five
/// milliseconds because the answer came from a process that was already up, and the started process
/// logged its first line 1.7 seconds AFTER the verdict had been rendered.
///
/// The version comparison could not catch it: VersionUtil.TryParse strips build metadata, so the
/// orphan's "1.8.4+71f90bad..." and the freshly placed "1.8.4" compare equal. On a same-version
/// reinstall - the common case - the old guard was blind. The identity work behind issue #2042
/// only ever caught a version CHANGE, which is the case that matters least. The transport has since
/// changed - the registration FILE replaced the health route when the launcher's listener was
/// deleted (remove-the-network-port mission, phase 6) - and every identity rule here survives that
/// change, because a pre-existing launcher's registration file is exactly as valid-looking as its
/// health answer was.
/// </summary>
public sealed class LauncherHealthProbeIdentityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "launcher-identity-tests", Guid.NewGuid().ToString("N"));

    public LauncherHealthProbeIdentityTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string OrphanBody =
        """{"pid":34084,"version":"1.8.4+71f90bad0ea1e25cc006159f42c6474bd4263dee","startedAtUtc":"2026-07-29T05:00:00Z","userInterface":"tray"}""";

    private LauncherHealth Reading(string body, bool alive = true)
    {
        var path = Path.Combine(_dir, "launcher.json");
        File.WriteAllText(path, body);
        return LauncherHealthProbe.ReadRegistration(path, processIsAlive: _ => alive)!;
    }

    // The exact reproduction, as data: same version, different process. This must NOT certify.
    // Revert-proof: drop the pid term from Certifies and this goes red.
    [Fact]
    public void Certifies_SameVersionFromADifferentProcess_IsRefused()
    {
        var health = Reading(OrphanBody);

        Assert.True(health.Ok);
        Assert.Equal(34084, health.Pid);
        // Version alone is satisfied - which is exactly why version alone was not enough.
        Assert.True(LauncherHealthProbe.VersionMatches("1.8.4", health.Version));

        Assert.False(
            LauncherHealthProbe.Certifies(health, expectedVersion: "1.8.4", expectedPid: 35158),
            "A registration from a process the installer did not start must never certify the install.");
    }

    [Fact]
    public void Certifies_TheProcessTheInstallerStarted_IsAccepted()
    {
        var health = Reading(OrphanBody);
        Assert.True(LauncherHealthProbe.Certifies(health, expectedVersion: "1.8.4", expectedPid: 34084));
    }

    // A registration carrying no process id cannot be identified, so when the installer knows
    // which process it started, silence is a refusal - not a pass.
    [Fact]
    public void Certifies_ARegistrationWithNoProcessId_IsRefusedWhenOneWasExpected()
    {
        var health = Reading("""{"version":"1.8.4"}""");

        Assert.Equal(0, health.Pid);
        Assert.False(LauncherHealthProbe.Certifies(health, expectedVersion: "1.8.4", expectedPid: 35158));
    }

    // Callers that genuinely have no process to expect (a health check that is not certifying an
    // install) keep the old behaviour.
    [Fact]
    public void Certifies_WithNoProcessExpectation_FallsBackToVersionOnly()
    {
        var health = Reading(OrphanBody);
        Assert.True(LauncherHealthProbe.Certifies(health, expectedVersion: "1.8.4", expectedPid: 0));
        Assert.False(LauncherHealthProbe.Certifies(health, expectedVersion: "1.9.0", expectedPid: 0));
    }

    // A registration whose process is DEAD is a crash leftover, not a launcher - even when it names
    // exactly the process the installer expected.
    [Fact]
    public void Certifies_ADeadProcess_IsRefusedEvenWhenItIsTheRightOne()
    {
        var health = Reading("""{"pid":35158,"version":"1.8.4"}""", alive: false);
        Assert.False(LauncherHealthProbe.Certifies(health, expectedVersion: "1.8.4", expectedPid: 35158));
    }

    [Fact]
    public void PidMatches_IsTheWholeRule()
    {
        Assert.True(LauncherHealthProbe.PidMatches(expected: 0, reported: 34084));   // nothing expected
        Assert.True(LauncherHealthProbe.PidMatches(expected: 35158, reported: 35158));
        Assert.False(LauncherHealthProbe.PidMatches(expected: 35158, reported: 34084));
        Assert.False(LauncherHealthProbe.PidMatches(expected: 35158, reported: 0));
    }
}
