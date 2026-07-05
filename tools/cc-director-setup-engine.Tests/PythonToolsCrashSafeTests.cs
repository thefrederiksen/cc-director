using System.Runtime.Versioning;
using System.Threading;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests for the issue #994 crash-safe base-Python provisioning: the whole-directory swap that leaves the
/// live runtime intact when a rebuild cannot complete, the runtime-verify that rejects an incomplete staged
/// Python before it can replace a working one, and the cross-process install lock. These are the guards that
/// stop a redundant or interrupted install (a running Director holding a .pyd open) from stripping the shared
/// Python of its standard library and breaking every cc-* tool.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PythonToolsCrashSafeTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public PythonToolsCrashSafeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-pycrash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // --- SwapDir: the whole-directory atomic swap ---------------------------------------------------

    [Fact]
    public void SwapDir_NoLiveDir_MovesStagedIntoPlace()
    {
        var staged = Path.Combine(_dir, "staged");
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "new.txt"), "new");
        var live = Path.Combine(_dir, "live"); // does not exist yet

        var ok = PythonToolsInstaller.SwapDir(staged, live, out var err);

        Assert.True(ok, err);
        Assert.True(File.Exists(Path.Combine(live, "new.txt")));
        Assert.False(Directory.Exists(staged)); // staged was renamed into place
    }

    [Fact]
    public void SwapDir_ReplacesExistingLive()
    {
        var live = Path.Combine(_dir, "live");
        Directory.CreateDirectory(live);
        File.WriteAllText(Path.Combine(live, "old.txt"), "old");
        var staged = Path.Combine(_dir, "staged");
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "new.txt"), "new");

        var ok = PythonToolsInstaller.SwapDir(staged, live, out var err);

        Assert.True(ok, err);
        Assert.True(File.Exists(Path.Combine(live, "new.txt")));
        Assert.False(File.Exists(Path.Combine(live, "old.txt"))); // old content fully replaced
    }

    [Fact]
    public void SwapDir_LiveHasOpenFile_FailsAndLeavesLiveIntact()
    {
        // The core issue #994 guarantee: if a running Director holds a file open inside the live Python tree,
        // Windows refuses to rename that tree, so the swap fails - and the live tree is left EXACTLY as it was
        // (this is the case that used to partially delete the runtime and strip its standard library).
        var live = Path.Combine(_dir, "live");
        Directory.CreateDirectory(live);
        File.WriteAllText(Path.Combine(live, "keep.txt"), "original");
        var staged = Path.Combine(_dir, "staged");
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "new.txt"), "new");

        bool ok;
        string err;
        using (var _ = new FileStream(Path.Combine(live, "keep.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ok = PythonToolsInstaller.SwapDir(staged, live, out err);
        }

        Assert.False(ok);
        Assert.NotEqual("", err);
        Assert.Equal("original", File.ReadAllText(Path.Combine(live, "keep.txt"))); // untouched
        Assert.False(File.Exists(Path.Combine(live, "new.txt")));                    // staged NOT swapped in
    }

    // --- InstallAsync: an incomplete staged Python never replaces a working one --------------------

    [Fact]
    public async Task InstallAsync_IncompleteStagedPython_LeavesLivePythonUntouched()
    {
        // The live base Python is healthy-looking (a sentinel proves it). The release's Python asset extracts
        // an interpreter that cannot run (a non-PE "python.exe"), so it fails the runtime verify while still
        // in staging. The install must fail WITHOUT touching the live Python and WITHOUT recording a version.
        Directory.CreateDirectory(_layout.PythonDir);
        var sentinel = Path.Combine(_layout.PythonDir, "SENTINEL.txt");
        File.WriteAllText(sentinel, "do-not-delete");

        var release = StageIncompletePythonRelease("9.9.9", "cc-pdf");
        var result = await new PythonToolsInstaller(_layout).InstallAsync(release, new ReleaseSource());

        Assert.False(result.Success);
        Assert.Contains("standard library", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(sentinel), "the live base Python was disturbed by a failed rebuild");
        Assert.Equal("do-not-delete", File.ReadAllText(sentinel));
        Assert.Null(InstalledManifest.Load(_layout).Get(PythonToolsInstaller.ComponentId));
    }

    // --- SharedInstallLock: cross-process serialization --------------------------------------------

    [Fact]
    public void SharedInstallLock_HeldByAnotherThread_TimesOut()
    {
        using var acquired = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        var holder = new Thread(() =>
        {
            using var held = SharedInstallLock.Acquire(TimeSpan.FromSeconds(5));
            acquired.Set();
            release.Wait(TimeSpan.FromSeconds(10));
        }) { IsBackground = true };
        holder.Start();

        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)), "holder never acquired the lock");
        // A second acquirer (this thread) must not be able to take it while it is held elsewhere.
        Assert.Throws<TimeoutException>(() => SharedInstallLock.Acquire(TimeSpan.FromMilliseconds(300)));

        release.Set();
        holder.Join(TimeSpan.FromSeconds(5));

        // Once released, it is acquirable again.
        using var reacquired = SharedInstallLock.Acquire(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Stage a local release whose assets are real, SHA-matching zips, but whose "python.exe" is a non-PE
    /// text file - so InstallAsync gets past download/extract and fails at the runtime verify of the STAGED
    /// Python (BasePythonRuns), before any swap. The tools bundle carries a minimal manifest + wheelhouse.
    /// </summary>
    private ResolvedRelease StageIncompletePythonRelease(string version, params string[] scripts)
    {
        var releaseDir = Path.Combine(_dir, "release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(releaseDir);
        var work = Path.Combine(_dir, "work-" + Guid.NewGuid().ToString("N"));

        // Python asset: a "python.exe" that is NOT a valid executable, so launching it throws and the runtime
        // probe reports the interpreter as dead (exactly the "missing standard library" shape from the field).
        var pyStage = Path.Combine(work, "py");
        Directory.CreateDirectory(pyStage);
        File.WriteAllText(Path.Combine(pyStage, "python.exe"), "not a real interpreter");
        var pyZip = Path.Combine(releaseDir, PythonToolsInstaller.PythonAsset);
        System.IO.Compression.ZipFile.CreateFromDirectory(pyStage, pyZip);

        // Tools asset: tools-manifest.json + an (empty) wheelhouse so the bundle parses.
        var toolsStage = Path.Combine(work, "tools");
        Directory.CreateDirectory(Path.Combine(toolsStage, "wheelhouse"));
        var toolsArr = string.Join(",", scripts.Select(s => $"{{\"dist\":\"{s}\",\"scripts\":[\"{s}\"]}}"));
        File.WriteAllText(Path.Combine(toolsStage, "tools-manifest.json"),
            $"{{\"bundleVersion\":\"{version}\",\"tools\":[{toolsArr}]}}");
        var toolsZip = Path.Combine(releaseDir, PythonToolsInstaller.ToolsAsset);
        System.IO.Compression.ZipFile.CreateFromDirectory(toolsStage, toolsZip);

        var json =
            "{\"version\":\"" + version + "\",\"assets\":{" +
            "\"cc-python-win-x64.zip\":{\"version\":\"" + version + "\",\"sha256\":\"" + Hashing.Sha256OfFile(pyZip) + "\",\"platform\":\"windows\",\"size\":" + new FileInfo(pyZip).Length + "}," +
            "\"cc-tools-pyenv-win-x64.zip\":{\"version\":\"" + version + "\",\"sha256\":\"" + Hashing.Sha256OfFile(toolsZip) + "\",\"platform\":\"windows\",\"size\":" + new FileInfo(toolsZip).Length + "}" +
            "}}";
        File.WriteAllText(Path.Combine(releaseDir, "release-manifest.json"), json);
        return ReleaseSource.LoadLocalReleaseDir(releaseDir);
    }
}
