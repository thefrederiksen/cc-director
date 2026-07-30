using CcDirector.Core.Update;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The launcher applying a Director update from outside the Director (issue #1033), and above all the
/// case that must never be reported as a success: a new build that does NOT come up.
///
/// These tests do not simulate a health check with a boolean that says yes. The stand-in for "the
/// running Director" READS THE INSTALLED BUILD off disk and answers with the version it finds there,
/// and only when it has been started. So an assertion about what answered is an assertion about what
/// was actually swapped in, in what order - a rollback that ran before the stop, or a poll that read
/// before the start, shows up as the wrong version rather than passing quietly.
///
/// Both install shapes are covered: a single executable file, which is Windows, and an application
/// bundle directory, which is macOS. The bundle cases run everywhere, and that is the point - the macOS
/// rollback used to be a hole where the recovery methods began "if this is not Windows, do nothing"
/// (issue #1032), and a hole cannot be proved closed by code that only ever runs on the other platform.
/// </summary>
public class DirectorUpdateApplyTests : IDisposable
{
    private readonly string _root;
    private readonly string _installDirectory;
    private readonly string _stagingDirectory;

    private const string OldVersion = "1.8.7";
    private const string NewVersion = "1.9.0";

    public DirectorUpdateApplyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cc-dua-" + Guid.NewGuid().ToString("N"));
        _installDirectory = Path.Combine(_root, "app");
        _stagingDirectory = Path.Combine(_root, "staged");
        Directory.CreateDirectory(_installDirectory);
        Directory.CreateDirectory(_stagingDirectory);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ---- The single-executable shape (Windows) -----------------------------

    [Fact]
    public async Task NewBuildComesUp_Swaps_ReportsUpdated_AndRemovesTheBackup()
    {
        var target = InstalledFile(OldVersion);
        var staged = StagedFile(NewVersion);
        var director = new FakeDirector(target, answersFor: NewVersion);

        var result = await Apply(target, staged, director);

        Assert.Equal(SelfUpdateOutcome.Updated, result.Outcome);
        Assert.Equal(NewVersion, File.ReadAllText(target));
        Assert.Equal(1, director.Stops);
        Assert.Equal(1, director.Starts);

        // The backup is deleted once the new build is proved, so superseded builds do not pile up.
        Assert.False(File.Exists(LauncherBackup(target)));
    }

    [Fact]
    public async Task NewBuildNeverComesUp_RollsBackToThePreviousBuild_AndDoesNotReportSuccess()
    {
        var target = InstalledFile(OldVersion);
        var staged = StagedFile(NewVersion);

        // Nothing will ever answer: the new build is installed and does not start.
        var director = new FakeDirector(target, answersFor: null);

        var result = await Apply(target, staged, director);

        Assert.NotEqual(SelfUpdateOutcome.Updated, result.Outcome);
        Assert.Equal(SelfUpdateOutcome.RolledBack, result.Outcome);

        // The previous build is back on disk. This is the assertion the whole change exists for.
        Assert.Equal(OldVersion, File.ReadAllText(target));
        Assert.Contains(NewVersion, result.Message);
        Assert.Contains(result.Steps, step => step.Contains("rolling back", StringComparison.Ordinal));

        // Stopped and started twice: once for the attempt, once to put the previous build back.
        Assert.Equal(2, director.Stops);
        Assert.Equal(2, director.Starts);
    }

    [Fact]
    public async Task SomethingAnswersButNotAsTheNewVersion_StillRollsBack()
    {
        // Liveness is not identity. An answer proves something is listening; only the version proves it
        // is the build that was just installed. Here the OLD version keeps answering - which is what a
        // lingering process, or a new build that never took over, looks like from outside.
        var target = InstalledFile(OldVersion);
        var staged = StagedFile(NewVersion);
        var director = new FakeDirector(target, answersFor: OldVersion);

        var result = await Apply(target, staged, director);

        Assert.Equal(SelfUpdateOutcome.RolledBack, result.Outcome);
        Assert.Equal(OldVersion, File.ReadAllText(target));
    }

    [Fact]
    public async Task NoPreviousBuildToRestore_ReportsFailed_AndSaysTheRollbackWasNotPossible()
    {
        // A fresh install has no backup. A new build that then fails to start leaves the machine on that
        // build, and saying so plainly is the only honest answer - reporting a handled rollback here
        // would be a lie about the state of the machine.
        var target = Path.Combine(_installDirectory, "cc-director.exe");
        var staged = StagedFile(NewVersion);
        var director = new FakeDirector(target, answersFor: null);

        var result = await Apply(target, staged, director);

        Assert.Equal(SelfUpdateOutcome.Failed, result.Outcome);
        Assert.Contains(result.Steps, step => step.Contains("ROLLBACK NOT POSSIBLE", StringComparison.Ordinal));
        Assert.Equal(NewVersion, File.ReadAllText(target));
    }

    [Fact]
    public async Task StagedBuildIsMissing_ReportsFailed_AndPutsTheDirectorBack()
    {
        var target = InstalledFile(OldVersion);
        var missing = Path.Combine(_stagingDirectory, "not-downloaded.exe");
        var director = new FakeDirector(target, answersFor: OldVersion);

        var result = await Apply(target, missing, director);

        Assert.Equal(SelfUpdateOutcome.Failed, result.Outcome);
        Assert.Equal(OldVersion, File.ReadAllText(target));   // untouched
        Assert.Equal(1, director.Starts);                      // restarted on the build already installed
    }

    [Fact]
    public async Task TheLauncherBackupIsNotTheOneTheDirectorsCleanupDeletes()
    {
        // The freshly started Director deletes the ".old" backup during its own startup cleanup. If the
        // launcher kept its rollback material there, a build that started far enough to run cleanup and
        // then died would have destroyed the only way back WHILE the launcher was still waiting to find
        // out whether it was healthy. So the launcher's backup lives under a different name.
        var target = InstalledFile(OldVersion);
        var staged = StagedFile(NewVersion);
        var director = new FakeDirector(target, answersFor: null, pauseAfterStart: true);

        var applying = Apply(target, staged, director);
        await director.WaitForStartAsync();

        Assert.True(File.Exists(LauncherBackup(target)), "the launcher's own backup must exist while the new build is on trial");
        Assert.False(File.Exists(target + DirectorBuildSwapper.DefaultBackupSuffix),
            "the launcher must not use the backup name the Director's startup cleanup deletes");

        director.Release();
        await applying;
    }

    // ---- The application-bundle shape (macOS) ------------------------------

    [Fact]
    public async Task Bundle_NewBuildComesUp_SwapsTheWholeBundle()
    {
        var target = InstalledBundle(OldVersion);
        var staged = StagedBundle(NewVersion);
        var director = new FakeDirector(target, answersFor: NewVersion);

        var result = await Apply(target, staged, director);

        Assert.Equal(SelfUpdateOutcome.Updated, result.Outcome);
        Assert.Equal(NewVersion, File.ReadAllText(DirectorBuildSwapper.BundleExecutable(target)));
        Assert.False(Directory.Exists(LauncherBackup(target)));
    }

    [Fact]
    public async Task Bundle_NewBuildNeverComesUp_RollsBackTheWholeBundle()
    {
        // The macOS rollback, proved. The bundle swap used to delete the installed bundle outright, so no
        // backup existed and the recovery path could not have worked even with its platform guard
        // removed. This asserts the previous bundle is genuinely back in place.
        var target = InstalledBundle(OldVersion);
        var staged = StagedBundle(NewVersion);
        var director = new FakeDirector(target, answersFor: null);

        var result = await Apply(target, staged, director);

        Assert.Equal(SelfUpdateOutcome.RolledBack, result.Outcome);
        Assert.True(Directory.Exists(target));
        Assert.Equal(OldVersion, File.ReadAllText(DirectorBuildSwapper.BundleExecutable(target)));

        // The rest of the bundle came back too, not just the binary - a rollback that restored one file
        // would leave a bundle whose resources belong to the build that failed.
        Assert.Equal("resources-" + OldVersion,
            File.ReadAllText(Path.Combine(target, "Contents", "Resources", "marker.txt")));
    }

    [Fact]
    public async Task Bundle_SomethingAnswersButNotAsTheNewVersion_StillRollsBack()
    {
        var target = InstalledBundle(OldVersion);
        var staged = StagedBundle(NewVersion);
        var director = new FakeDirector(target, answersFor: OldVersion);

        var result = await Apply(target, staged, director);

        Assert.Equal(SelfUpdateOutcome.RolledBack, result.Outcome);
        Assert.Equal(OldVersion, File.ReadAllText(DirectorBuildSwapper.BundleExecutable(target)));
    }

    // ---- Helpers ----------------------------------------------------------

    private static Task<SelfUpdateResult> Apply(string target, string staged, FakeDirector director)
        => new DirectorUpdateApply(unlockTimeout: TimeSpan.FromSeconds(1), pollInterval: TimeSpan.FromMilliseconds(20))
            .ApplyAsync(
                target, staged, NewVersion,
                stopDirector: director.StopAsync,
                startDirector: director.Start,
                readRunningVersion: director.ReadVersionAsync,
                healthTimeout: TimeSpan.FromMilliseconds(300));

    private static string LauncherBackup(string target)
        => DirectorBuildSwapper.BackupPathFor(target, DirectorBuildSwapper.LauncherBackupSuffix);

    private string InstalledFile(string version)
    {
        var path = Path.Combine(_installDirectory, "cc-director.exe");
        File.WriteAllText(path, version);
        return path;
    }

    private string StagedFile(string version)
    {
        var path = Path.Combine(_stagingDirectory, "cc-director-win-x64.exe");
        File.WriteAllText(path, version);
        return path;
    }

    private string InstalledBundle(string version) => WriteBundle(Path.Combine(_installDirectory, "Director.app"), version);

    private string StagedBundle(string version) => WriteBundle(Path.Combine(_stagingDirectory, "Director.app"), version);

    private static string WriteBundle(string bundlePath, string version)
    {
        var executable = DirectorBuildSwapper.BundleExecutable(bundlePath);
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, version);

        var resources = Path.Combine(bundlePath, "Contents", "Resources");
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(resources, "marker.txt"), "resources-" + version);
        return bundlePath;
    }

    /// <summary>
    /// A stand-in for the Director that answers with the version of the build ACTUALLY INSTALLED at the
    /// target path, and only while it is started. <paramref name="answersFor"/> is the version that is
    /// able to start at all - so passing null models the one case that matters most: the new build is
    /// installed and never comes up.
    /// </summary>
    private sealed class FakeDirector
    {
        private readonly string _target;
        private readonly string? _answersFor;
        private readonly bool _pauseAfterStart;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _running;

        public FakeDirector(string target, string? answersFor, bool pauseAfterStart = false)
        {
            _target = target;
            _answersFor = answersFor;
            _pauseAfterStart = pauseAfterStart;
        }

        public int Stops { get; private set; }
        public int Starts { get; private set; }

        public Task StopAsync(CancellationToken ct)
        {
            Stops++;
            _running = false;
            return Task.CompletedTask;
        }

        public void Start()
        {
            Starts++;
            _running = true;
            _started.TrySetResult();
        }

        public async Task<string?> ReadVersionAsync(CancellationToken ct)
        {
            if (_pauseAfterStart) await _released.Task.WaitAsync(ct);
            if (!_running) return null;

            var installed = ReadInstalledVersion();
            // A build that cannot start answers nothing, however healthy the file on disk looks.
            return installed is not null && installed == _answersFor ? installed : null;
        }

        public Task WaitForStartAsync() => _started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void Release() => _released.TrySetResult();

        private string? ReadInstalledVersion()
        {
            try
            {
                if (File.Exists(_target)) return File.ReadAllText(_target);
                var executable = DirectorBuildSwapper.BundleExecutable(_target);
                return File.Exists(executable) ? File.ReadAllText(executable) : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
