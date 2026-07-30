using CcDirector.Core.Update;
using Xunit;

namespace CcDirector.Core.Tests.Update;

/// <summary>
/// Placing a Director build over the installed one and putting the previous one back, for BOTH shapes
/// the Director ships in - a single executable file (Windows) and an application bundle directory
/// (macOS) - because the recovery paths that used to do this were written for a file and therefore did
/// nothing at all off Windows (issue #1032, structurally fixed by issue #1033).
///
/// The bundle cases run on every platform on purpose. A hole that only opens on macOS cannot be proved
/// closed by code that only runs on macOS, and this is the mechanism that delivers every future fix.
/// </summary>
public class DirectorBuildSwapperTests : IDisposable
{
    private readonly string _root;

    public DirectorBuildSwapperTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cc-dbs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Place_File_InstallsTheNewBuildAndKeepsThePreviousOne()
    {
        var target = WriteFile("cc-director.exe", "OLD");
        var staged = WriteFile("staged.exe", "NEW");

        var backup = DirectorBuildSwapper.Place(target, staged);

        Assert.Equal(DirectorBuildSwapper.BackupPathFor(target), backup);
        Assert.Equal("NEW", File.ReadAllText(target));
        Assert.Equal("OLD", File.ReadAllText(backup!));
    }

    [Fact]
    public void Place_File_WithNothingInstalled_ReportsThereWasNoPreviousBuild()
    {
        var target = Path.Combine(_root, "cc-director.exe");
        var staged = WriteFile("staged.exe", "NEW");

        Assert.Null(DirectorBuildSwapper.Place(target, staged));
        Assert.Equal("NEW", File.ReadAllText(target));
    }

    [Fact]
    public void RestoreBackup_File_PutsThePreviousBuildBack()
    {
        var target = WriteFile("cc-director.exe", "OLD");
        DirectorBuildSwapper.Place(target, WriteFile("staged.exe", "NEW"));

        Assert.True(DirectorBuildSwapper.RestoreBackup(target));
        Assert.Equal("OLD", File.ReadAllText(target));
    }

    [Fact]
    public void RestoreBackup_KeepingTheBackup_LeavesSomethingToRestoreASecondTime()
    {
        // The Director's own startup recovery keeps the backup: consuming it would leave nothing behind
        // if the build it just restored also failed to start.
        var target = WriteFile("cc-director.exe", "OLD");
        DirectorBuildSwapper.Place(target, WriteFile("staged.exe", "NEW"));

        Assert.True(DirectorBuildSwapper.RestoreBackup(target, keepBackup: true));
        Assert.Equal("OLD", File.ReadAllText(target));
        Assert.True(File.Exists(DirectorBuildSwapper.BackupPathFor(target)));
        Assert.True(DirectorBuildSwapper.RestoreBackup(target, keepBackup: true));
    }

    [Fact]
    public void RestoreBackup_WhenNothingIsInstalledAtAll_StillPutsThePreviousBuildBack()
    {
        // The state a swap leaves if it fails at the very last rename, after the previous build has been
        // moved aside: nothing installed, everything in the backup. Restoring has to work from there, or
        // that machine has no Director and no way for any future release to reach it.
        var target = WriteFile("cc-director.exe", "OLD");
        DirectorBuildSwapper.Place(target, WriteFile("staged.exe", "NEW"), DirectorBuildSwapper.LauncherBackupSuffix);
        File.Delete(target);

        Assert.False(DirectorBuildSwapper.Inspect(target).Exists);
        Assert.True(DirectorBuildSwapper.RestoreBackup(target, DirectorBuildSwapper.LauncherBackupSuffix));
        Assert.Equal("OLD", File.ReadAllText(target));
    }

    [Fact]
    public void RestoreBackup_Bundle_WhenNothingIsInstalledAtAll_StillPutsThePreviousBundleBack()
    {
        var target = WriteBundle("Director.app", "OLD");
        DirectorBuildSwapper.Place(target, WriteBundle("staged/Director.app", "NEW"), DirectorBuildSwapper.LauncherBackupSuffix);
        Directory.Delete(target, recursive: true);

        Assert.False(DirectorBuildSwapper.Inspect(target).Exists);
        Assert.True(DirectorBuildSwapper.RestoreBackup(target, DirectorBuildSwapper.LauncherBackupSuffix));
        Assert.Equal("OLD", File.ReadAllText(DirectorBuildSwapper.BundleExecutable(target)));
    }

    [Fact]
    public void RestoreBackup_WithNoBackup_SaysSoRatherThanClaimingARestore()
    {
        var target = WriteFile("cc-director.exe", "ONLY");
        Assert.False(DirectorBuildSwapper.RestoreBackup(target));
        Assert.Equal("ONLY", File.ReadAllText(target));
    }

    [Fact]
    public void Place_Bundle_InstallsTheWholeBundleAndKeepsThePreviousOne()
    {
        var target = WriteBundle("Director.app", "OLD");
        var staged = WriteBundle("staged/Director.app", "NEW");

        var backup = DirectorBuildSwapper.Place(target, staged);

        Assert.Equal(DirectorBuildSwapper.BackupPathFor(target), backup);
        Assert.Equal("NEW", File.ReadAllText(DirectorBuildSwapper.BundleExecutable(target)));
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(target, "Contents", "Resources", "marker.txt")));
        Assert.Equal("OLD", File.ReadAllText(DirectorBuildSwapper.BundleExecutable(backup!)));
    }

    [Fact]
    public void RestoreBackup_Bundle_PutsTheWholePreviousBundleBack()
    {
        var target = WriteBundle("Director.app", "OLD");
        DirectorBuildSwapper.Place(target, WriteBundle("staged/Director.app", "NEW"));

        Assert.True(DirectorBuildSwapper.RestoreBackup(target));

        Assert.Equal("OLD", File.ReadAllText(DirectorBuildSwapper.BundleExecutable(target)));
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(target, "Contents", "Resources", "marker.txt")));
    }

    [Fact]
    public void Inspect_ABundleWithNoExecutableInside_IsNotPresent()
    {
        // The macOS half-swap that had no name. An interrupted bundle copy leaves a directory that
        // exists and holds nothing that can run; calling that "installed" is how a Mac ends up booting
        // nothing, with the recovery path deciding there was nothing to recover.
        var empty = Path.Combine(_root, "Half.app");
        Directory.CreateDirectory(Path.Combine(empty, "Contents", "MacOS"));

        var presence = DirectorBuildSwapper.Inspect(empty);

        Assert.False(presence.Exists);
        Assert.Equal(0, presence.Length);
        Assert.True(UpdateInstaller.NeedsHalfSwapRecovery(
            installExists: presence.Exists, installLength: presence.Length, oldExists: true, oldLength: 1024));
    }

    [Fact]
    public void Inspect_ACompleteBundle_IsPresentWithTheExecutablesLength()
    {
        var bundle = WriteBundle("Director.app", "OLD");
        var presence = DirectorBuildSwapper.Inspect(bundle);

        Assert.True(presence.Exists);
        Assert.Equal(new FileInfo(DirectorBuildSwapper.BundleExecutable(bundle)).Length, presence.Length);
    }

    [Fact]
    public void Inspect_AMissingPath_IsNotPresent()
    {
        Assert.False(DirectorBuildSwapper.Inspect(Path.Combine(_root, "nothing-here")).Exists);
    }

    [Fact]
    public void DeleteBackup_RemovesEitherShape_AndIsQuietWhenThereIsNothingToRemove()
    {
        var file = WriteFile("cc-director.exe", "OLD");
        DirectorBuildSwapper.Place(file, WriteFile("staged.exe", "NEW"));
        DirectorBuildSwapper.DeleteBackup(file);
        Assert.False(File.Exists(DirectorBuildSwapper.BackupPathFor(file)));

        var bundle = WriteBundle("Director.app", "OLD");
        DirectorBuildSwapper.Place(bundle, WriteBundle("staged/Director.app", "NEW"));
        DirectorBuildSwapper.DeleteBackup(bundle);
        Assert.False(Directory.Exists(DirectorBuildSwapper.BackupPathFor(bundle)));

        DirectorBuildSwapper.DeleteBackup(Path.Combine(_root, "never-existed"));
    }

    [Fact]
    public void TheLauncherBackupNameIsNotTheOneTheDirectorsCleanupDeletes()
    {
        // Two owners, two names. The Director's startup cleanup deletes the default backup; the launcher
        // needs its own to survive that cleanup, because the cleanup runs BEFORE the new build has
        // proved it works.
        var target = WriteFile("cc-director.exe", "OLD");
        DirectorBuildSwapper.Place(target, WriteFile("staged.exe", "NEW"), DirectorBuildSwapper.LauncherBackupSuffix);

        Assert.True(File.Exists(target + DirectorBuildSwapper.LauncherBackupSuffix));
        Assert.False(File.Exists(target + DirectorBuildSwapper.DefaultBackupSuffix));
    }

    private string WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteBundle(string relativePath, string content)
    {
        var bundle = Path.Combine(_root, relativePath);
        var executable = DirectorBuildSwapper.BundleExecutable(bundle);
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, content);
        var resources = Path.Combine(bundle, "Contents", "Resources");
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(resources, "marker.txt"), content);
        return bundle;
    }
}
