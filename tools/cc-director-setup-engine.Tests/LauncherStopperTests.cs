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
/// could not remove, and every later install collided with it. The real orphan was still running
/// seventy-three minutes after the tree it ran from had been deleted.
///
/// These tests pin the ORDER and the SCOPE, because both were the fix:
///   - ask the launcher to quit politely first, via the root-scoped lifecycle signal;
///   - only ever stop a process whose executable is under the install-owned launcher directory;
///   - escalate, because the real orphan ignored a polite request;
///   - and judge success by the PROCESSES being gone, not by what was attempted. (The verdict used
///     to include the launcher port; the launcher listens on nothing now - remove-the-network-port
///     mission, phase 6 - so the process scan is the entire fact.)
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
    public void Stop_NothingOfOursRunning_SucceedsWithoutStoppingAnything()
    {
        var root = MakeRoot();
        var listed = false;
        var stopper = new LauncherStopper(LayoutIn(root))
        {
            ListLauncherProcesses = () => { listed = true; return []; },
            RequestQuit = _ => throw new InvalidOperationException("must not ask when nothing of ours is running"),
            StopProcess = _ => throw new InvalidOperationException("must not stop anything"),
        };

        var result = stopper.Stop();

        Assert.True(result.Stopped);
        // It DOES enumerate now, always: a launcher that is starting, failed to bind, or listens on
        // another port holds no port and would otherwise have its files deleted underneath it.
        Assert.True(listed);
    }

    // The polite path. It no longer depends on a token file still being on disk, and that ordering
    // hazard is the reason it is worth restating: the quit used to be a post behind a bearer token, so
    // an uninstall that had already wiped the token got a 401 and force-killed instead. A named signal
    // has nothing to be missing.
    [Fact]
    public void Stop_AsksTheLauncherToQuitFirst()
    {
        var root = MakeRoot();
        string? sawRoot = null;
        var quitAsked = false;
        var killed = false;

        var layout = LayoutIn(root);
        var ourLauncher = new LauncherProcess(4242, Path.Combine(layout.LauncherDir, "cc-launcher") + " --managed");
        var alive = true;

        var s = new LauncherStopper(layout)
        {
            RequestQuit = askedRoot =>
            {
                quitAsked = true; sawRoot = askedRoot; alive = false; return true;
            },
            ListLauncherProcesses = () => alive ? [ourLauncher] : [],
            StopProcess = _ => { killed = true; return true; },
        };

        var result = s.Stop();

        Assert.True(quitAsked);
        // Scoped to the root being uninstalled, which is what keeps a launcher serving a different root
        // from hearing it at all.
        Assert.Equal(root, sawRoot);
        Assert.True(result.Stopped);
        Assert.False(killed);   // it went quietly; nothing had to be killed
    }

    // Nobody listening for the quit signal - a launcher older than this build, or one whose startup
    // never got that far. The process path must still run. This is the orphan case.
    [Fact]
    public void Stop_WhenNothingAnswersTheQuitSignal_StopsTheInstalledProcessInstead()
    {
        var root = MakeRoot();
        var layout = LayoutIn(root);
        var running = true;
        var stopped = new List<int>();

        var s = new LauncherStopper(layout)
        {
            RequestQuit = _ => false,
            ListLauncherProcesses = () => running
                ? [new LauncherProcess(34084, Path.Combine(layout.LauncherDir, "cc-launcher") + " --managed")]
                : [],
            StopProcess = pid => { stopped.Add(pid); running = false; return true; },
        };

        var result = s.Stop();

        Assert.True(result.Stopped);
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
            RequestQuit = _ => throw new InvalidOperationException(
                "must not ask a launcher outside the install to quit - it shares the same token file"),
            ListLauncherProcesses = () => [new LauncherProcess(999, @"D:\repos\devthrottle\src\CcDirector.Launcher\bin\cc-launcher.exe --managed")],
            StopProcess = pid => { stopped.Add(pid); return true; },
        };

        var result = s.Stop();

        Assert.Empty(stopped);
        // Nothing of ours was running, so this did not fail at its job.
        Assert.True(result.Stopped);
        Assert.Contains(result.Steps, x => x.Contains("nothing of ours to stop", StringComparison.Ordinal));
    }

    // The whole failure was an installer that trusted an attempt. A stop whose process survives must
    // report failure, so the uninstall can say a later install will collide.
    // Revert-proof: return the attempt instead of the end-state scan and this goes red.
    [Fact]
    public void Stop_ReportsFailure_WhenTheProcessSurvivesEveryAttempt()
    {
        var root = MakeRoot();
        var layout = LayoutIn(root);

        var s = new LauncherStopper(layout)
        {
            RequestQuit = _ => false,
            // The process survives every attempt - the exact orphan behaviour.
            ListLauncherProcesses = () => [new LauncherProcess(34084, Path.Combine(layout.LauncherDir, "cc-launcher") + " --managed")],
            StopProcess = _ => true,                // the attempt "succeeded"
        };

        var result = s.Stop();

        Assert.False(result.Stopped);
        Assert.Contains(result.Steps, x => x.Contains("STILL RUNNING", StringComparison.Ordinal));
    }

    // FINDING 1, the one that made this whole class useless on the real machine: ps does not quote
    // paths, and the installed launcher lives under "Library/Application Support" - a path with a
    // SPACE. Taking the first whitespace-delimited token as the executable produced
    // "/Users/<user>/Library/Application", which never matched the install directory, so the orphan
    // was never stopped. The command line is matched as a PREFIX instead.
    // Revert-proof: parse an executable out of the command line by splitting on space and this reds.
    [Fact]
    public void Stop_RecognisesTheInstalledLauncher_WhenItsPathContainsSpaces()
    {
        var root = Path.Combine(Path.GetTempPath(), "stopper-" + Guid.NewGuid().ToString("N"),
                                "Application Support", "cc-director");
        Directory.CreateDirectory(root);
        var layout = LayoutIn(root);
        var stopped = new List<int>();
        var alive = true;

        var s = new LauncherStopper(layout)
        {
            RequestQuit = _ => false,
            ListLauncherProcesses = () => alive
                ? [new LauncherProcess(34084, Path.Combine(layout.LauncherDir, "cc-launcher") + " --managed")]
                : [],
            StopProcess = pid => { stopped.Add(pid); alive = false; return true; },
        };

        var result = s.Stop();

        Assert.Equal([34084], stopped);
        Assert.True(result.Stopped);
    }

    // A sibling directory is not us. "<LauncherDir>-dev/cc-launcher" starts with the same text as
    // "<LauncherDir>", so a bare prefix comparison would kill somebody else's launcher.
    [Fact]
    public void Stop_DoesNotTreatASiblingDirectoryAsTheInstallDirectory()
    {
        var root = MakeRoot();
        var layout = LayoutIn(root);
        var stopped = new List<int>();

        var s = new LauncherStopper(layout)
        {
            RequestQuit = _ => throw new InvalidOperationException("not ours"),
            ListLauncherProcesses = () => [new LauncherProcess(777, layout.LauncherDir + "-dev/cc-launcher --managed")],
            StopProcess = pid => { stopped.Add(pid); return true; },
        };

        s.Stop();

        Assert.Empty(stopped);
    }

    // FINDING 4: an installed launcher that is invisible to every rendezvous - still starting, or
    // half-initialized - must still be stopped, because its files are about to be deleted underneath
    // it.
    //
    // It is ASKED first as well: the signal is named for the storage root rather than found through a
    // port, so even a launcher that never finished starting hears it. Here nothing answers, so the
    // process path still runs - which is what this test was written to protect.
    [Fact]
    public void Stop_StopsAnInstalledLauncherThatIsStillStarting()
    {
        var root = MakeRoot();
        var layout = LayoutIn(root);
        var stopped = new List<int>();
        var asked = false;
        var alive = true;

        var s = new LauncherStopper(layout)
        {
            RequestQuit = _ => { asked = true; return false; },
            ListLauncherProcesses = () => alive
                ? [new LauncherProcess(5150, Path.Combine(layout.LauncherDir, "cc-launcher") + " --managed")]
                : [],
            StopProcess = pid => { stopped.Add(pid); alive = false; return true; },
        };

        var result = s.Stop();

        Assert.True(asked, "a launcher holding no port must still be asked politely before it is killed");
        Assert.Equal([5150], stopped);
        Assert.True(result.Stopped);
    }

    // Two installed launchers: stopping the first one found is not enough.
    [Fact]
    public void Stop_StopsEveryInstalledLauncher_NotJustTheFirst()
    {
        var root = MakeRoot();
        var layout = LayoutIn(root);
        var exe = Path.Combine(layout.LauncherDir, "cc-launcher");
        var live = new List<int> { 100, 200 };
        var stopped = new List<int>();

        var s = new LauncherStopper(layout)
        {
            RequestQuit = _ => false,
            ListLauncherProcesses = () => live.Select(p => new LauncherProcess(p, exe + " --managed")).ToList(),
            StopProcess = pid => { live.Remove(pid); stopped.Add(pid); return true; },
        };

        var result = s.Stop();

        Assert.Equal([100, 200], stopped.OrderBy(x => x).ToList());
        Assert.True(result.Stopped);
    }

    // If the machine will not tell us what is running, do not claim success - there is no port left
    // to corroborate with, so an unreadable process list means the verdict is unknowable, and
    // unknowable must read as NOT stopped. The caller uses this to decide whether it is safe to
    // delete the launcher's files.
    [Fact]
    public void Stop_WhenTheProcessListCannotBeRead_RefusesToClaimSuccess()
    {
        var root = MakeRoot();
        var s = new LauncherStopper(LayoutIn(root))
        {
            ListLauncherProcesses = () => throw new InvalidOperationException("ps is unavailable"),
            RequestQuit = _ => false,
            StopProcess = _ => false,
        };

        var result = s.Stop();

        Assert.False(result.Stopped);
        Assert.Contains(result.Steps, x => x.Contains("cannot confirm the launcher is stopped", StringComparison.Ordinal));
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
            // FileShare.Read is what the product's own log writer uses - the earlier ReadWrite made this
            // test easier than reality.
            using (var held = new FileStream(openLog, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(held) { AutoFlush = true })
            {
                writer.WriteLine("still being written");

                var kept = new Uninstaller(new InstallLayout(root)).PreserveLogs(root, steps, errors);

                Assert.NotNull(kept);
                var keptLog = Path.Combine(kept!, "live.log");
                Assert.True(File.Exists(keptLog), "a log held open for writing was not captured");
                using var reader = new FileStream(keptLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                Assert.Contains("still being written", new StreamReader(reader).ReadToEnd(), StringComparison.Ordinal);
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
