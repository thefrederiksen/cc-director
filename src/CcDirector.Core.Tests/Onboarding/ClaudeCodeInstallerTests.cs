using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Onboarding;
using Xunit;

namespace CcDirector.Core.Tests.Onboarding;

/// <summary>
/// Tests for <see cref="ClaudeCodeInstaller"/>. The real installer runs the official claude.ai
/// script; these pin the invocation shape and the exit-code contract through the process seam,
/// with no network and no process.
/// </summary>
public sealed class ClaudeCodeInstallerTests
{
    [Fact]
    public void BuildStartInfo_RunsTheOfficialInstaller_ForThisPlatform()
    {
        var psi = ClaudeCodeInstaller.BuildStartInfo();

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("powershell.exe", psi.FileName);
            Assert.Contains(ClaudeCodeInstaller.WindowsInstallCommand, psi.Arguments);
        }
        else
        {
            Assert.Equal("/bin/bash", psi.FileName);
            Assert.Contains(ClaudeCodeInstaller.UnixInstallCommand, psi.Arguments);
        }

        // The wizard must be able to stream the script's own words as progress.
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.False(psi.UseShellExecute);
    }

    /// <summary>Synchronous IProgress so the test observes reports deterministically -
    /// Progress&lt;T&gt; posts to the thread pool, which would make this assertion a race.</summary>
    private sealed class ImmediateProgress : IProgress<string>
    {
        public List<string> Lines { get; } = new();
        public void Report(string value) => Lines.Add(value);
    }

    [Fact]
    public async Task InstallAsync_ExitZero_IsSuccess_AndStreamsProgress()
    {
        var progress = new ImmediateProgress();
        var installer = new ClaudeCodeInstaller
        {
            RunProcessSeam = (_, p, _) =>
            {
                p.Report("Downloading Claude Code...");
                p.Report("Installed to ~/.local/bin");
                return Task.FromResult(0);
            },
        };

        var result = await installer.InstallAsync(progress);

        Assert.True(result.Success);
        Assert.Equal(new[] { "Downloading Claude Code...", "Installed to ~/.local/bin" }, progress.Lines);
    }

    [Fact]
    public async Task InstallAsync_NonZeroExit_IsFailure_NamingTheCode()
    {
        var installer = new ClaudeCodeInstaller { RunProcessSeam = (_, _, _) => Task.FromResult(7) };

        var result = await installer.InstallAsync(new Progress<string>(_ => { }));

        Assert.False(result.Success);
        Assert.Contains("7", result.Message);
    }

    [Fact]
    public async Task InstallAsync_ProcessLaunchFailure_IsFailure_WithTheReason()
    {
        var installer = new ClaudeCodeInstaller
        {
            RunProcessSeam = (_, _, _) => throw new InvalidOperationException("powershell not found"),
        };

        var result = await installer.InstallAsync(new Progress<string>(_ => { }));

        Assert.False(result.Success);
        Assert.Contains("powershell not found", result.Message);
    }

    [Fact]
    public async Task InstallAsync_Cancellation_Propagates()
    {
        var installer = new ClaudeCodeInstaller
        {
            RunProcessSeam = (_, _, ct) => Task.FromCanceled<int>(ct),
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => installer.InstallAsync(new Progress<string>(_ => { }), cts.Token));
    }
}
