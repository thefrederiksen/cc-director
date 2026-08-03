using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests.Git;

/// <summary>
/// Apple's shim is TWO states wearing one path, and the detector has to tell them apart WITHOUT
/// running it (devthrottle_internal issue #1048).
///
/// On macOS <c>/usr/bin/git</c> exists on every Mac. With the developer tools installed it dispatches
/// to the real git and behaves exactly like git; without them, running it puts Apple's install dialog
/// on the screen. The first version of this guard could not distinguish the two and refused both -
/// which traded a dialog for permanent silence on the commonest Mac configuration there is. Losing
/// the answer on a machine that HAS git is not the same as staying quiet on one that has not.
///
/// The question "will running the shim prompt?" is answerable by looking at the filesystem, with no
/// process started at all. That is what these tests pin.
/// </summary>
public class GitPresenceMacOsTests
{
    private const string RealGitBehindTheShim = "/Library/Developer/CommandLineTools/usr/bin/git";

    private static Task<GitVersionProbe> AnAppleGitBanner()
        => Task.FromResult(new GitVersionProbe(true, 0, "git version 2.39.5 (Apple Git-154)"));

    /// <summary>
    /// A stock Mac WITH the developer tools. The real git is on disk, so the shim dispatches rather
    /// than prompting - it is safe to run, and the wizard gets a real answer instead of a shrug.
    /// </summary>
    [Fact]
    public async Task MacWithDeveloperTools_IsProbed_AndReportedPresent()
    {
        var probeWasCalled = false;

        var presence = await GitPresenceDetector.DetectAsync(
            _ => GitPresenceDetector.AppleShimPath,
            (_, _) => { probeWasCalled = true; return AnAppleGitBanner(); },
            path => path == RealGitBehindTheShim,
            isMacOs: true,
            CancellationToken.None);

        Assert.True(probeWasCalled, "a Mac with working git was never probed, so the wizard learns nothing about it");
        Assert.Equal(GitAvailability.Present, presence.Availability);
        Assert.Equal("git version 2.39.5 (Apple Git-154)", presence.Version);
    }

    /// <summary>The same, with a full Xcode rather than the standalone Command Line Tools.</summary>
    [Fact]
    public async Task MacWithXcode_IsProbed_AndReportedPresent()
    {
        var probeWasCalled = false;

        var presence = await GitPresenceDetector.DetectAsync(
            _ => GitPresenceDetector.AppleShimPath,
            (_, _) => { probeWasCalled = true; return AnAppleGitBanner(); },
            path => path == "/Applications/Xcode.app/Contents/Developer/usr/bin/git",
            isMacOs: true,
            CancellationToken.None);

        Assert.True(probeWasCalled);
        Assert.Equal(GitAvailability.Present, presence.Availability);
    }

    /// <summary>
    /// A stock Mac WITHOUT the developer tools. Nothing is behind the shim, so running it would
    /// prompt. The assertion that matters is not the verdict - it is that the probe IS NEVER CALLED.
    /// A detector with a side effect on the user's machine is not a detector.
    /// </summary>
    [Fact]
    public async Task MacWithoutDeveloperTools_IsNeverLaunched_AndSaysNothing()
    {
        var probeWasCalled = false;

        var presence = await GitPresenceDetector.DetectAsync(
            _ => GitPresenceDetector.AppleShimPath,
            (_, _) => { probeWasCalled = true; return AnAppleGitBanner(); },
            _ => false,
            isMacOs: true,
            CancellationToken.None);

        Assert.False(probeWasCalled, "Apple's shim was executed with nothing behind it; that can open the install dialog");
        Assert.Equal(GitAvailability.Undetermined, presence.Availability);
        Assert.False(presence.ShouldAdviseInstallingGit);
    }

    /// <summary>
    /// A Mac whose git comes from Homebrew resolves elsewhere. It is not Apple's shim, so the
    /// filesystem question does not arise and it is probed like anything else.
    /// </summary>
    [Fact]
    public async Task MacWithHomebrewGit_IsProbedWhateverTheDeveloperToolsAreDoing()
    {
        var probeWasCalled = false;

        var presence = await GitPresenceDetector.DetectAsync(
            _ => "/opt/homebrew/bin/git",
            (_, _) => { probeWasCalled = true; return Task.FromResult(new GitVersionProbe(true, 0, "git version 2.45.1")); },
            _ => false,
            isMacOs: true,
            CancellationToken.None);

        Assert.True(probeWasCalled);
        Assert.Equal(GitAvailability.Present, presence.Availability);
    }

    /// <summary>
    /// The same path on a machine that is not a Mac is not Apple's shim and carries none of its
    /// behaviour, so no filesystem question is asked and it is probed normally.
    /// </summary>
    [Fact]
    public async Task OffMacOs_TheSamePathIsProbedNormally()
    {
        var probeWasCalled = false;

        await GitPresenceDetector.DetectAsync(
            _ => GitPresenceDetector.AppleShimPath,
            (_, _) => { probeWasCalled = true; return Task.FromResult(new GitVersionProbe(true, 0, "git version 2.45.1")); },
            _ => false,
            isMacOs: false,
            CancellationToken.None);

        Assert.True(probeWasCalled);
    }

    /// <summary>
    /// A caller who has asked to stop gets a cancellation, not a verdict - including on the early
    /// returns, which used to hand back an answer without ever checking.
    /// </summary>
    [Fact]
    public async Task APreCancelledCall_DoesNotReturnAVerdictFromAnEarlyReturn()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GitPresenceDetector.DetectAsync(
                _ => null,   // the NotFound early return, which never awaited anything
                (_, _) => AnAppleGitBanner(),
                _ => false,
                isMacOs: false,
                cts.Token));
    }
}
