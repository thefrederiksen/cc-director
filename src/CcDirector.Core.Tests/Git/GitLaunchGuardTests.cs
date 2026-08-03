using CcDirector.Core.Git;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Git;

/// <summary>
/// What the product does when git cannot be LAUNCHED (devthrottle_internal issue #1048).
///
/// The defect these cover had never once run. On a machine with no git, Process.Start does NOT
/// return false - it THROWS Win32Exception with ERROR_FILE_NOT_FOUND. So the missing-git branch in
/// each of these services was unreachable, and the exception left them by a route none of their
/// callers expects: every one of them is written against a result object carrying Success=false.
///
/// EVERY test here fails the launch by naming an executable that resolves nowhere, so the operating
/// system fails it for exactly the reason it fails on a clean Windows install. These tests therefore
/// run identically on ANY machine, with or without git - which matters, because a machine without
/// git is a supported machine and these are the tests it most needs to keep.
///
/// That sentence was claimed once before and was FALSE: one test failed the launch with a missing
/// working directory instead, so its result depended on whether the host had git, and it reddened a
/// build on a git-less machine. The claim is now verified by a control rather than asserted - the
/// suite is run a second time with git removed from PATH, and must give the identical result with
/// nothing skipped. If you add a test here, run that control before you believe this paragraph.
///
/// The assertion in every case is the same: a RESULT that says why, never an exception.
/// </summary>
public class GitLaunchGuardTests
{
    /// <summary>A command name no machine has. Deliberately not a path, so PATH resolution is what fails.</summary>
    private const string NoSuchExecutable = "devthrottle-no-such-git-executable";

    private const string NotInstalled = "git is not installed on this machine, or is not on PATH";

    private static string ADirectoryThatExists() => Path.GetTempPath();

    [Fact]
    public async Task GitCommandRunner_WhenGitCannotBeLaunched_ReturnsAFailedResultRatherThanThrowing()
    {
        var runner = new GitCommandRunner(NoSuchExecutable);

        var result = await runner.RunAsync(ADirectoryThatExists(), new[] { "worktree", "list", "--porcelain" });

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(NotInstalled, result.Error);
    }

    /// <summary>
    /// The worktree inventory is what the Source Control tab's Worktrees page renders, and it is one
    /// of the callers the exception used to escape into. With no git it must come back as an explicit
    /// failure carrying the reason - an empty list would render as "this repository has no
    /// worktrees", which is a statement nothing established.
    /// </summary>
    [Fact]
    public async Task WorktreeInventory_WhenGitCannotBeLaunched_FailsWithTheReasonRatherThanLookingEmpty()
    {
        var service = new WorktreeInventoryService(new GitCommandRunner(NoSuchExecutable));

        var inventory = await service.GetInventoryAsync(ADirectoryThatExists(), fetchPrune: false);

        Assert.False(inventory.Success);
        Assert.Empty(inventory.Worktrees);
        Assert.Contains(NotInstalled, inventory.Error);
    }

    /// <summary>
    /// Stage, unstage, discard and commit reach here from the desktop Source Control buttons, the
    /// Control API git verbs, and through those the Cockpit and the phone. None of them catches an
    /// exception; all of them read GitWriteResult.Success.
    /// </summary>
    [Fact]
    public async Task GitWriteService_Commit_WhenGitCannotBeLaunched_ReturnsAFailedResult()
    {
        var result = await new GitWriteService(NoSuchExecutable).CommitAsync(ADirectoryThatExists(), "a message");

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(NotInstalled, result.Error);
    }

    [Fact]
    public async Task GitWriteService_Stage_WhenGitCannotBeLaunched_ReturnsAFailedResult()
    {
        var result = await new GitWriteService(NoSuchExecutable).StageAsync(ADirectoryThatExists(), Array.Empty<string>());

        Assert.False(result.Success);
        Assert.Equal(NotInstalled, result.Error);
    }

    /// <summary>
    /// ProcessRunner has always DOCUMENTED a Started=false result for a process that could not start.
    /// It could never produce one: Start throws for a missing executable rather than returning false,
    /// so the branch naming that case was unreachable and every caller carried its own catch-all.
    /// </summary>
    [Fact]
    public async Task ProcessRunner_WithAMissingExecutable_ReportsNotStartedWithTheOperatingSystemCode()
    {
        var result = await ProcessRunner.RunAsync(NoSuchExecutable, new[] { "--version" }, ADirectoryThatExists());

        Assert.False(result.Started);
        Assert.Equal(-1, result.ExitCode);
        // Carrying the CODE is what lets a caller say "not installed" without guessing at the
        // wording of an operating system message, which differs by platform and by language.
        Assert.Equal(2, result.StartErrorCode);
    }

    /// <summary>
    /// The read provider the Source Control view renders. It used to report a fixed "Failed to start
    /// git process", which threw the reason away - and said the same thing when git ran perfectly
    /// well and merely exited non-zero.
    ///
    /// THIS TEST USED TO DEPEND ON THE HOST HAVING GIT, which is the one thing it must not do. It
    /// failed the launch with a missing working DIRECTORY, so what came back depended on which
    /// failure the operating system hit first: with git installed, "the directory name is invalid";
    /// without it, "no such file" - which the rule correctly describes as git not being installed,
    /// and the assertion that the message must NOT say "not installed" then failed. It reddened a
    /// build on exactly the machine this whole change exists to support. It now uses the same seam
    /// as every other test here, so the failure is always the same one, everywhere.
    ///
    /// The assertion it used to carry - that a launch failure which is NOT a missing file keeps its
    /// own words rather than being relabelled "not installed" - has not been lost. It is proved
    /// directly and deterministically by AnyOtherCode_KeepsTheRealReason below, which is where a
    /// rule about error codes belongs anyway.
    /// </summary>
    [Fact]
    public async Task GitStatusProvider_WhenGitCannotBeLaunched_ReportsTheReason()
    {
        // A directory of its own, not the shared temp root: GitStatusProvider keeps a STATIC cache
        // keyed by path, so a sibling test reading the same folder within the cache window could
        // hand this one a cached success instead of the launch failure it is here to observe.
        var ownDirectory = Path.Combine(Path.GetTempPath(), "devthrottle-status-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ownDirectory);
        try
        {
            var result = await new GitStatusProvider(NoSuchExecutable).GetStatusAsync(ownDirectory);

            Assert.False(result.Success);
            Assert.NotEqual("Failed to start git process", result.Error);
            Assert.Equal(NotInstalled, result.Error);
        }
        finally
        {
            try { Directory.Delete(ownDirectory, recursive: true); } catch { /* disposable temp folder */ }
        }
    }

    // ---- The sentence itself -----------------------------------------------------------------

    [Fact]
    public void NoSuchFile_SaysNotInstalled()
    {
        // Code 2 is ERROR_FILE_NOT_FOUND on Windows and ENOENT on POSIX - the same meaning on both.
        Assert.Equal(NotInstalled, GitLaunchFailure.Describe(2, "No such file or directory"));
    }

    /// <summary>
    /// Code 3 is Windows ERROR_PATH_NOT_FOUND but POSIX ESRCH, "no such process", which says nothing
    /// about whether git is installed. The platform is passed in rather than read from the machine:
    /// on Windows the correct rule and "code 3 means missing everywhere" produce identical output, so
    /// a test reading the real platform could not fail here however wrong the rule became.
    /// </summary>
    [Fact]
    public void CodeThree_IsOnlyAMissingFileOnWindows()
    {
        Assert.Equal(NotInstalled, GitLaunchFailure.Describe(3, "The system cannot find the path specified", isWindows: true));

        var onPosix = GitLaunchFailure.Describe(3, "No such process", isWindows: false);
        Assert.Equal("git could not be started: No such process", onPosix);
        Assert.DoesNotContain("not installed", onPosix);
    }

    [Fact]
    public void AnyOtherCode_KeepsTheRealReason()
    {
        // 5 is ERROR_ACCESS_DENIED: git is right there, and calling that "not installed" would send
        // the user off to reinstall something already on the machine.
        var message = GitLaunchFailure.Describe(5, "Access is denied");

        Assert.Equal("git could not be started: Access is denied", message);
        Assert.DoesNotContain("not installed", message);
    }
}
