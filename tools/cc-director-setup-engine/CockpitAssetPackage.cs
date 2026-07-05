using System.IO.Compression;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Handles the React desktop Cockpit's archive asset (devthrottle-gateway-cockpit-win-x64.zip): the
/// built React app (epic #967 cutover, issue #979) the Gateway serves at the site root <c>/</c>. The
/// single-file Gateway exe carries NO loose content, so a delivery of only the exe drops the Cockpit
/// and every UI path answers 404 on an installed / self-updated Gateway. The build ships the Cockpit
/// as a side-car zip (the same delivery pattern as the mobile app, <see cref="MobilePackage"/>) that
/// the setup engine unpacks into <c>wwwroot/c</c> BESIDE the Gateway exe - exactly where
/// <c>CockpitReactApp.WebRoot</c> (<c>AppContext.BaseDirectory/wwwroot/c</c>, see
/// <see cref="InstallLayout.GatewayCockpitDir"/>) looks. Kept separate from the Windows-only tray work
/// so extraction is testable on any OS without elevation.
/// </summary>
public static class CockpitAssetPackage
{
    /// <summary>The release asset carrying the built Cockpit (the contents of wwwroot/c).</summary>
    public const string AssetName = "devthrottle-gateway-cockpit-win-x64.zip";

    private const string IndexFile = "index.html";

    /// <summary>
    /// Download + SHA-256 verify the Cockpit zip and extract it into <c>wwwroot/c</c> beside the
    /// Gateway exe (the clean-install path). Returns the <c>wwwroot/c</c> directory, or <c>null</c>
    /// when the release carries no Cockpit asset (a release that predates the cutover - the Gateway
    /// simply serves no Cockpit). Throws on a SHA-256 mismatch or a missing <c>index.html</c> after
    /// extraction (no silent degrade).
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
            EngineLog.Write($"[CockpitAssetPackage] release has no {AssetName}; the Gateway will serve no Cockpit (release predates issue #979)");
            return null;
        }

        var staged = await source.DownloadAssetAsync(AssetName, release.DownloadUrls, ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(asset.Sha256) && !Hashing.Sha256Matches(staged, asset.Sha256))
                throw new InvalidOperationException("Cockpit zip SHA-256 mismatch; download rejected.");

            var dir = ExtractZip(staged, layout);
            EngineLog.Write($"[CockpitAssetPackage] extracted {AssetName} {asset.Version} -> {dir}");
            return dir;
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Extract an ALREADY-staged-and-verified Cockpit zip (the self-update path: the running Gateway
    /// staged + SHA-verified it via <see cref="GatewayUpdater.StagedCockpitZipPath"/> before launching
    /// the update helper, so no download or release source is needed here). Returns the
    /// <c>wwwroot/c</c> directory, or <c>null</c> when no staged zip is present (a release without the
    /// Cockpit asset - nothing to apply). Throws on a missing <c>index.html</c> after extraction.
    /// </summary>
    public static string? ExtractStagedZip(InstallLayout layout, string stagedZipPath)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedZipPath);

        if (!File.Exists(stagedZipPath))
        {
            EngineLog.Write($"[CockpitAssetPackage] no staged Cockpit zip at {stagedZipPath}; leaving wwwroot/c unchanged");
            return null;
        }

        var dir = ExtractZip(stagedZipPath, layout);
        EngineLog.Write($"[CockpitAssetPackage] applied staged {AssetName} -> {dir}");
        return dir;
    }

    /// <summary>
    /// Replace <c>wwwroot/c</c> beside the Gateway exe with the zip's contents. The zip's root is the
    /// contents of <c>wwwroot/c</c> (<c>index.html</c> + the hashed <c>assets/</c>), so it extracts
    /// directly into the Cockpit dir. Cleans the target first so a re-install never leaves a stale
    /// hashed asset behind.
    /// </summary>
    private static string ExtractZip(string zipPath, InstallLayout layout)
    {
        var cockpitDir = layout.GatewayCockpitDir;
        if (Directory.Exists(cockpitDir))
            Directory.Delete(cockpitDir, recursive: true);
        Directory.CreateDirectory(cockpitDir);

        ZipFile.ExtractToDirectory(zipPath, cockpitDir, overwriteFiles: true);

        var index = Path.Combine(cockpitDir, IndexFile);
        if (!File.Exists(index))
            throw new InvalidOperationException($"Cockpit {IndexFile} not found after extraction at {index}.");
        return cockpitDir;
    }
}
