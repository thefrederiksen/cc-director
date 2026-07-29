using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The unattended install path, end to end, against a local release directory.
///
/// NOTHING proved this before. There were tests for the command line tool's argument parsing and its
/// install scope, and none that ran an install and looked at what landed on disk - so every claim
/// that "the agent path works" rested on hope. This is the path a script or a coding agent takes
/// (<c>install --release-dir …</c>), and it is the same engine both wizards drive, so a break here is
/// a break in all three.
///
/// SCOPE, stated plainly: this exercises the ENGINE - plan, place, record, then remove. It does not
/// start a process, does not touch the network, and does not run the command line executable itself
/// (its parsing and its exit codes are pinned separately). What it does prove is that an install
/// places what it promised and an uninstall takes exactly that away.
///
/// Hermetic: a sandbox root per test, a fabricated release with real files and real hashes.
/// </summary>
public sealed class UnattendedInstallRoundTripTests : IDisposable
{
    private readonly string _dir;
    private readonly string _releaseDir;
    private readonly InstallLayout _layout;

    public UnattendedInstallRoundTripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "unattended-" + Guid.NewGuid().ToString("N"));
        _releaseDir = Path.Combine(_dir, "release");
        Directory.CreateDirectory(_releaseDir);
        _layout = new InstallLayout(Path.Combine(_dir, "cc-director"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup only */ }
    }

    /// <summary>A release directory holding the two components this installer places, with the real
    /// hashes the engine will verify.</summary>
    private ResolvedRelease BuildRelease(string version)
    {
        var assets = new Dictionary<string, object>();

        foreach (var component in new[] { ComponentRegistry.Director, ComponentRegistry.Launcher })
        {
            var assetName = OperatingSystem.IsWindows() ? component.WindowsAsset : component.MacAsset;
            if (assetName is null) continue;

            var path = Path.Combine(_releaseDir, assetName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"{component.Id}@{version}");
            assets[assetName] = new
            {
                version,
                sha256 = Hashing.Sha256OfFile(path),
                platform = OperatingSystem.IsWindows() ? "windows" : "macos",
                size = new FileInfo(path).Length,
            };
        }

        File.WriteAllText(
            Path.Combine(_releaseDir, "release-manifest.json"),
            JsonSerializer.Serialize(new { version, assets }));
        return ReleaseSource.LoadLocalReleaseDir(_releaseDir);
    }

    private static UpdatePlan PlanFor(ResolvedRelease release, IReadOnlyList<Component> components)
    {
        var items = new List<PlanItem>();
        foreach (var c in components)
        {
            var assetName = OperatingSystem.IsWindows() ? c.WindowsAsset : c.MacAsset;
            if (assetName is null) continue;
            var asset = release.Manifest.TryGetAsset(assetName);
            if (asset is null) continue;
            items.Add(new PlanItem(c.Id, PlanItemKind.Install, asset.Name, null, asset.Version, asset.Sha256));
        }
        return new UpdatePlan { Items = items };
    }

    /// <summary>Stages the asset from the local release directory - the same seam the real downloader
    /// fills from GitHub, which is what makes this hermetic.</summary>
    private UpdateRunner.Downloader FromReleaseDir() =>
        (item, _) =>
        {
            var source = Path.Combine(_releaseDir, item.AssetName);
            var staged = Path.Combine(_dir, "staged-" + Guid.NewGuid().ToString("N"));
            File.Copy(source, staged, overwrite: true);
            return Task.FromResult(staged);
        };

    private static int CountOf(UpdateRunResult r, ApplyStatus status) => r.Results.Count(x => x.Status == status);

    // The whole point: an unattended install puts the components on disk and RECORDS what it put
    // there, so a later status or update can tell what this machine has.
    [Fact]
    public async Task Install_PlacesEveryComponentAndRecordsTheVersion()
    {
        var release = BuildRelease("1.8.6");
        var components = ComponentRegistry.ForRole(ComponentRegistry.Apps, InstallRole.Workstation)
            .Where(c => (OperatingSystem.IsWindows() ? c.WindowsAsset : c.MacAsset) is not null)
            .ToList();

        var runner = new UpdateRunner(_layout, components, FromReleaseDir());
        var result = await runner.ApplyAsync(PlanFor(release, components));

        Assert.True(CountOf(result, ApplyStatus.Installed) + CountOf(result, ApplyStatus.Updated) > 0,
            "the install placed nothing at all");
        Assert.Equal(0, CountOf(result, ApplyStatus.Failed));

        var recorded = InstalledManifest.Load(_layout);
        foreach (var c in components)
        {
            var placed = _layout.PathFor(c);
            Assert.True(File.Exists(placed) || Directory.Exists(placed), $"{c.Id} is not at {placed}");
            Assert.Equal("1.8.6", recorded.Get(c.Id));
        }
    }

    // Running it twice must be safe and must not report phantom work. An agent that installs, checks,
    // and installs again is normal.
    [Fact]
    public async Task Install_RunTwice_IsSafeAndStaysRecordedAtTheSameVersion()
    {
        var release = BuildRelease("1.8.6");
        var components = ComponentRegistry.ForRole(ComponentRegistry.Apps, InstallRole.Workstation)
            .Where(c => (OperatingSystem.IsWindows() ? c.WindowsAsset : c.MacAsset) is not null)
            .ToList();
        var plan = PlanFor(release, components);

        var first = await new UpdateRunner(_layout, components, FromReleaseDir()).ApplyAsync(plan);
        var second = await new UpdateRunner(_layout, components, FromReleaseDir()).ApplyAsync(plan);

        Assert.Equal(0, CountOf(first, ApplyStatus.Failed));
        Assert.Equal(0, CountOf(second, ApplyStatus.Failed));
        var recorded = InstalledManifest.Load(_layout);
        foreach (var c in components)
            Assert.Equal("1.8.6", recorded.Get(c.Id));
    }

    // A corrupted asset must FAIL the install rather than place a file whose contents nobody vouched
    // for. The hash is the only thing standing between a truncated download and a broken machine.
    [Fact]
    public async Task Install_WithAWrongHash_Fails_AndPlacesNothing()
    {
        var release = BuildRelease("1.8.6");
        var components = ComponentRegistry.ForRole(ComponentRegistry.Apps, InstallRole.Workstation)
            .Where(c => (OperatingSystem.IsWindows() ? c.WindowsAsset : c.MacAsset) is not null)
            .Take(1)
            .ToList();

        var poisoned = new UpdatePlan
        {
            Items = PlanFor(release, components).Items
                .Select(i => new PlanItem(i.ComponentId, i.Kind, i.AssetName, i.FromVersion, i.ToVersion,
                                          new string('0', 64)))
                .ToList(),
        };

        var result = await new UpdateRunner(_layout, components, FromReleaseDir()).ApplyAsync(poisoned);

        Assert.True(CountOf(result, ApplyStatus.Failed) > 0, "a wrong hash must fail the install");
        Assert.Equal(0, CountOf(result, ApplyStatus.Installed) + CountOf(result, ApplyStatus.Updated));
        Assert.Null(InstalledManifest.Load(_layout).Get(components[0].Id));
    }

    // The round trip. An uninstall must take away exactly what the install placed - the state a
    // machine is left in is what the NEXT install has to cope with, and getting that wrong is what
    // left a Mac unable to install anything.
    [Fact]
    public async Task Uninstall_AfterAnUnattendedInstall_RemovesWhatWasPlaced()
    {
        var release = BuildRelease("1.8.6");
        var components = ComponentRegistry.ForRole(ComponentRegistry.Apps, InstallRole.Workstation)
            .Where(c => (OperatingSystem.IsWindows() ? c.WindowsAsset : c.MacAsset) is not null)
            .ToList();
        await new UpdateRunner(_layout, components, FromReleaseDir()).ApplyAsync(PlanFor(release, components));

        var placed = components.Select(c => _layout.PathFor(c)).ToList();
        Assert.All(placed, p => Assert.True(File.Exists(p) || Directory.Exists(p)));

        var report = new Uninstaller(_layout).Apply(InstallRole.Workstation);

        // The launcher stop reports against the REAL machine (is anything on the launcher port?), so
        // the report's overall success is not assertable here. What is assertable, and what matters,
        // is that the files this install placed are gone.
        Assert.NotEmpty(report.Steps);
        foreach (var p in placed)
            Assert.False(File.Exists(p) || Directory.Exists(p), $"{p} survived the uninstall");
    }
}
