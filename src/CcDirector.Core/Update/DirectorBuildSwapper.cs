using System.Diagnostics;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Update;

/// <summary>
/// Places a Director build over the installed one, keeps the previous build as a backup, and puts
/// that backup back on demand - for BOTH shapes the Director ships in: a single executable file on
/// Windows, and an application bundle (a directory) on macOS.
///
/// This exists because the recovery paths that used to do this were written for a file and therefore
/// began with "if this is not Windows, do nothing" (issue #1032). A Mac whose update produced a build
/// that could not start had no way back at all. The shape of the install is the only thing that
/// differs, so the shape is the only thing this class branches on - the decision of WHEN to roll back
/// is made elsewhere and is identical on both platforms.
///
/// The moves are ordered so the install is never left with nothing in place for longer than a single
/// rename: the replacement is fully materialized BESIDE the target first, then the previous build is
/// renamed aside, then the replacement is renamed in. Every path used is a sibling of the target, so
/// the renames stay on one volume and cannot degrade into a slow copy.
/// </summary>
public static class DirectorBuildSwapper
{
    /// <summary>
    /// The backup suffix the Director's own startup recovery reads and its cleanup deletes.
    /// A caller that needs its backup to OUTLIVE the next Director startup must pass a different
    /// suffix - see <see cref="LauncherBackupSuffix"/>.
    /// </summary>
    public const string DefaultBackupSuffix = ".old";

    /// <summary>
    /// The backup suffix the launcher uses when IT applies an update.
    ///
    /// Deliberately not "<c>.old</c>": the freshly started Director deletes "<c>.old</c>" during its
    /// own startup cleanup, which would destroy the launcher's only way back WHILE the launcher is
    /// still waiting to find out whether that Director is healthy. A build that starts far enough to
    /// run cleanup and then dies before answering would have left the launcher with a broken install
    /// and nothing to restore. The launcher owns this backup and removes it itself once the new build
    /// has proved it works.
    /// </summary>
    public const string LauncherBackupSuffix = ".prev";

    /// <summary>The path a backup of <paramref name="targetPath"/> is kept at.</summary>
    public static string BackupPathFor(string targetPath, string backupSuffix = DefaultBackupSuffix)
        => targetPath + backupSuffix;

    /// <summary>The path a replacement is materialized at before it is renamed into place.</summary>
    public static string StagingPathFor(string targetPath) => targetPath + ".new";

    /// <summary>
    /// True when this path is an application bundle (a directory) rather than a single executable
    /// file. Asked of a path that EXISTS; a path that is neither reads as not-a-bundle so callers
    /// treat a missing install as the file case and report it missing rather than throwing.
    /// </summary>
    public static bool IsBundle(string path) => !File.Exists(path) && Directory.Exists(path);

    /// <summary>
    /// Whether a build is present at <paramref name="Exists"/>, and the length of its executable.
    /// The length is what tells a half-written copy from a real build, so it is reported for a bundle
    /// too - taken from the executable inside it, because an empty directory and a complete bundle are
    /// both "a directory that exists".
    /// </summary>
    public readonly record struct BuildPresence(bool Exists, long Length);

    /// <summary>
    /// Report whether a usable build sits at <paramref name="path"/> and how big its executable is,
    /// for either shape. A bundle counts as present only when the executable inside it is there -
    /// an interrupted bundle copy leaves a directory that exists and contains nothing that can run,
    /// and calling that "present" is how a Mac ends up booting nothing.
    /// </summary>
    public static BuildPresence Inspect(string path)
    {
        try
        {
            if (IsBundle(path))
            {
                var executable = BundleExecutable(path);
                var bundleInfo = new FileInfo(executable);
                return new BuildPresence(bundleInfo.Exists, bundleInfo.Exists ? bundleInfo.Length : 0);
            }

            var info = new FileInfo(path);
            return new BuildPresence(info.Exists, info.Exists ? info.Length : 0);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorBuildSwapper] Inspect({path}) FAILED: {ex.Message}");
            return new BuildPresence(false, 0);
        }
    }

    /// <summary>The executable inside an application bundle: Contents/MacOS/cc-director.</summary>
    public static string BundleExecutable(string bundlePath)
        => Path.Combine(bundlePath, "Contents", "MacOS", UpdateInstaller.ExecutableName);

    /// <summary>
    /// Make <paramref name="stagedSource"/> become <paramref name="targetPath"/>, keeping whatever was
    /// there as a backup. Returns the backup path, or null when there was no previous build to keep.
    /// </summary>
    public static string? Place(string targetPath, string stagedSource, string backupSuffix = DefaultBackupSuffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedSource);

        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        return IsBundle(stagedSource)
            ? PlaceBundle(targetPath, stagedSource, backupSuffix)
            : PlaceFile(targetPath, stagedSource, backupSuffix);
    }

    /// <summary>
    /// Put the backup back over <paramref name="targetPath"/>. Returns false when there is no backup
    /// to restore, which is a real answer the caller must report rather than treat as a restore.
    ///
    /// <paramref name="keepBackup"/> leaves the backup on disk afterwards, for the Director's own
    /// startup recovery: its cleanup step deletes the backup a moment later anyway, and a restore that
    /// consumed it would leave nothing behind if the restored build also failed to start.
    /// </summary>
    public static bool RestoreBackup(string targetPath, string backupSuffix = DefaultBackupSuffix, bool keepBackup = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var backup = BackupPathFor(targetPath, backupSuffix);
        if (!Inspect(backup).Exists)
        {
            FileLog.Write($"[DirectorBuildSwapper] RestoreBackup: no backup at {backup}");
            return false;
        }

        if (IsBundle(backup))
            RestoreBundle(targetPath, backup, keepBackup);
        else
            RestoreFile(targetPath, backup, keepBackup);

        FileLog.Write($"[DirectorBuildSwapper] RestoreBackup: restored {targetPath} from {backup} (kept={keepBackup})");
        return true;
    }

    /// <summary>Delete a backup this caller owns and no longer needs. Never throws.</summary>
    public static void DeleteBackup(string targetPath, string backupSuffix = DefaultBackupSuffix)
    {
        var backup = BackupPathFor(targetPath, backupSuffix);
        try
        {
            Remove(backup);
            FileLog.Write($"[DirectorBuildSwapper] DeleteBackup: removed {backup}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorBuildSwapper] DeleteBackup({backup}) FAILED: {ex.Message}");
        }
    }

    private static string? PlaceFile(string targetPath, string stagedSource, string backupSuffix)
    {
        if (!File.Exists(stagedSource))
            throw new FileNotFoundException("Staged build not found.", stagedSource);

        var staging = StagingPathFor(targetPath);
        var backup = BackupPathFor(targetPath, backupSuffix);

        if (File.Exists(staging)) File.Delete(staging);
        File.Copy(stagedSource, staging);

        if (!File.Exists(targetPath))
        {
            File.Move(staging, targetPath);
            FileLog.Write($"[DirectorBuildSwapper] PlaceFile: nothing to replace, installed at {targetPath}");
            return null;
        }

        if (File.Exists(backup)) File.Delete(backup);
        File.Replace(staging, targetPath, backup);
        FileLog.Write($"[DirectorBuildSwapper] PlaceFile: installed {targetPath} (backup {backup})");
        return backup;
    }

    private static string? PlaceBundle(string targetPath, string stagedBundle, string backupSuffix)
    {
        var staging = StagingPathFor(targetPath);
        var backup = BackupPathFor(targetPath, backupSuffix);

        Remove(staging);
        CopyBundle(stagedBundle, staging);
        PrepareBundleToRun(staging);

        if (!Directory.Exists(targetPath) && !File.Exists(targetPath))
        {
            Directory.Move(staging, targetPath);
            FileLog.Write($"[DirectorBuildSwapper] PlaceBundle: nothing to replace, installed at {targetPath}");
            return null;
        }

        Remove(backup);
        Directory.Move(targetPath, backup);
        Directory.Move(staging, targetPath);
        FileLog.Write($"[DirectorBuildSwapper] PlaceBundle: installed {targetPath} (backup {backup})");
        return backup;
    }

    private static void RestoreFile(string targetPath, string backup, bool keepBackup)
    {
        var staging = StagingPathFor(targetPath);
        if (File.Exists(staging)) File.Delete(staging);

        if (keepBackup)
        {
            File.Copy(backup, staging);
            if (File.Exists(targetPath)) File.Replace(staging, targetPath, null);
            else File.Move(staging, targetPath);
            return;
        }

        if (File.Exists(targetPath)) File.Replace(backup, targetPath, null);
        else File.Move(backup, targetPath);
    }

    private static void RestoreBundle(string targetPath, string backup, bool keepBackup)
    {
        var staging = StagingPathFor(targetPath);
        Remove(staging);

        if (keepBackup)
        {
            CopyBundle(backup, staging);
            PrepareBundleToRun(staging);
            Remove(targetPath);
            Directory.Move(staging, targetPath);
            return;
        }

        Remove(targetPath);
        Directory.Move(backup, targetPath);
        PrepareBundleToRun(targetPath);
    }

    /// <summary>
    /// Copy an application bundle. On macOS this is <c>ditto</c>, which is what the existing bundle
    /// swap uses and the only copy that reliably preserves the symbolic links, permissions and
    /// extended attributes inside a bundle. Everywhere else it is a plain recursive copy - which is
    /// what lets the ordering and rollback logic above be exercised, and proved, on Windows.
    /// </summary>
    private static void CopyBundle(string source, string destination)
    {
        if (OperatingSystem.IsMacOS())
        {
            Run("/usr/bin/ditto", source, destination);
            return;
        }

        CopyDirectory(source, destination);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var child in Directory.GetDirectories(source))
            CopyDirectory(child, Path.Combine(destination, Path.GetFileName(child)));
    }

    /// <summary>
    /// Make a freshly materialized bundle runnable on macOS: clear the download quarantine and set the
    /// executable bit. A no-op on every other platform. Best effort - a failure here is reported and
    /// the caller's health check is what decides whether the build actually works.
    /// </summary>
    private static void PrepareBundleToRun(string bundlePath)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Run("/usr/bin/xattr", "-dr", "com.apple.quarantine", bundlePath);
        Run("/bin/chmod", "+x", BundleExecutable(bundlePath));
    }

    /// <summary>Delete a path of either shape. Absent is success; there is nothing to remove.</summary>
    private static void Remove(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static void Run(string file, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo { FileName = file, UseShellExecute = false };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {file}");
        process.WaitForExit();
        if (process.ExitCode != 0)
            FileLog.Write($"[DirectorBuildSwapper] Run non-zero exit ({process.ExitCode}): {file} {string.Join(' ', arguments)}");
    }
}
