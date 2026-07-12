using System.Diagnostics;
using System.Reflection;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Update;

/// <summary>
/// Applies a staged update by swapping the installed build with a downloaded one.
///
/// A running single-file executable cannot overwrite itself (Windows holds a
/// file lock; macOS keeps the live inode), so the swap is performed by the
/// freshly downloaded build running in a hidden "<c>--apply-update</c>" mode: it
/// waits for the old process to exit, replaces the install, and relaunches.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>Name of the .NET apphost binary inside the bundle / on disk.</summary>
    public const string ExecutableName = "cc-director";

    /// <summary>Root staging directory for downloaded updates: config/director/updates/.</summary>
    public static string StagingRoot => Path.Combine(CcStorage.ToolConfig("director"), "updates");

    /// <summary>
    /// The path a new build must overwrite to "become" the installed app. On
    /// Windows this is the running cc-director.exe; on macOS it is the enclosing
    /// "CC Director.app" bundle (not the binary inside it). Falls back to the
    /// process path when not running from a bundle (e.g. a bare dev binary).
    /// </summary>
    public static string InstallTarget()
    {
        var proc = Environment.ProcessPath ?? "";
        if (OperatingSystem.IsMacOS())
            return AppBundleOf(proc) ?? proc;
        return proc;
    }

    /// <summary>
    /// Max times startup will hand a staged update to the relauncher before giving up.
    /// A staged update whose swap never completes must NOT make us relaunch-and-exit
    /// forever (issue #242), which presents to the user as "clicking does nothing".
    /// </summary>
    public const int MaxApplyAttempts = 2;

    /// <summary>
    /// True when the staged update has already failed to apply <see cref="MaxApplyAttempts"/>
    /// times for its current version. Pure decision, unit-tested.
    /// </summary>
    public static bool HasExhaustedApplyAttempts(UpdaterState state, int maxAttempts)
        => state.ApplyAttemptVersion == state.StagedVersion && state.ApplyAttempts >= maxAttempts;

    /// <summary>
    /// True when a swap left the install in a broken state that must be recovered from the
    /// <c>.old</c> backup on startup (issue #242): the install exe is missing or zero-length
    /// (a half-completed copy/replace) while a non-empty <c>.old</c> backup is present.
    /// Pure decision, unit-tested.
    /// </summary>
    public static bool NeedsHalfSwapRecovery(bool installExists, long installLength, bool oldExists, long oldLength)
        => (!installExists || installLength == 0) && oldExists && oldLength > 0;

    /// <summary>
    /// True when a previously-swapped build never proved healthy and must be rolled back to its
    /// <c>.old</c> backup on this startup (issue #242): a health check is still pending for a
    /// version, the running build is NOT that version (so the new build failed to come up and
    /// hand control back), and a non-empty <c>.old</c> backup exists to roll back to.
    /// Pure decision, unit-tested.
    /// </summary>
    public static bool NeedsHealthRollback(string? pendingHealthVersion, string runningVersion, bool oldExists, long oldLength)
        => !string.IsNullOrEmpty(pendingHealthVersion)
           && !string.Equals(pendingHealthVersion, runningVersion, StringComparison.Ordinal)
           && oldExists && oldLength > 0;

    /// <summary>
    /// If a verified, newer update has been staged for THIS install path, launch the
    /// relauncher to apply it and return true (the caller must then exit so the swap can
    /// proceed). Called at startup, before any session exists, so applying an update never
    /// loses running work. Returns false when nothing is pending or we boot the current
    /// build instead; in the latter "gave up" case <paramref name="failureNotice"/> is set
    /// to a user-facing message the caller should surface (issue #242 -- never fail silently).
    /// </summary>
    public static bool TryApplyStagedUpdateAtStartup(out string? failureNotice)
    {
        failureNotice = null;
        try
        {
            var state = UpdaterState.Load();
            if (string.IsNullOrEmpty(state.StagedVersion)
                || string.IsNullOrEmpty(state.StagedExecutable)
                || string.IsNullOrEmpty(state.InstallTarget))
                return false;

            // Only ever apply an update that targets the path we are running from.
            if (!PathsEqual(state.InstallTarget, InstallTarget()))
                return false;

            // Never re-apply a version that already failed its post-update health check and was
            // rolled back (issue #242). Pinning it here stops a re-stage/re-apply loop of a build
            // we already know does not start.
            if (!string.IsNullOrEmpty(state.PinnedBadVersion)
                && string.Equals(state.PinnedBadVersion, state.StagedVersion, StringComparison.Ordinal))
            {
                FileLog.Start();
                FileLog.Write($"[UpdateInstaller] Staged {state.StagedVersion} is pinned as a failed update; clearing without applying.");
                ClearStagedState();
                return false;
            }

            if (!StagedIsNewer(state.StagedVersion))
            {
                // The staged version is not newer than what's running -- the apply already
                // succeeded (or is obsolete). Clear it so we never re-evaluate it again.
                FileLog.Start();
                FileLog.Write($"[UpdateInstaller] Staged {state.StagedVersion} is not newer than running build; clearing.");
                ClearStagedState();
                return false;
            }

            if (!File.Exists(state.StagedExecutable))
            {
                FileLog.Start();
                FileLog.Write($"[UpdateInstaller] Staged executable missing, clearing: {state.StagedExecutable}");
                ClearStagedState();
                return false;
            }

            // Bound the apply: if it has already failed MaxApplyAttempts times, give up,
            // clear the staged state, and boot the current build with a visible notice
            // instead of relaunching-and-exiting forever (issue #242).
            if (HasExhaustedApplyAttempts(state, MaxApplyAttempts))
            {
                FileLog.Start();
                FileLog.Write($"[UpdateInstaller] Giving up on staged update {state.StagedVersion} after {state.ApplyAttempts} failed apply attempts; clearing and booting current build.");
                var version = state.StagedVersion;
                ClearStagedState();
                failureNotice =
                    $"Director could not finish updating to {version} after {MaxApplyAttempts} attempts, " +
                    "so it has started on the current version instead. The pending update was cleared and " +
                    "will be retried later. See the log for details.";
                return false;
            }

            // Record this attempt BEFORE launching, so a crash mid-apply still counts toward
            // the bound (otherwise a swap that crashes silently would never increment). Reset
            // the counter when a different version is now staged.
            int priorAttempts = state.ApplyAttemptVersion == state.StagedVersion ? state.ApplyAttempts : 0;
            state.ApplyAttemptVersion = state.StagedVersion;
            state.ApplyAttempts = priorAttempts + 1;
            state.Save();

            FileLog.Start();
            FileLog.Write($"[UpdateInstaller] Applying staged update {state.StagedVersion} at startup (attempt {state.ApplyAttempts}/{MaxApplyAttempts}) -> {state.InstallTarget}");
            LaunchRelauncher(state.StagedExecutable, state.InstallTarget);
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdateInstaller] TryApplyStagedUpdateAtStartup FAILED: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Spawn the staged build detached, instructing it to wait for us (the current
    /// process) to exit and then swap itself over <paramref name="installTarget"/>.
    /// The caller should request application shutdown immediately after this returns.
    /// </summary>
    public static void LaunchRelauncher(string stagedExecutable, string installTarget)
    {
        FileLog.Write($"[UpdateInstaller] LaunchRelauncher: staged={stagedExecutable}, target={installTarget}");
        var psi = new ProcessStartInfo
        {
            FileName = stagedExecutable,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--apply-update");
        psi.ArgumentList.Add(installTarget);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(psi);
    }

    /// <summary>
    /// Entry point for the hidden "<c>--apply-update &lt;targetPath&gt; &lt;parentPid&gt;</c>"
    /// mode. Waits for the parent to exit, replaces the installed build with this
    /// (staged) one, relaunches it, and returns a process exit code.
    /// </summary>
    public static int ApplyUpdate(string targetPath, int parentPid)
    {
        FileLog.Start();
        FileLog.Write($"[UpdateInstaller] ApplyUpdate: target={targetPath}, parentPid={parentPid}, self={Environment.ProcessPath}");

        WaitForProcessExit(parentPid, TimeSpan.FromSeconds(30));

        // Record which version we are about to install so the freshly-swapped build must
        // prove it can come up healthy before the update is trusted (issue #242). If that
        // build fails to reach its main window, a later startup sees this marker still set
        // (and the running version unchanged) and rolls back to the .old backup.
        var versionBeingInstalled = UpdaterState.Load().StagedVersion;

        if (OperatingSystem.IsWindows())
            SwapWindows(targetPath);
        else if (OperatingSystem.IsMacOS())
            SwapMac(targetPath);
        else
            throw new PlatformNotSupportedException("Auto-update is only supported on Windows and macOS.");

        // Clear the staged marker BEFORE relaunching so the freshly-installed build
        // doesn't see itself as a pending update and loop. Arm the post-update health
        // self-check at the same time (issue #242).
        ClearStagedState();
        ArmHealthCheck(versionBeingInstalled);
        Relaunch(targetPath);

        FileLog.Write("[UpdateInstaller] ApplyUpdate: complete");
        FileLog.Stop();
        return 0;
    }

    /// <summary>
    /// Startup housekeeping: delete leftovers from a previous swap and prune staging
    /// directories older than 7 days. The "<c>.old</c>" backup is deliberately KEPT while a
    /// post-update health check is still pending - it is the only thing a health rollback can
    /// restore, and the marker clears (and the backup is deleted) in
    /// <see cref="MarkCurrentBuildHealthy"/> once the new build proves itself. Safe to call
    /// unconditionally; never throws.
    /// </summary>
    public static void CleanupAfterUpdate()
    {
        try
        {
            // Remove leftovers from a prior swap: any ".new" an interrupted swap left behind
            // and any ".failed" a rollback set aside (each is a file on Windows, a bundle
            // directory on macOS). The ".old" backup is removed only when no health check is
            // pending; deleting it on the first boot of a freshly-swapped build would leave a
            // later health rollback with nothing to restore.
            var target = InstallTarget();
            var healthPending = !string.IsNullOrEmpty(UpdaterState.Load().PendingHealthCheckVersion);
            var leftovers = healthPending
                ? new[] { target + ".new", target + ".failed" }
                : new[] { target + ".new", target + ".failed", target + ".old" };
            if (healthPending)
                FileLog.Write("[UpdateInstaller] CleanupAfterUpdate: keeping the .old backup until the new build proves healthy.");
            foreach (var leftover in leftovers)
            {
                if (!EntryExists(leftover)) continue;
                DeleteEntry(leftover);
                FileLog.Write($"[UpdateInstaller] CleanupAfterUpdate: removed {leftover}");
            }

            if (Directory.Exists(StagingRoot))
            {
                var cutoff = DateTime.UtcNow.AddDays(-7);
                foreach (var dir in Directory.EnumerateDirectories(StagingRoot))
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                    {
                        Directory.Delete(dir, recursive: true);
                        FileLog.Write($"[UpdateInstaller] CleanupAfterUpdate: pruned stale staging {dir}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdateInstaller] CleanupAfterUpdate FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Recover from a half-completed swap on startup (issue #242). If the install exe is
    /// missing or zero-length (an interrupted copy/replace) but a non-empty <c>.old</c> backup
    /// is present, restore the install from the backup so the app can boot instead of dying
    /// silently. Returns a user-facing notice when a recovery happened, otherwise null.
    /// MUST run BEFORE <see cref="CleanupAfterUpdate"/> (which deletes the <c>.old</c> backup).
    /// Best-effort; never throws.
    /// </summary>
    public static string? RecoverHalfAppliedSwap()
    {
        try
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
                return null;

            var target = InstallTarget();
            var old = target + ".old";

            var installExists = EntryExists(target);
            var installLength = installExists ? EntryExecutableLength(target) : 0;
            var oldExists = EntryExists(old);
            var oldLength = oldExists ? EntryExecutableLength(old) : 0;

            if (!NeedsHalfSwapRecovery(installExists, installLength, oldExists, oldLength))
                return null;

            FileLog.Start();
            FileLog.Write($"[UpdateInstaller] RecoverHalfAppliedSwap: install is " +
                $"{(installExists ? $"broken (executable {installLength} bytes)" : "missing")}; restoring from {old} ({oldLength} bytes).");

            if (OperatingSystem.IsMacOS())
            {
                // The install is a bundle directory (or a bare development binary). A directory
                // cannot be restored by an atomic copy, so restore by rename: the backup becomes
                // the install again. The backup is consumed, which is fine - the restored build
                // was the last known-good one and its next boot needs no backup.
                DeleteEntry(target);
                MoveEntry(old, target);
            }
            else
            {
                // Windows behavior unchanged: restore by copy so the backup survives for
                // another recovery attempt.
                if (installExists) File.Delete(target);
                File.Copy(old, target);
            }
            FileLog.Write($"[UpdateInstaller] RecoverHalfAppliedSwap: restored {target} from backup.");

            return "Director detected a half-finished update and restored the previous working " +
                   "version so it could start. The interrupted update will be retried later. " +
                   "See the log for details.";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdateInstaller] RecoverHalfAppliedSwap FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Roll back an update that produced a build which never came up healthy (issue #242). If a
    /// post-update health check is still pending for a version the running build did not become,
    /// restore the install from the <c>.old</c> backup, pin the bad version so it is not re-applied,
    /// clear the health marker, and return a "update X failed, rolled back" notice. The caller must
    /// exit and relaunch the restored build so the rolled-back version runs. Returns null when no
    /// rollback is needed. MUST run BEFORE <see cref="CleanupAfterUpdate"/>. Best-effort; never throws.
    /// </summary>
    public static string? TryRollBackFailedUpdate()
    {
        try
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
                return null;

            var state = UpdaterState.Load();
            var pending = state.PendingHealthCheckVersion;
            if (string.IsNullOrEmpty(pending))
                return null;

            var running = (Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0)).ToString(3);
            var target = InstallTarget();
            var old = target + ".old";
            var oldExists = EntryExists(old);
            var oldLength = oldExists ? EntryExecutableLength(old) : 0;

            if (!NeedsHealthRollback(pending, running, oldExists, oldLength))
                return null;

            FileLog.Start();
            FileLog.Write($"[UpdateInstaller] TryRollBackFailedUpdate: update {pending} never became healthy " +
                $"(running {running}); rolling back to backup {old} ({oldLength} bytes) and pinning the bad version.");

            if (OperatingSystem.IsMacOS())
            {
                // Restore by rename. The bad build is set aside FIRST (never deleted before
                // the backup is in place), then the backup is renamed in. If the second rename
                // fails the install is momentarily missing, and the next startup's
                // RecoverHalfAppliedSwap restores the still-present backup.
                var failed = target + ".failed";
                DeleteEntry(failed);
                if (EntryExists(target)) MoveEntry(target, failed);
                MoveEntry(old, target);
                DeleteEntry(failed);
            }
            else
            {
                // Windows behavior unchanged: restore the previous build over the failing one
                // by copy, keeping the backup.
                var newPath = target + ".new";
                if (File.Exists(newPath)) File.Delete(newPath);
                File.Copy(old, newPath);
                if (File.Exists(target)) File.Replace(newPath, target, null);
                else File.Move(newPath, target);
            }

            // Pin the bad version and clear the health marker so we do not loop.
            state.PinnedBadVersion = pending;
            state.PendingHealthCheckVersion = null;
            state.Save();

            FileLog.Write($"[UpdateInstaller] TryRollBackFailedUpdate: restored previous build at {target}; pinned bad version {pending}.");
            return $"Update {pending} failed to start correctly, so Director rolled back to the " +
                   "previous working version. That update has been blocked from retrying. " +
                   "See the log for details.";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdateInstaller] TryRollBackFailedUpdate FAILED: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Clear the post-update health-check marker once the current build has reached a healthy,
    /// interactive state (issue #242). Called from the UI after the main window is shown, so a
    /// successful update is no longer treated as pending and never rolls back. No-op when nothing
    /// is pending. Best-effort; never throws.
    /// </summary>
    /// <returns>
    /// True when a pending health check was actually cleared - i.e. a Director self-update was just
    /// applied and this is its first healthy boot. This is the version-change signal the lifecycle
    /// uses to force a one-time tool reconcile (issue #827): a version bump is the strongest signal
    /// the bundled tools manifest changed. False when nothing was pending (an ordinary boot).
    /// </returns>
    public static bool MarkCurrentBuildHealthy()
    {
        try
        {
            var state = UpdaterState.Load();
            if (string.IsNullOrEmpty(state.PendingHealthCheckVersion))
                return false;

            FileLog.Write($"[UpdateInstaller] MarkCurrentBuildHealthy: clearing pending health check for {state.PendingHealthCheckVersion}.");
            state.PendingHealthCheckVersion = null;
            state.Save();

            // The update is now trusted, so the ".old" backup has served its purpose. Delete it
            // in the background: this method is called from the UI thread right after the main
            // window shows, and on macOS the backup is a whole application bundle.
            var old = InstallTarget() + ".old";
            _ = Task.Run(() =>
            {
                try
                {
                    if (!EntryExists(old)) return;
                    DeleteEntry(old);
                    FileLog.Write($"[UpdateInstaller] MarkCurrentBuildHealthy: removed backup {old} after a healthy boot.");
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[UpdateInstaller] MarkCurrentBuildHealthy: backup cleanup FAILED: {ex.Message}");
                }
            });
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdateInstaller] MarkCurrentBuildHealthy FAILED: {ex.Message}");
            return false;
        }
    }

    /// <summary>Arm the post-update health self-check for a freshly-installed version (issue #242).</summary>
    private static void ArmHealthCheck(string? version)
    {
        if (string.IsNullOrEmpty(version))
            return;
        var state = UpdaterState.Load();
        state.PendingHealthCheckVersion = version;
        state.Save();
        FileLog.Write($"[UpdateInstaller] ArmHealthCheck: armed post-update health check for {version}.");
    }

    /// <summary>True when a file OR a directory exists at the path (an install target is a single-file executable on Windows, a bundle directory on macOS).</summary>
    private static bool EntryExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// The byte length of the executable an install entry represents: the file itself for a
    /// single-file install, or the apphost binary inside a ".app" bundle directory. Returns 0
    /// when the executable is missing - a bundle directory without its binary is as broken as
    /// a zero-length file, and the recovery decisions treat both the same way.
    /// </summary>
    private static long EntryExecutableLength(string path)
    {
        var file = new FileInfo(path);
        if (file.Exists) return file.Length;
        var bundled = new FileInfo(Path.Combine(path, "Contents", "MacOS", ExecutableName));
        return bundled.Exists ? bundled.Length : 0;
    }

    /// <summary>Rename a file or a directory. Both installs live on one volume, so this is a true rename, never a copy.</summary>
    private static void MoveEntry(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    /// <summary>Delete a file or a directory tree; no-op when nothing is there.</summary>
    private static void DeleteEntry(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    /// <summary>
    /// Return the enclosing ".app" bundle path for a path inside one, or null when
    /// the path is not inside a bundle.
    /// </summary>
    public static string? AppBundleOf(string path)
    {
        const string marker = ".app" + "/";
        var idx = path.IndexOf(marker, StringComparison.Ordinal);
        if (idx >= 0)
            return path.Substring(0, idx + 4); // keep the ".app"
        if (path.EndsWith(".app", StringComparison.Ordinal))
            return path;
        return null;
    }

    private static void SwapWindows(string targetExe)
    {
        var staged = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath is null; cannot locate the staged build.");

        // Copy the new build onto the target volume FIRST, so the install is never
        // left without an exe if the copy fails. Then atomically replace, keeping
        // the old exe as a ".old" backup (cleaned up on the next normal startup).
        var newPath = targetExe + ".new";
        var old = targetExe + ".old";
        if (File.Exists(newPath)) File.Delete(newPath);
        File.Copy(staged, newPath);

        if (File.Exists(old)) File.Delete(old);
        if (File.Exists(targetExe))
            File.Replace(newPath, targetExe, old); // target <- new, old <- previous target
        else
            File.Move(newPath, targetExe);
        FileLog.Write($"[UpdateInstaller] SwapWindows: installed staged build at {targetExe}");
    }

    private static void SwapMac(string targetApp)
    {
        var stagedApp = AppBundleOf(Environment.ProcessPath ?? "")
            ?? throw new InvalidOperationException("Staged build is not inside an .app bundle; cannot swap.");

        // Build the replacement bundle fully BESIDE the target first (de-quarantined
        // and executable), then swap with two back-to-back renames so the install is
        // never left half-written. The previous build is kept as a ".old" backup, the
        // rollback target if this new build never proves healthy; it is deleted only
        // when the new build marks itself healthy. A running process keeps its file
        // node across a rename, so renaming a live bundle is safe; the quarantine
        // strip below must stay BEFORE anything could launch the new bundle (once
        // Gatekeeper assesses a quarantined bundle the attribute becomes permanently
        // unremovable for the user).
        var newApp = targetApp + ".new";
        var oldApp = targetApp + ".old";
        Run("/bin/rm", "-rf", newApp);
        Run("/usr/bin/ditto", stagedApp, newApp);
        Run("/usr/bin/xattr", "-dr", "com.apple.quarantine", newApp);
        Run("/bin/chmod", "+x", Path.Combine(newApp, "Contents", "MacOS", ExecutableName));

        Run("/bin/rm", "-rf", oldApp);
        if (Directory.Exists(targetApp) || File.Exists(targetApp))
            Run("/bin/mv", targetApp, oldApp);
        Run("/bin/mv", newApp, targetApp);
        FileLog.Write($"[UpdateInstaller] SwapMac: installed staged bundle at {targetApp} (backup: {oldApp})");
    }

    private static void Relaunch(string targetPath)
    {
        if (OperatingSystem.IsMacOS())
            Run("/usr/bin/open", targetPath);
        else
            Process.Start(new ProcessStartInfo { FileName = targetPath, UseShellExecute = true });
    }

    private static void ClearStagedState()
    {
        try
        {
            var s = UpdaterState.Load();
            s.StagedVersion = null;
            s.StagedExecutable = null;
            s.InstallTarget = null;
            s.ApplyAttempts = 0;
            s.ApplyAttemptVersion = null;
            s.Save();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[UpdateInstaller] ClearStagedState FAILED: {ex.Message}");
        }
    }

    private static bool StagedIsNewer(string stagedVersion)
    {
        var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        if (!Version.TryParse(stagedVersion, out var staged)) return false;
        static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
        return Norm(staged) > Norm(current);
    }

    private static bool PathsEqual(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            comparison);
    }

    private static void Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo { FileName = file, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {file}");
        p.WaitForExit();
        if (p.ExitCode != 0)
            FileLog.Write($"[UpdateInstaller] Run non-zero exit ({p.ExitCode}): {file} {string.Join(' ', args)}");
    }

    private static void WaitForProcessExit(int pid, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                var p = Process.GetProcessById(pid);
                if (p.HasExited) return;
            }
            catch (ArgumentException)
            {
                return; // no such process == already exited
            }
            Thread.Sleep(200);
        }
        FileLog.Write($"[UpdateInstaller] WaitForProcessExit: pid {pid} still alive after {timeout.TotalSeconds}s; proceeding");
    }
}
