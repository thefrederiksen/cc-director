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
}
