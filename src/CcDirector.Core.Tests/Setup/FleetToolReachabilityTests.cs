using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CcDirector.Core.Setup;
using Xunit;

namespace CcDirector.Core.Tests.Setup;

/// <summary>
/// The check answers "can a session I spawn actually drive me?", so these tests drive it with a stand-in
/// tool whose exit code they control. That is the whole discriminator: the Director reads the exit code
/// and never asks the tool to judge itself.
///
/// A note on what the fixture can and cannot distinguish: a stand-in that exits 0 proves the PASS path
/// and nothing about authentication, because the fake never authenticates. What authentication failure
/// looks like from here is exactly what a non-zero exit looks like - which is why the real machine proof
/// (a stale tool returning "missing or invalid token", exit 1, against a live Director) is recorded in
/// docs/setup-health-check.md rather than simulated here.
/// </summary>
public class FleetToolReachabilityTests : IDisposable
{
    private readonly List<string> _temporaryFiles = new();

    public void Dispose()
    {
        foreach (var path in _temporaryFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { /* a leftover temp file is not a test failure */ }
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>A runnable stand-in for cc-devthrottle that exits with the code we want to test.</summary>
    private string StubTool(int exitCode, string message)
    {
        var extension = OperatingSystem.IsWindows() ? ".cmd" : ".sh";
        var path = Path.Combine(Path.GetTempPath(), $"fleet-tool-stub-{Guid.NewGuid():N}{extension}");
        var body = OperatingSystem.IsWindows()
            ? $"@echo off\r\necho {message}\r\nexit /b {exitCode}\r\n"
            : $"#!/bin/sh\necho \"{message}\"\nexit {exitCode}\n";
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _temporaryFiles.Add(path);
        return path;
    }

    private static FleetToolReachability WithResolved(string? resolvedPath)
        => new(TimeSpan.FromSeconds(30), _ => resolvedPath);

    /// <summary>
    /// Resolve the bare command name one way and an explicit path another, which is the whole point of
    /// the second probe: PATH's answer and our own copy are different questions with different answers.
    /// </summary>
    private static FleetToolReachability WithPathAndOwn(string? onPath, string? ours)
        => new(TimeSpan.FromSeconds(30),
            command => command == FleetToolReachability.ToolName ? onPath : ours);

    private static readonly string OurBinDir =
        Path.Combine("C:", "cc-director", "instances", "default", "bin");

    [Fact]
    public async Task RunAsync_ToolNotOnPath_ReportsNotFound()
    {
        var check = await WithResolved(null).RunAsync("http://127.0.0.1:7879", expectedBinDir: null);

        Assert.Equal(FleetToolVerdict.NotFound, check.Verdict);
        Assert.Null(check.ResolvedPath);
        Assert.Contains("PATH", check.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ToolReachesDirector_ReportsWorking()
    {
        var stub = StubTool(exitCode: 0, "ok");

        var check = await WithResolved(stub).RunAsync("http://127.0.0.1:7879", expectedBinDir: null);

        Assert.Equal(FleetToolVerdict.Working, check.Verdict);
        Assert.Equal(stub, check.ResolvedPath);
    }

    [Fact]
    public async Task RunAsync_ToolCannotAuthenticate_ReportsCannotReachDirectorWithTheToolsOwnReason()
    {
        // What the stale 1.7.1 build does on a machine whose Director moved to an instance home.
        var stub = StubTool(exitCode: 1, "Error: missing or invalid token");

        var check = await WithResolved(stub).RunAsync("http://127.0.0.1:7879", expectedBinDir: null);

        Assert.Equal(FleetToolVerdict.CannotReachDirector, check.Verdict);
        // The tool's own sentence survives to the panel. Reporting a bare exit code here is what made
        // the original failure undiagnosable from the outside.
        Assert.Contains("missing or invalid token", check.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ToolThatCannotLaunch_IsAFailureNotAPass()
    {
        // A tool too old to accept the arguments, or missing from disk between resolution and launch.
        // Absence of a clean answer must never read as a good one.
        var check = await WithResolved(Path.Combine(Path.GetTempPath(), "definitely-not-here-x9.cmd"))
            .RunAsync("http://127.0.0.1:7879", expectedBinDir: null);

        Assert.Equal(FleetToolVerdict.CannotReachDirector, check.Verdict);
    }

    [Fact]
    public async Task RunAsync_NoControlApiAddress_Throws()
    {
        var reachability = WithResolved("/some/tool");

        await Assert.ThrowsAsync<ArgumentException>(
            () => reachability.RunAsync("", expectedBinDir: null));
    }

    [Fact]
    public async Task RunAsync_PathToolFails_AndOurOwnCopyWorks_IsRepairableByRepointingPath()
    {
        var stale = StubTool(exitCode: 1, "Error: missing or invalid token");
        var ours = StubTool(exitCode: 0, "ok");

        var check = await WithPathAndOwn(onPath: stale, ours: ours).RunAsync("http://127.0.0.1:7879", OurBinDir);

        Assert.Equal(FleetToolVerdict.CannotReachDirector, check.Verdict);
        Assert.Equal(FleetToolVerdict.Working, check.OwnVerdict);
        Assert.True(check.CanRepairByRepointingPath);
        Assert.False(check.OwnToolsAreMissingOrBroken);
    }

    [Fact]
    public async Task RunAsync_PathToolFails_AndWeHaveNoCopyOfOurOwn_IsNotRepairableByRepointingPath()
    {
        // THE FAILURE THIS EXISTS FOR, 2026-08-01 on SOREN_NORTH. PATH resolved a stale install's
        // cc-devthrottle and this Director's own bin directory was EMPTY - its tools had never been
        // installed. The panel saw only "PATH points somewhere else", offered "Repoint PATH", and the
        // repair put an empty directory in front: resolution fell straight through it to the same
        // stale copy and reported the same "missing or invalid token" it had been pressed to fix.
        //
        // Repointing must not be offered here. There is nothing to point at.
        var stale = StubTool(exitCode: 1, "Error: missing or invalid token");

        var check = await WithPathAndOwn(onPath: stale, ours: null).RunAsync("http://127.0.0.1:7879", OurBinDir);

        Assert.Equal(FleetToolVerdict.CannotReachDirector, check.Verdict);
        Assert.Equal(FleetToolVerdict.NotFound, check.OwnVerdict);
        Assert.False(check.CanRepairByRepointingPath);
        Assert.True(check.OwnToolsAreMissingOrBroken);
        Assert.Contains(OurBinDir, check.OwnDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_PathToolFails_AndOurOwnCopyAlsoFails_IsNotRepairableByRepointingPath()
    {
        // Our tools are installed and broken. Reordering PATH would hand sessions a second copy that
        // fails the same way, so the button must stay away from this one too.
        var stale = StubTool(exitCode: 1, "Error: missing or invalid token");
        var oursBroken = StubTool(exitCode: 1, "ModuleNotFoundError: no module named cc_shared");

        var check = await WithPathAndOwn(onPath: stale, ours: oursBroken)
            .RunAsync("http://127.0.0.1:7879", OurBinDir);

        Assert.Equal(FleetToolVerdict.CannotReachDirector, check.OwnVerdict);
        Assert.False(check.CanRepairByRepointingPath);
        Assert.True(check.OwnToolsAreMissingOrBroken);
        Assert.Contains("ModuleNotFoundError", check.OwnDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_PathToolWorks_LeavesOurOwnCopyUnasked()
    {
        // Nothing is wrong, so there is no second question - and an unasked question must report as
        // Unchecked, never as a pass.
        var working = StubTool(exitCode: 0, "ok");

        var check = await WithPathAndOwn(onPath: working, ours: null).RunAsync("http://127.0.0.1:7879", OurBinDir);

        Assert.Equal(FleetToolVerdict.Working, check.Verdict);
        Assert.Equal(FleetToolVerdict.Unchecked, check.OwnVerdict);
        Assert.False(check.CanRepairByRepointingPath);
        Assert.False(check.OwnToolsAreMissingOrBroken);
    }

    [Fact]
    public async Task RunAsync_NoBinDirOfOurOwn_ReportsOurCopyAsUncheckedRatherThanMissing()
    {
        // A development build has no install directory. "We have no tools" would be a claim the check
        // cannot support, and it would light this banner on every developer machine.
        var stale = StubTool(exitCode: 1, "Error: missing or invalid token");

        var check = await WithPathAndOwn(onPath: stale, ours: null)
            .RunAsync("http://127.0.0.1:7879", expectedBinDir: null);

        Assert.Equal(FleetToolVerdict.Unchecked, check.OwnVerdict);
        Assert.False(check.OwnToolsAreMissingOrBroken);
        Assert.False(check.CanRepairByRepointingPath);
    }

    [Fact]
    public void CanRepairByRepointingPath_OurOwnCopyWorksButPathResolvedItAnyway_IsFalse()
    {
        // Same install, still refused. Repointing PATH at the directory it already resolves repairs
        // nothing, so the fault must not be offered a fix that cannot touch it.
        var check = new FleetToolCheck(
            FleetToolVerdict.CannotReachDirector,
            ResolvedPath: Path.Combine(OurBinDir, "cc-devthrottle.cmd"),
            ExpectedBinDir: OurBinDir,
            Detail: "missing or invalid token",
            OwnVerdict: FleetToolVerdict.Working);

        Assert.False(check.CanRepairByRepointingPath);
    }

    [Fact]
    public void IsDifferentInstall_ResolvedOutsideOurBinDir_IsTrue()
    {
        var check = new FleetToolCheck(
            FleetToolVerdict.CannotReachDirector,
            ResolvedPath: Path.Combine("C:", "cc-director", "bin", "cc-devthrottle.cmd"),
            ExpectedBinDir: Path.Combine("C:", "cc-director", "instances", "default", "bin"),
            Detail: "missing or invalid token");

        Assert.True(check.IsDifferentInstall);
    }

    [Fact]
    public void IsDifferentInstall_ResolvedInsideOurBinDir_IsFalse()
    {
        var binDir = Path.Combine("C:", "cc-director", "instances", "default", "bin");
        var check = new FleetToolCheck(
            FleetToolVerdict.Working,
            ResolvedPath: Path.Combine(binDir, "cc-devthrottle.cmd"),
            ExpectedBinDir: binDir,
            Detail: "reached this Director");

        Assert.False(check.IsDifferentInstall);
    }

    [Fact]
    public void IsDifferentInstall_WithNoBinDirToCompareAgainst_IsFalse()
    {
        // A development build has no install directory of its own. It must not be reported as a
        // different install, because that is a claim the check cannot support - and asserting it
        // would light the badge on every developer machine for a fault that is not there.
        var check = new FleetToolCheck(
            FleetToolVerdict.Working,
            ResolvedPath: Path.Combine("C:", "anywhere", "cc-devthrottle.cmd"),
            ExpectedBinDir: null,
            Detail: "reached this Director");

        Assert.False(check.IsDifferentInstall);
    }

    // ---------- The repointed command line is not a broken one ----------
    //
    // Since phase 2 the tool reaches the fleet through the GATEWAY and deliberately ignores
    // CC_DIRECTOR_API. This probe supplies only CC_DIRECTOR_API, so a perfectly healthy tool answers
    // with the mission's accepted no-Gateway sentence and a non-zero exit. Reading that as
    // CannotReachDirector painted the Tools fault banner and offered install and PATH repairs on a
    // machine whose install was fine.
    //
    // The existing fourteen tests all pass by running stubs that return chosen exit codes; none of them
    // ran a tool that produced this message, which is why the mismatch was invisible to the suite.

    [Fact]
    public async Task A_tool_that_says_there_is_no_gateway_is_not_reported_as_a_director_fault()
    {
        var stub = StubTool(1, "CC_GATEWAY_URL is not set, so there is no Gateway to call.");
        var check = await WithResolved(stub).RunAsync("http://127.0.0.1:1234", null);

        Assert.Equal(FleetToolVerdict.NoGateway, check.Verdict);
        Assert.NotEqual(FleetToolVerdict.CannotReachDirector, check.Verdict);
    }

    [Fact]
    public async Task A_tool_with_no_session_key_is_also_not_a_director_fault()
    {
        var stub = StubTool(1, "CC_GATEWAY_SESSION_KEY is not set, so this session has no credential.");
        var check = await WithResolved(stub).RunAsync("http://127.0.0.1:1234", null);

        Assert.Equal(FleetToolVerdict.NoGateway, check.Verdict);
    }

    [Fact]
    public async Task A_genuinely_broken_tool_is_still_a_director_fault()
    {
        // The classification must not swallow the fault it was built to report. Anything that is NOT
        // the no-Gateway answer still reads as unreachable.
        var stub = StubTool(1, "connection refused");
        var check = await WithResolved(stub).RunAsync("http://127.0.0.1:1234", null);

        Assert.Equal(FleetToolVerdict.CannotReachDirector, check.Verdict);
    }
}
