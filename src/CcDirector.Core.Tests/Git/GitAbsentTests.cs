using CcDirector.Core.Git;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Git;

/// <summary>
/// What happens when git is not on the machine at all (devthrottle_internal issue #1048).
///
/// These run a REAL subprocess launch against an executable name that resolves nowhere, so the
/// operating system fails it for exactly the reason it fails on a clean Windows install: Process.Start
/// throws Win32Exception with ERROR_FILE_NOT_FOUND. It does NOT return false - which is why the
/// missing-git path had never been exercised and why every one of these call sites let the exception
/// out to callers written against a result object.
///
/// The assertion in every case is the same: a RESULT that says why, never an exception.
/// </summary>
public class GitAbsentTests
{
    /// <summary>A command name no machine has. Deliberately not a path, so PATH resolution is what fails.</summary>
    private const string NoSuchExecutable = "devthrottle-no-such-git-executable";

    private static string ADirectoryThatExists() => Path.GetTempPath();

    [Fact]
    public async Task GitCommandRunner_WithNoGit_ReturnsAFailedResultRatherThanThrowing()
    {
        var runner = new GitCommandRunner(NoSuchExecutable);

        var result = await runner.RunAsync(ADirectoryThatExists(), new[] { "worktree", "list", "--porcelain" });

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("git is not installed on this machine, or is not on PATH", result.Error);
    }

    /// <summary>
    /// The worktree inventory is what the Source Control tab's Worktrees page renders. With no git it
    /// must come back as an explicit failure carrying the reason, not as an empty list - an empty list
    /// would render as "this repository has no worktrees", which is a statement nothing established.
    /// </summary>
    [Fact]
    public async Task WorktreeInventory_WithNoGit_FailsWithTheReasonRatherThanLookingEmpty()
    {
        var service = new WorktreeInventoryService(new GitCommandRunner(NoSuchExecutable));

        var inventory = await service.GetInventoryAsync(ADirectoryThatExists(), fetchPrune: false);

        Assert.False(inventory.Success);
        Assert.Empty(inventory.Worktrees);
        Assert.Contains("git is not installed on this machine, or is not on PATH", inventory.Error);
    }

    [Fact]
    public async Task GitWriteService_Commit_WithNoGit_ReturnsAFailedResultRatherThanThrowing()
    {
        var service = new GitWriteService(NoSuchExecutable);

        var result = await service.CommitAsync(ADirectoryThatExists(), "a message");

        Assert.False(result.Success);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("git is not installed on this machine, or is not on PATH", result.Error);
    }

    [Fact]
    public async Task GitWriteService_Stage_WithNoGit_ReturnsAFailedResultRatherThanThrowing()
    {
        var service = new GitWriteService(NoSuchExecutable);

        var result = await service.StageAsync(ADirectoryThatExists(), Array.Empty<string>());

        Assert.False(result.Success);
        Assert.Equal("git is not installed on this machine, or is not on PATH", result.Error);
    }

    /// <summary>
    /// ProcessRunner documents that Started is false when the process could not start. Before this it
    /// could not be: Start throws for a missing executable rather than returning false, so the branch
    /// naming that case was unreachable and each caller carried its own catch-all instead.
    /// </summary>
    [Fact]
    public async Task ProcessRunner_WithAMissingExecutable_ReportsNotStartedWithTheOperatingSystemCode()
    {
        var result = await ProcessRunner.RunAsync(NoSuchExecutable, new[] { "--version" }, ADirectoryThatExists());

        Assert.False(result.Started);
        Assert.Equal(-1, result.ExitCode);
        // 2 is ERROR_FILE_NOT_FOUND. Carrying the code is what lets a caller say "not installed"
        // without guessing at the wording of an operating system message.
        Assert.Equal(2, result.StartErrorCode);
    }

    /// <summary>
    /// The detector against the real machine. Every other test here injects the two machine-touching
    /// steps; this one runs neither injected, so PATH resolution, the subprocess and the version
    /// banner are all exercised together. It asserts Present because the machines this suite runs on
    /// have git - a red here means either git really has gone missing or the detector has stopped
    /// recognising a working one, and both are worth being told about.
    /// </summary>
    [Fact]
    public async Task GitPresenceDetector_OnThisMachine_ReachesADefiniteVerdict()
    {
        // This machine HAS git, so the detector must say so. The value of running it for real is that
        // it exercises the whole path - PATH resolution, the subprocess, the version banner - which
        // the injected-probe tests above deliberately do not.
        var presence = await GitPresenceDetector.DetectAsync();

        Assert.Equal(GitAvailability.Present, presence.Availability);
        Assert.False(presence.ShouldAdviseInstallingGit);
        Assert.NotNull(presence.ExecutablePath);
        Assert.Contains("git version", presence.Version!, StringComparison.OrdinalIgnoreCase);
    }
}
