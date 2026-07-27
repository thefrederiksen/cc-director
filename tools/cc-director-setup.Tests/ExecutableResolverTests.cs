using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using CcDirectorSetup.Services;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// The Re-check regression: installing .NET while setup was open and clicking Re-check reported
/// "Not found" forever, while closing and reopening setup on the same machine reported "Found".
///
/// The cause was not the PATH the checker built - that part was already right. It was that
/// <see cref="Process.Start"/> resolves a bare <c>FileName</c> against the PARENT process's PATH
/// and ignores the one placed in <c>psi.Environment</c> for the child. So "where dotnet" found the
/// new runtime and the very next call, "dotnet --list-runtimes", could not start at all.
///
/// <see cref="ExecutableResolver"/> removes the parent PATH from the equation by handing
/// Process.Start an absolute path. These tests pin both halves: that the resolver finds a tool in a
/// directory that is NOT on this process's PATH, and that the framework behaviour the fix exists to
/// route around is real.
/// </summary>
public sealed class ExecutableResolverTests : IDisposable
{
    private readonly string _dir;

    public ExecutableResolverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-setup-resolver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* the temp dir is disposable */ }
    }

    /// <summary>A real, runnable executable in the temp directory, under a name nothing else uses.</summary>
    private string PlaceRealExecutable(string name)
    {
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe");
        Assert.True(File.Exists(source), $"Expected a system executable to copy at {source}");
        var target = Path.Combine(_dir, name);
        File.Copy(source, target);
        return target;
    }

    private string PlaceFile(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void FindIn_FindsARuntimeWhoseDirectoryIsNotOnTheProcessPath()
    {
        var expected = PlaceFile("dotnet.exe");

        // The whole point: this directory is not on the PATH this test process launched with, which
        // is the situation of a user who installed .NET after opening setup.
        Assert.DoesNotContain(_dir, Environment.GetEnvironmentVariable("PATH") ?? "", StringComparison.OrdinalIgnoreCase);

        var resolved = ExecutableResolver.FindIn("dotnet", _dir, [], File.Exists);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void FindIn_FindsAToolInAWellKnownDirectory_WhenItIsOnNoPathAtAll()
    {
        // A freshly installed runtime is on disk before its PATH entry reaches every process, so
        // the extra directories have to be searched even with an empty PATH.
        var expected = PlaceFile("dotnet.exe");

        var resolved = ExecutableResolver.FindIn("dotnet", searchPath: null, extraDirectories: [_dir], File.Exists);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void FindIn_ReturnsNull_WhenTheToolIsNowhere()
    {
        Assert.Null(ExecutableResolver.FindIn("dotnet", _dir, [], File.Exists));
    }

    [Fact]
    public void FindIn_SearchesTheSearchPathBeforeTheWellKnownDirectories()
    {
        var onPath = Path.Combine(_dir, "onpath");
        var wellKnown = Path.Combine(_dir, "wellknown");
        Directory.CreateDirectory(onPath);
        Directory.CreateDirectory(wellKnown);
        File.WriteAllText(Path.Combine(onPath, "dotnet.exe"), "");
        File.WriteAllText(Path.Combine(wellKnown, "dotnet.exe"), "");

        var resolved = ExecutableResolver.FindIn("dotnet", onPath, [wellKnown], File.Exists);

        Assert.Equal(Path.Combine(onPath, "dotnet.exe"), resolved);
    }

    [Fact]
    public void FindIn_FindsAScriptShim_SoAnNpmInstalledAgentIsNotReportedMissing()
    {
        // Claude Code installed through npm lands as claude.cmd, not claude.exe.
        var expected = PlaceFile("claude.cmd");

        Assert.Equal(expected, ExecutableResolver.FindIn("claude", _dir, [], File.Exists));
    }

    [Fact]
    public void FindIn_PrefersTheExecutableOverAShimInTheSameDirectory()
    {
        var exe = PlaceFile("claude.exe");
        PlaceFile("claude.cmd");

        Assert.Equal(exe, ExecutableResolver.FindIn("claude", _dir, [], File.Exists));
    }

    [Fact]
    public void FindIn_SkipsAMalformedPathEntry_AndKeepsSearchingTheRest()
    {
        var expected = PlaceFile("dotnet.exe");

        var resolved = ExecutableResolver.FindIn("dotnet", "C:\\bad|dir\0x;" + _dir, [], File.Exists);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void FindIn_EmptyAndWhitespacePathEntries_AreIgnored()
    {
        var expected = PlaceFile("dotnet.exe");

        Assert.Equal(expected, ExecutableResolver.FindIn("dotnet", $";  ;{_dir};", [], File.Exists));
    }

    /// <summary>
    /// The root cause itself, pinned. If someone reverts the checker to a bare command name, this
    /// test documents exactly why Re-check went blind: a child PATH cannot make Process.Start find
    /// the file, and only the absolute path can.
    /// </summary>
    [Fact]
    public void ProcessStart_IgnoresTheChildPathWhenResolvingTheFileName_ButTakesAnAbsolutePath()
    {
        var absolute = PlaceRealExecutable("ccsetupprobe.exe");
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);

        // Handing the child a PATH that contains the tool is NOT enough - the name is resolved
        // against the parent's PATH, which has never heard of this directory.
        Assert.Throws<System.ComponentModel.Win32Exception>(
            () => Start("ccsetupprobe", $"{system};{_dir}"));

        // The same tool, same child PATH, started by absolute path: works.
        using var byAbsolutePath = Start(absolute, $"{system};{_dir}");
        Assert.NotNull(byAbsolutePath);
        byAbsolutePath!.WaitForExit(10_000);
        Assert.Equal(0, byAbsolutePath.ExitCode);

        // And the resolver hands back exactly that absolute path.
        Assert.Equal(absolute, ExecutableResolver.FindIn("ccsetupprobe", _dir, [], File.Exists));
    }

    private static Process? Start(string fileName, string childPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["PATH"] = childPath;
        return Process.Start(psi);
    }
}
