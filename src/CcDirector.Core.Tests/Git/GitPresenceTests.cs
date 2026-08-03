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
    /// The banner must be what the program LEADS with, not a phrase buried in its output. A warning
    /// that merely mentions the words would otherwise be accepted as a working git, and the sentence
    /// stored as the version would be the warning.
    /// </summary>
    [Theory]
    [InlineData("warning: git version could not be determined")]
    [InlineData("note: run --help for the git version banner")]
    public async Task MentionsTheWordsWithoutLeadingWithThem_IsUndetermined(string output)
    {
        var presence = await Detect("C:\\decoy\\git.exe", (_, _) => Answers(true, 0, output));

        Assert.Equal(GitAvailability.Undetermined, presence.Availability);
        Assert.Null(presence.Version);
    }

    /// <summary>
    /// The caller asking to stop is not a verdict about the machine. It has to come back out as a
    /// cancellation, not as a confident-looking "we could not tell".
    /// </summary>
    [Fact]
    public async Task CallerCancellation_Propagates_RatherThanBecomingAVerdict()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // The REAL detector, not the injected probe: the catch that has to let cancellation through
        // lives inside the real version probe, so a test that supplies its own probe never reaches
        // it and would pass whatever that catch did.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GitPresenceDetector.DetectAsync(cts.Token));
    }

    /// <summary>
    /// The one ruling the user interface is allowed to read. Stated as a table so that a later change
    /// making Undetermined advise an install fails here rather than on somebody's screen.
    /// </summary>
    [Theory]
    [InlineData(GitAvailability.Present, false)]
    [InlineData(GitAvailability.NotFound, true)]
    [InlineData(GitAvailability.Undetermined, false)]
    public void OnlyGitFailingToResolveOnPathAdvisesInstallingGit(GitAvailability availability, bool expected)
    {
        var presence = new GitPresence(availability, null, null, "");

        Assert.Equal(expected, presence.ShouldAdviseInstallingGit);
    }

    /// <summary>
    /// "Not installed" is a claim about the machine and is made ONLY for the operating system codes
    /// that mean the file is not there. A refusal to run is a different fact and keeps its own words.
    /// </summary>
    [Fact]
    public void LaunchFailure_NoSuchFile_SaysNotInstalled()
    {
        // Code 2 is ERROR_FILE_NOT_FOUND on Windows and ENOENT on POSIX - the same meaning on both,
        // which is why it is the one code read on every platform.
        var message = GitLaunchFailure.Describe(2, "The system cannot find the file specified");

        Assert.Equal("git is not installed on this machine, or is not on PATH", message);
    }

    /// <summary>
    /// Code 3 is Windows ERROR_PATH_NOT_FOUND but POSIX ESRCH, "no such process", which says nothing
    /// about whether git is installed. Reading it as a missing install would tell a Mac user to
    /// reinstall software that is already on their disk.
    /// </summary>
    [Fact]
    public void LaunchFailure_CodeThree_IsOnlyAMissingFileOnWindows()
    {
        // The platform is passed in rather than read from the machine. On Windows the two rules -
        // the correct one and "code 3 means missing everywhere" - produce identical output, so a
        // test that read the real platform could not fail here however wrong the rule became.
        Assert.Equal(
            "git is not installed on this machine, or is not on PATH",
            GitLaunchFailure.Describe(3, "The system cannot find the path specified", isWindows: true));

        var onPosix = GitLaunchFailure.Describe(3, "No such process", isWindows: false);
        Assert.Equal("git could not be started: No such process", onPosix);
        Assert.DoesNotContain("not installed", onPosix);
    }

    /// <summary>Code 2 means "no such file" on both platforms, so it reads the same on both.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LaunchFailure_CodeTwo_SaysNotInstalledOnEveryPlatform(bool isWindows)
    {
        Assert.Equal(
            "git is not installed on this machine, or is not on PATH",
            GitLaunchFailure.Describe(2, "No such file or directory", isWindows));
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
