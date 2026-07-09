using System.IO.Compression;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Handles ffmpeg's archive asset (ffmpeg-win-x64.zip): the pinned static Windows ffmpeg the Gateway
/// shells out to for the long-clip WebM/Opus -> PCM WAV transcode (issue #1139 / #1186). The single-file
/// Gateway exe carries NO loose content, so a delivery of only the exe leaves ffmpeg absent and every
/// over-budget non-WAV clip fails to transcode. The build ships ffmpeg as a side-car zip (the same
/// delivery pattern as the mobile app <see cref="MobilePackage"/> and the Cockpit
/// <see cref="CockpitAssetPackage"/>) that the setup engine unpacks BESIDE the Gateway exe - exactly
/// where <c>FfmpegAudioTranscoder.ResolveFfmpegPath</c> (<c>AppContext.BaseDirectory/ffmpeg.exe</c>,
/// see <see cref="InstallLayout.GatewayFfmpegPath"/>) looks.
///
/// UNLIKE the mobile app and Cockpit, ffmpeg.exe lands DIRECTLY in the Gateway dir (root), NOT a cleaned
/// <c>wwwroot</c> subdir - the Gateway exe and its wwwroot tree live in that same dir, so this extract
/// must NOT wipe the directory: it only overwrites the ffmpeg files. Kept separate from the Windows-only
/// tray work so extraction is testable on any OS without elevation.
/// </summary>
public static class FfmpegPackage
{
    /// <summary>The release asset carrying ffmpeg.exe (the pinned static Windows build, plus its LICENSE).</summary>
    public const string AssetName = "ffmpeg-win-x64.zip";

    /// <summary>The executable the zip's root carries and the transcoder resolves beside the Gateway exe.</summary>
    public const string ExeFile = "ffmpeg.exe";

    /// <summary>
    /// Download + SHA-256 verify the ffmpeg zip and extract it beside the Gateway exe (the clean-install
    /// path). Returns the placed ffmpeg.exe path, or <c>null</c> when the release carries no ffmpeg asset
    /// (a release that predates issue #1186 - the Gateway simply cannot transcode over-budget non-WAV
    /// clips, exactly as before). Throws on a SHA-256 mismatch or a missing ffmpeg.exe after extraction
    /// (no silent degrade).
    /// </summary>
    public static async Task<string?> ExtractAsync(
        InstallLayout layout, ResolvedRelease release, ReleaseSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(source);

        var asset = release.Manifest.TryGetAsset(AssetName);
        if (asset is null)
        {
            EngineLog.Write($"[FfmpegPackage] release has no {AssetName}; the Gateway cannot transcode long non-WAV clips (release predates #1186)");
            return null;
        }

        var staged = await source.DownloadAssetAsync(AssetName, release.DownloadUrls, ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(asset.Sha256) && !Hashing.Sha256Matches(staged, asset.Sha256))
                throw new InvalidOperationException("ffmpeg zip SHA-256 mismatch; download rejected.");

            var exePath = ExtractZip(staged, layout);
            EngineLog.Write($"[FfmpegPackage] extracted {AssetName} {asset.Version} -> {exePath}");
            return exePath;
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Extract an ALREADY-staged-and-verified ffmpeg zip (the self-update path: the running Gateway
    /// staged + SHA-verified it via <see cref="GatewayUpdater.StagedFfmpegZipPath"/> before launching the
    /// update helper, so no download or release source is needed here). Returns the placed ffmpeg.exe
    /// path, or <c>null</c> when no staged zip is present (a release without the ffmpeg asset - nothing to
    /// apply). Throws on a missing ffmpeg.exe after extraction.
    /// </summary>
    public static string? ExtractStagedZip(InstallLayout layout, string stagedZipPath)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedZipPath);

        if (!File.Exists(stagedZipPath))
        {
            EngineLog.Write($"[FfmpegPackage] no staged ffmpeg zip at {stagedZipPath}; leaving ffmpeg.exe unchanged");
            return null;
        }

        var exePath = ExtractZip(stagedZipPath, layout);
        EngineLog.Write($"[FfmpegPackage] applied staged {AssetName} -> {exePath}");
        return exePath;
    }

    /// <summary>
    /// Place the zip's ffmpeg files BESIDE the Gateway exe. The zip's root is ffmpeg.exe (plus its LICENSE
    /// for attribution), so it extracts directly into the Gateway dir. UNLIKE the mobile/Cockpit extract,
    /// the Gateway dir is NOT cleaned first - the Gateway exe and its wwwroot tree live here too, so this
    /// only overwrites the ffmpeg files. Asserts ffmpeg.exe landed (fail loud - a missing binary is a
    /// build/deploy error, matching the transcoder's own resolution contract).
    /// </summary>
    private static string ExtractZip(string zipPath, InstallLayout layout)
    {
        var gatewayDir = layout.GatewayDir;
        Directory.CreateDirectory(gatewayDir);

        ZipFile.ExtractToDirectory(zipPath, gatewayDir, overwriteFiles: true);

        var exePath = layout.GatewayFfmpegPath;
        if (!File.Exists(exePath))
            throw new InvalidOperationException($"ffmpeg {ExeFile} not found after extraction at {exePath}.");
        return exePath;
    }
}
