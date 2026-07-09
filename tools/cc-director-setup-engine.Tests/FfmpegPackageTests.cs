using System.IO.Compression;
using System.Text.Json;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Issue #1186: the pinned ffmpeg ships as a side-car zip the setup engine unpacks BESIDE the Gateway
/// exe (Gateway dir root), so the long-clip WebM/Opus -> PCM WAV transcode (issue #1139) works on a
/// clean install / self-update / redeploy with no manual copy. These tests prove the extract contract:
/// lands ffmpeg.exe where the transcoder resolves it, does NOT wipe the Gateway dir (the exe + wwwroot
/// live there, unlike the cleaned mobile/Cockpit subdirs), verifies the SHA, and tolerates a release
/// without the asset.
/// </summary>
public class FfmpegPackageTests : IDisposable
{
    private readonly string _dir;
    private readonly string _releaseDir;
    private readonly InstallLayout _layout;

    public FfmpegPackageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-ffmpeg-" + Guid.NewGuid().ToString("N"));
        _releaseDir = Path.Combine(_dir, "release");
        Directory.CreateDirectory(_releaseDir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>Build a real ffmpeg zip (ffmpeg.exe + LICENSE) and a manifest in a local release dir.</summary>
    private string BuildReleaseDir(string exeBytes = "MZ-fake-ffmpeg")
    {
        var zipPath = BuildFfmpegZip(Path.Combine(_releaseDir, FfmpegPackage.AssetName), exeBytes);
        var sha = Hashing.Sha256OfFile(zipPath);
        var manifest = new
        {
            version = "0.5.0",
            assets = new Dictionary<string, object>
            {
                [FfmpegPackage.AssetName] = new { version = "0.5.0", sha256 = sha, platform = "windows", size = new FileInfo(zipPath).Length },
            },
        };
        File.WriteAllText(Path.Combine(_releaseDir, "release-manifest.json"), JsonSerializer.Serialize(manifest));
        return _releaseDir;
    }

    /// <summary>The zip's root is ffmpeg.exe (+ its license), matching release.yml's packaging.</summary>
    private string BuildFfmpegZip(string zipPath, string exeBytes = "MZ-fake-ffmpeg", bool includeExe = true)
    {
        var payload = Path.Combine(_dir, "payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(payload);
        if (includeExe) File.WriteAllText(Path.Combine(payload, FfmpegPackage.ExeFile), exeBytes);
        File.WriteAllText(Path.Combine(payload, "LICENSE-ffmpeg.txt"), "GPL");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        ZipFile.CreateFromDirectory(payload, zipPath);
        Directory.Delete(payload, recursive: true);
        return zipPath;
    }

    [Fact]
    public async Task ExtractAsync_PlacesFfmpegExe_BesideTheExe()
    {
        var release = ReleaseSource.LoadLocalReleaseDir(BuildReleaseDir());

        var exePath = await FfmpegPackage.ExtractAsync(_layout, release, new ReleaseSource());

        Assert.NotNull(exePath);
        Assert.Equal(_layout.GatewayFfmpegPath, exePath);
        Assert.True(File.Exists(_layout.GatewayFfmpegPath));
        // ffmpeg.exe lands in the Gateway dir ROOT (beside where the exe is placed), NOT a subdir.
        Assert.Equal(_layout.GatewayDir, Path.GetDirectoryName(_layout.GatewayFfmpegPath));
    }

    [Fact]
    public async Task ExtractAsync_DoesNotWipeGatewayDir_ExeAndWwwrootSurvive()
    {
        // The Gateway exe and its wwwroot tree live in the SAME dir ffmpeg unpacks into, so the extract
        // must NOT clean the directory (the key difference from the mobile/Cockpit extracts).
        Directory.CreateDirectory(_layout.GatewayMobileDir);
        File.WriteAllText(_layout.PathFor(ComponentRegistry.Gateway), "installed-gateway-exe");
        File.WriteAllText(Path.Combine(_layout.GatewayMobileDir, "index.html"), "mobile");

        var release = ReleaseSource.LoadLocalReleaseDir(BuildReleaseDir());
        await FfmpegPackage.ExtractAsync(_layout, release, new ReleaseSource());

        Assert.True(File.Exists(_layout.GatewayFfmpegPath), "ffmpeg.exe should land beside the exe");
        Assert.Equal("installed-gateway-exe", File.ReadAllText(_layout.PathFor(ComponentRegistry.Gateway)));
        Assert.True(File.Exists(Path.Combine(_layout.GatewayMobileDir, "index.html")), "wwwroot/m must survive");
    }

    [Fact]
    public async Task ExtractAsync_NoFfmpegAsset_ReturnsNull()
    {
        // A manifest with no ffmpeg asset (a release that predates #1186).
        File.WriteAllText(Path.Combine(_releaseDir, "release-manifest.json"),
            "{\"version\":\"0.5.0\",\"assets\":{}}");
        var release = ReleaseSource.LoadLocalReleaseDir(_releaseDir);

        var exePath = await FfmpegPackage.ExtractAsync(_layout, release, new ReleaseSource());

        Assert.Null(exePath);
        Assert.False(File.Exists(_layout.GatewayFfmpegPath));
    }

    [Fact]
    public async Task ExtractAsync_ShaMismatch_Throws()
    {
        var dir = BuildReleaseDir();
        var manifestPath = Path.Combine(dir, "release-manifest.json");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"sha256\":\"", "\"sha256\":\"00"));
        var release = ReleaseSource.LoadLocalReleaseDir(dir);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => FfmpegPackage.ExtractAsync(_layout, release, new ReleaseSource()));
    }

    [Fact]
    public void ExtractStagedZip_AppliesStagedZip_BesideTheExe()
    {
        // The self-update path: an already-verified zip sitting at the staged path.
        var staged = BuildFfmpegZip(Path.Combine(_dir, "staged.zip"));

        var exePath = FfmpegPackage.ExtractStagedZip(_layout, staged);

        Assert.NotNull(exePath);
        Assert.Equal(_layout.GatewayFfmpegPath, exePath);
        Assert.True(File.Exists(_layout.GatewayFfmpegPath));
    }

    [Fact]
    public void ExtractStagedZip_NoStagedZip_ReturnsNull_LeavesFfmpegUntouched()
    {
        // A pre-existing ffmpeg.exe must survive when there is no staged zip (a release without the asset).
        Directory.CreateDirectory(_layout.GatewayDir);
        File.WriteAllText(_layout.GatewayFfmpegPath, "prior-ffmpeg");

        var exePath = FfmpegPackage.ExtractStagedZip(_layout, Path.Combine(_dir, "does-not-exist.zip"));

        Assert.Null(exePath);
        Assert.Equal("prior-ffmpeg", File.ReadAllText(_layout.GatewayFfmpegPath));
    }

    [Fact]
    public void ExtractStagedZip_ZipMissingFfmpegExe_Throws()
    {
        // A corrupt/incomplete zip that carries no ffmpeg.exe must fail loud (no silent degrade).
        var staged = BuildFfmpegZip(Path.Combine(_dir, "no-exe.zip"), includeExe: false);

        Assert.Throws<InvalidOperationException>(() => FfmpegPackage.ExtractStagedZip(_layout, staged));
    }
}
