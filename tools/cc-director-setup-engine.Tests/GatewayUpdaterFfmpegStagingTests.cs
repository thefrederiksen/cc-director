using System.IO.Compression;
using System.Text.Json;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Issue #1186: when the running Gateway stages a newer exe for a self-update, it must ALSO stage the
/// matching ffmpeg zip next to it so the update helper can lay ffmpeg.exe beside the swapped exe with no
/// download (the single-file exe carries no loose content). These tests prove StageAsync stages the
/// ffmpeg zip, and that a release without the ffmpeg asset clears any stale staged zip.
/// </summary>
public class GatewayUpdaterFfmpegStagingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _releaseDir;
    private readonly InstallLayout _layout;

    public GatewayUpdaterFfmpegStagingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-gwffmpeg-" + Guid.NewGuid().ToString("N"));
        _releaseDir = Path.Combine(_dir, "release");
        Directory.CreateDirectory(_releaseDir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>A release with a newer Gateway exe (version 0.5.0) and, optionally, the ffmpeg zip.</summary>
    private ResolvedRelease BuildRelease(bool includeFfmpeg)
    {
        var gwAsset = Path.Combine(_releaseDir, ComponentRegistry.Gateway.WindowsAsset);
        File.WriteAllText(gwAsset, "gateway-v2");
        var assets = new Dictionary<string, object>
        {
            [ComponentRegistry.Gateway.WindowsAsset] =
                new { version = "0.5.0", sha256 = Hashing.Sha256OfFile(gwAsset), platform = "windows", size = new FileInfo(gwAsset).Length },
        };

        if (includeFfmpeg)
        {
            var zipPath = BuildFfmpegZip(Path.Combine(_releaseDir, FfmpegPackage.AssetName));
            assets[FfmpegPackage.AssetName] =
                new { version = "0.5.0", sha256 = Hashing.Sha256OfFile(zipPath), platform = "windows", size = new FileInfo(zipPath).Length };
        }

        File.WriteAllText(Path.Combine(_releaseDir, "release-manifest.json"),
            JsonSerializer.Serialize(new { version = "0.5.0", assets }));
        return ReleaseSource.LoadLocalReleaseDir(_releaseDir);
    }

    private string BuildFfmpegZip(string zipPath)
    {
        var payload = Path.Combine(_dir, "f-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(payload, FfmpegPackage.ExeFile), "MZ-fake-ffmpeg");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(payload, zipPath);
        Directory.Delete(payload, recursive: true);
        return zipPath;
    }

    /// <summary>Mark the Gateway as installed at an OLDER version so an update is available.</summary>
    private void MarkGatewayInstalled(string version)
    {
        var exe = _layout.PathFor(ComponentRegistry.Gateway);
        Directory.CreateDirectory(Path.GetDirectoryName(exe) ?? _layout.GatewayDir);
        File.WriteAllText(exe, "installed-gateway");
        var m = InstalledManifest.Load(_layout);
        m.Set(ComponentRegistry.Gateway.Id, version);
        m.Save(_layout);
    }

    [Fact]
    public async Task StageAsync_StagesFfmpegZip_AlongsideExe()
    {
        MarkGatewayInstalled("0.4.0");
        var release = BuildRelease(includeFfmpeg: true);
        var updater = new GatewayUpdater(_layout);

        var staged = await updater.StageAsync(release, new ReleaseSource());

        Assert.NotNull(staged);
        Assert.True(File.Exists(updater.StagedExePath), "the new Gateway exe should be staged");
        Assert.True(File.Exists(updater.StagedFfmpegZipPath), "the matching ffmpeg zip should be staged beside it");
    }

    [Fact]
    public async Task StageAsync_NoFfmpegAsset_RemovesStaleStagedZip()
    {
        MarkGatewayInstalled("0.4.0");
        var updater = new GatewayUpdater(_layout);
        // A stale staged zip from a prior release must not be applied over a newer exe.
        Directory.CreateDirectory(Path.GetDirectoryName(updater.StagedFfmpegZipPath) ?? _layout.StateDir);
        File.WriteAllText(updater.StagedFfmpegZipPath, "stale");

        var staged = await updater.StageAsync(BuildRelease(includeFfmpeg: false), new ReleaseSource());

        Assert.NotNull(staged);
        Assert.True(File.Exists(updater.StagedExePath));
        Assert.False(File.Exists(updater.StagedFfmpegZipPath), "the stale staged ffmpeg zip should be removed");
    }
}
