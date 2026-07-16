using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class InstalledManifestTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public InstalledManifestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-im-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Load_Absent_IsEmpty()
    {
        var m = InstalledManifest.Load(_layout);
        Assert.Null(m.Get("cc-pdf"));
        Assert.Empty(m.Entries);
    }

    [Fact]
    public void SaveLoad_RoundTrips_CaseInsensitive()
    {
        var m = InstalledManifest.Load(_layout);
        m.Set("cc-pdf", "1.2.3");
        m.Set("director", "0.3.7");
        m.Save(_layout);

        Assert.True(File.Exists(_layout.InstalledManifestPath));
        var restored = InstalledManifest.Load(_layout);
        Assert.Equal("1.2.3", restored.Get("cc-pdf"));
        Assert.Equal("1.2.3", restored.Get("CC-PDF"));   // case-insensitive
        Assert.Equal("0.3.7", restored.Get("director"));
    }

    [Fact]
    public void Load_CorruptFile_IsEmpty_DoesNotThrow()
    {
        Directory.CreateDirectory(_layout.SetupStateDir);
        File.WriteAllText(_layout.InstalledManifestPath, "{ not valid json");
        var m = InstalledManifest.Load(_layout);
        Assert.Empty(m.Entries);
    }

    [Fact]
    public void Remove_ForgetsEntry()
    {
        var m = InstalledManifest.Empty();
        m.Set("cc-pdf", "1.0.0");
        Assert.True(m.Remove("cc-pdf"));
        Assert.Null(m.Get("cc-pdf"));
    }

    [Fact]
    public void Reader_PrefersRecordedVersion_OverOlderFileStamp()
    {
        // Manifest says cc-pdf is 2.0.0; the file stamp reads older (1.5.0). Recorded wins -
        // the record is written at placement time from the release and is the reliable form.
        var manifest = InstalledManifest.Empty();
        manifest.Set("cc-pdf", "2.0.0");
        var pdf = ComponentRegistry.ToolComponent("cc-pdf");

        var reader = new InstalledStateReader(
            _layout,
            fileExists: _ => true,
            readVersion: _ => "1.5.0",
            installed: manifest);

        var state = reader.Read(pdf);
        Assert.True(state.Present);
        Assert.Equal("2.0.0", state.Version);
    }

    [Fact]
    public void Reader_SelfUpdatedBinary_ReportsTheOnDiskVersion()
    {
        // Issue #1740: the Director self-updates in place without updating installed.json, so the
        // record goes stale (says 1.0.7) while the binary on disk is genuinely newer (1.4.0). A
        // strictly newer readable on-disk version must win, or status under-reports and plan
        // proposes a redundant re-download of a build the machine already runs.
        var manifest = InstalledManifest.Empty();
        manifest.Set("director", "1.0.7");

        var reader = new InstalledStateReader(
            _layout,
            fileExists: _ => true,
            readVersion: _ => "1.4.0+003490b0c6b361569e03ece5e9d68ad7b76c6449",
            installed: manifest);

        var state = reader.Read(ComponentRegistry.Director);
        Assert.True(state.Present);
        Assert.Equal("1.4.0+003490b0c6b361569e03ece5e9d68ad7b76c6449", state.Version);
    }

    [Fact]
    public void Reader_EqualVersionsDifferingOnlyInBuildMetadata_KeepTheRecordedForm()
    {
        // "1.4.0" recorded versus "1.4.0+sha" stamped are the SAME version - formatting noise must
        // not trigger the self-update override, so the clean recorded form is what gets reported.
        var manifest = InstalledManifest.Empty();
        manifest.Set("director", "1.4.0");

        var reader = new InstalledStateReader(
            _layout,
            fileExists: _ => true,
            readVersion: _ => "1.4.0+abcdef123456",
            installed: manifest);

        Assert.Equal("1.4.0", reader.Read(ComponentRegistry.Director).Version);
    }

    [Fact]
    public void Reader_MacBundleDirectoryWithNoManifestEntry_ReportsBundleVersion()
    {
        // Issue #1736 regression shape: a macOS machine whose Director exists only as the
        // "CC Director.app" bundle, with no manifest entry (installed before the manifest
        // existed). It must read as PRESENT with the bundle's Info.plist version - the wizard's
        // old private detector reported exactly this machine as "not installed". (That the
        // presence check accepts a directory is covered by the DefaultExists tests.)
        var reader = new InstalledStateReader(
            _layout,
            fileExists: _ => true,
            readVersion: _ => "1.1.0",
            installed: InstalledManifest.Empty());

        var state = reader.Read(ComponentRegistry.Director);
        Assert.True(state.Present);
        Assert.Equal("1.1.0", state.Version);
    }

    [Fact]
    public void Reader_FallsBackToFileStamp_WhenNotRecorded()
    {
        var pdf = ComponentRegistry.ToolComponent("cc-pdf");
        var reader = new InstalledStateReader(
            _layout,
            fileExists: _ => true,
            readVersion: _ => "9.9.9",
            installed: InstalledManifest.Empty());

        Assert.Equal("9.9.9", reader.Read(pdf).Version);
    }

    [Fact]
    public void Reader_AbsentFile_IsNotPresent_RegardlessOfManifest()
    {
        // Even if the manifest still lists a version (e.g. stale after a manual delete), an absent
        // file means not-present - the file-existence gate dominates.
        var manifest = InstalledManifest.Empty();
        manifest.Set("cc-pdf", "2.0.0");
        var pdf = ComponentRegistry.ToolComponent("cc-pdf");
        var reader = new InstalledStateReader(_layout, fileExists: _ => false, readVersion: _ => null, installed: manifest);

        var state = reader.Read(pdf);
        Assert.False(state.Present);
        Assert.Null(state.Version);
    }
}
