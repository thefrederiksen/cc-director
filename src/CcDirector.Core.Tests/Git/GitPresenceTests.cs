using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests.Git;

/// <summary>
/// The git detector behind the setup wizard's Code step (devthrottle_internal issue #1048).
///
/// The behaviour under test is not "does this machine have git" - it is that the detector has THREE
/// answers and never launders the third into one of the other two. A machine we could not read must
/// not be reported as a machine without git, because the product then tells someone something false
/// about their own computer.
/// </summary>
public class GitPresenceTests
{
    private static Task<GitVersionProbe> Answers(bool ran, int exitCode, string output)
        => Task.FromResult(new GitVersionProbe(ran, exitCode, output));

    private static Task<GitPresence> Detect(
        string? resolvesTo,
        Func<string, CancellationToken, Task<GitVersionProbe>>? probe = null)
        => GitPresenceDetector.DetectAsync(
            _ => resolvesTo,
            probe ?? ((_, _) => Answers(true, 0, "git version 2.45.1.windows.1")),
            CancellationToken.None);

    [Fact]
    public async Task NothingOnPath_IsNotFound()
    {
        var presence = await Detect(resolvesTo: null);

        Assert.Equal(GitAvailability.NotFound, presence.Availability);
        Assert.True(presence.ShouldAdviseInstallingGit);
    }

    [Fact]
    public async Task ResolvesAndReportsItsVersion_IsPresent()
    {
        var presence = await Detect("C:\\Program Files\\Git\\cmd\\git.exe");

        Assert.Equal(GitAvailability.Present, presence.Availability);
        Assert.False(presence.ShouldAdviseInstallingGit);
        Assert.Equal("git version 2.45.1.windows.1", presence.Version);
    }

    /// <summary>
    /// A FILE EXISTING IS NOT A WORKING INSTALL. A stale shim, a zero-byte placeholder or a
    /// half-removed install still resolves on PATH; only running it settles the question.
    /// </summary>
    [Fact]
    public async Task ResolvesButWillNotRun_IsUndetermined_AndSaysNothing()
    {
        var presence = await Detect(
            "C:\\stale\\git.exe",
            (_, _) => Answers(ran: false, exitCode: -1, "The system cannot find the file specified"));

        Assert.Equal(GitAvailability.Undetermined, presence.Availability);
        Assert.False(presence.ShouldAdviseInstallingGit);
    }

    [Fact]
    public async Task ResolvesButExitsNonZero_IsUndetermined_AndSaysNothing()
    {
        var presence = await Detect("C:\\broken\\git.exe", (_, _) => Answers(true, 128, "fatal: something"));

        Assert.Equal(GitAvailability.Undetermined, presence.Availability);
        Assert.False(presence.ShouldAdviseInstallingGit);
    }

    /// <summary>
    /// Exit zero says SOMETHING ran, not that git ran. Anything on PATH under the name "git" that
    /// exits cleanly would otherwise be accepted as a working git install.
    /// </summary>
    [Fact]
    public async Task ExitsZeroWithoutIdentifyingItself_IsUndetermined_AndSaysNothing()
    {
        var presence = await Detect("C:\\decoy\\git.exe", (_, _) => Answers(true, 0, "usage: helper [options]"));

        Assert.Equal(GitAvailability.Undetermined, presence.Availability);
        Assert.False(presence.ShouldAdviseInstallingGit);
    }

    /// <summary>
    /// The one ruling the user interface is allowed to read. Stated as a table so that a later change
    /// making Undetermined advise an install fails here rather than on somebody's screen.
    /// </summary>
    [Theory]
    [InlineData(GitAvailability.Present, false)]
    [InlineData(GitAvailability.NotFound, true)]
    [InlineData(GitAvailability.Undetermined, false)]
    public void OnlyADefiniteAbsenceAdvisesInstallingGit(GitAvailability availability, bool expected)
    {
        var presence = new GitPresence(availability, null, null, "");

        Assert.Equal(expected, presence.ShouldAdviseInstallingGit);
    }

    /// <summary>
    /// "Not installed" is a claim about the machine and is made ONLY for the operating system codes
    /// that mean the file is not there. A refusal to run is a different fact and keeps its own words.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void LaunchFailure_FileNotFoundCodes_SayNotInstalled(int nativeErrorCode)
    {
        var message = GitLaunchFailure.Describe(nativeErrorCode, "The system cannot find the file specified");

        Assert.Equal("git is not installed on this machine, or is not on PATH", message);
    }

    [Fact]
    public void LaunchFailure_AnyOtherCode_KeepsTheRealReason()
    {
        // 5 is ERROR_ACCESS_DENIED: git is right there, and calling that "not installed" would send
        // the user off to reinstall something that is already on the machine.
        var message = GitLaunchFailure.Describe(5, "Access is denied");

        Assert.Equal("git could not be started: Access is denied", message);
        Assert.DoesNotContain("not installed", message);
    }
}
