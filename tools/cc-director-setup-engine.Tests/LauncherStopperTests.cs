using System;
using System.Collections.Generic;
using System.IO;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The guard for the failure that left a Mac unable to install anything.
///
/// The macOS uninstall only asked launchd to boot the launcher service out. That does nothing for a
/// launcher launchd never started - and reported success. The installer's own first-install path
/// created launchers exactly that way, so the product manufactured a process its own uninstaller
/// could not remove, and every later install collided with it on port 7900. The real orphan was
/// still serving that port seventy-three minutes after the tree it ran from had been deleted.
///
/// These tests pin the ORDER and the SCOPE, because both were the fix:
///   - ask the launcher to quit BEFORE anything wipes the token that authorizes the request;
///   - only ever stop a process whose executable is under the install-owned launcher directory;
///   - escalate, because the real orphan ignored a polite request;
///   - and judge success by the PORT, not by what was attempted.
/// </summary>
public sealed class LauncherStopperTests
{
    private static InstallLayout LayoutIn(string root) => new(root);

    private static string MakeRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "stopper-" + Guid.NewGuid().ToString("N"), "cc-director");
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void Stop_NothingOnThePort_SucceedsWithoutTouchingAnything()
    {
        var root = MakeRoot();
        var listed = false;
        var stopper = new LauncherStopper(LayoutIn(root))
        {
            PortInUse = () => false,
            ListLauncherProcesses = () => { listed = true; return []; },
            RequestQuit = (_, _) => throw new InvalidOperationException("must not ask when nothing is running"),
            StopProcess = _ => throw new InvalidOperationException("must not stop anything"),
        };

        var result = stopper.Stop();

        Assert.True(result.PortFree);
        Assert.False(listed);
    }

    // The polite path, and the ORDER that makes it possible: the token must still be on disk, which
    // is only true before a wipe. On the real machine the token file had already been deleted and the
    // shutdown request came back 401.
    [Fact]
    public void Stop_AsksTheLauncherToQuitFirst_WhenTheTokenIsStillOnDisk()
    {
        var root = MakeRoot();
        var stopper = new LauncherStopper(LayoutIn(root));
        Directory.CreateDirectory(Path.GetDirectoryName(stopper.TokenFilePath)!);
        File.WriteAllText(stopper.TokenFilePath, "the-token");

        string? sawToken = null;
        var quitAsked = false;
        var killed = false;
        var running = true;

        var s = new LauncherStopper(LayoutIn(root))
        {
            PortInUse = () => running,
            RequestQuit = (_, token) => { quitAsked = true; sawToken = token; running = false; return true; },
            ListLauncherProcesses = () => [],
            StopProcess = _ => { killed = true; return true; },
        };

        var result = s.Stop();

        Assert.True(quitAsked);
        Assert.Equal("the-token", sawToken);
        Assert.True(result.PortFree);
        Assert.False(killed);   // it went quietly; nothing had to be killed
    }

    // No token (the state a wipe leaves behind) means the polite path is impossible, so the process
    // path must run. This is the orphan case.
    [Fact]
    public void Stop_WithNoToken_StopsTheInstalledProcessInstead()
    {
        var root = MakeRoot();
        var layout = LayoutIn(root);
        var running = true;
        var stopped = new List<int>();

        var s = new LauncherStopper(layout)
        {
            PortInUse = () => running,
            RequestQuit = (_, _) => throw new InvalidOperationException("there is no token to ask with"),
            ListLauncherProcesses = () => [new LauncherProcess(34084, Path.Combine(layout.LauncherDir, "cc-launcher"))],
            StopProcess = pid => { stopped.Add(pid); running = false; return true; },
        };

        var result = s.Stop();

        Assert.True(result.PortFree);
        Assert.Equal([34084], stopped);
    }

    // Scope. A launcher a developer runs from a repository checkout is not ours and must survive.
    // Revert-proof: drop the ExecutablePath filter in Stop and this goes red.
    [Fact]
    public void Stop_NeverTouchesALauncherOutsideTheInstallDirectory()
    {
        var root = MakeRoot();
        var stopped = new List<int>();

        var s = new LauncherStopper(LayoutIn(root))
        {
            PortInUse = () => true,
            RequestQuit = (_, _) => false,
            ListLauncherProcesses = () => [new LauncherProcess(999, @"D:\repos\devthrottle\src\CcDirector.Launcher\bin\cc-launcher.exe")],
            StopProcess = pid => { stopped.Add(pid); return true; },
        };

        var result = s.Stop();

        Assert.Empty(stopped);
        Assert.False(result.PortFree);   // honest: the port is held, and not by us
    }

    // The whole failure was an installer that trusted an attempt. A stop that cannot free the port
    // must report failure, so the uninstall can say a later install will collide.
    // Revert-proof: return the attempt instead of the port state and this goes red.
    [Fact]
    public void Stop_ReportsFailure_WhenTheProcessWasStoppedButThePortIsStillHeld()
    {
        var root = MakeRoot();
        var layout = LayoutIn(root);

        var s = new LauncherStopper(layout)
        {
            PortInUse = () => true,                 // never frees
            RequestQuit = (_, _) => false,
            ListLauncherProcesses = () => [new LauncherProcess(34084, Path.Combine(layout.LauncherDir, "cc-launcher"))],
            StopProcess = _ => true,                // the attempt "succeeded"
        };

        var result = s.Stop();

        Assert.False(result.PortFree);
        Assert.Contains(result.Steps, s2 => s2.Contains("STILL IN USE", StringComparison.Ordinal));
    }
}

/// <summary>
/// A wipe must not destroy the account of how the installation behaved. On the machine investigated,
/// the line explaining a failed autostart registration was in a log deleted while a process still
/// held it open - and on macOS a deleted-but-open file cannot be read from another process without
/// root, so that cause is permanently unknown.
/// </summary>
public sealed class UninstallerPreservesLogsTests
{
    [Fact]
    public void PreserveLogs_CopiesTheLogsOutsideTheRootBeforeItIsDeleted()
    {
        var parent = Path.Combine(Path.GetTempPath(), "wipe-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "cc-director");
        var logs = Path.Combine(root, "logs", "director");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "director-2026-07-29-34084.log"),
            "[LauncherCore] Autostart registration FAILED: the line we lost");

        var steps = new List<string>();
        var errors = new List<string>();
        try
        {
            var kept = new Uninstaller(new InstallLayout(root)).PreserveLogs(root, steps, errors);

            Assert.NotNull(kept);
            Assert.False(kept!.StartsWith(root, StringComparison.OrdinalIgnoreCase),
                "logs kept INSIDE the root would be deleted with it");
            var copied = Path.Combine(kept, "director", "director-2026-07-29-34084.log");
            Assert.True(File.Exists(copied), $"expected the log at {copied}");
            Assert.Contains("the line we lost", File.ReadAllText(copied), StringComparison.Ordinal);
            Assert.Empty(errors);
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    // A log a live process still holds open is the one we most want, so a plain copy is not enough.
    [Fact]
    public void PreserveLogs_CopiesALogThatIsStillOpenForWriting()
    {
        var parent = Path.Combine(Path.GetTempPath(), "wipe-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(parent, "cc-director");
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        var openLog = Path.Combine(logs, "live.log");

        var steps = new List<string>();
        var errors = new List<string>();
        try
        {
            using (var held = new FileStream(openLog, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            using (var writer = new StreamWriter(held) { AutoFlush = true })
            {
                writer.WriteLine("still being written");

                var kept = new Uninstaller(new InstallLayout(root)).PreserveLogs(root, steps, errors);

                Assert.NotNull(kept);
                Assert.Contains("still being written",
                    File.ReadAllText(Path.Combine(kept!, "live.log")), StringComparison.Ordinal);
            }
        }
        finally
        {
            try { Directory.Delete(parent, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    [Fact]
    public void PreserveLogs_NoLogsPresent_IsNotAnError()
    {
        var root = Path.Combine(Path.GetTempPath(), "wipe-" + Guid.NewGuid().ToString("N"), "cc-director");
        Directory.CreateDirectory(root);
        var steps = new List<string>();
        var errors = new List<string>();

        Assert.Null(new Uninstaller(new InstallLayout(root)).PreserveLogs(root, steps, errors));
        Assert.Empty(errors);
    }
}

/// <summary>Reading the launcher's process id back from launchd, so the health check can demand an
/// answer from the process launchd is actually running.</summary>
public sealed class LaunchdPidParseTests
{
    [Fact]
    public void ParseLaunchdPid_ReadsThePidFieldFromARealPrintBlock()
    {
        const string output = """
            com.devthrottle.cc-launcher = {
                active count = 1
                path = /Users/soren/Library/LaunchAgents/com.devthrottle.cc-launcher.plist
                state = running
                program = /Users/soren/Library/Application Support/cc-director/launcher/cc-launcher
                pid = 35158
                immediate reason = speculative
            }
            """;

        Assert.Equal(35158, LauncherMacInstaller.ParseLaunchdPid(output));
    }

    [Fact]
    public void ParseLaunchdPid_NoPidField_IsZero()
    {
        Assert.Equal(0, LauncherMacInstaller.ParseLaunchdPid("state = not running\n"));
        Assert.Equal(0, LauncherMacInstaller.ParseLaunchdPid(""));
        Assert.Equal(0, LauncherMacInstaller.ParseLaunchdPid("Could not find service"));
    }
}
