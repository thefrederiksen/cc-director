using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>The outcome of placing the macOS Director .app.</summary>
public sealed record MacAppResult(bool Success, string Message, string? Version);

/// <summary>
/// Places the Director on macOS. The generic UpdateRunner places single-file exes and skips archives,
/// so the Director (shipped as cc-director-mac-arm64.zip containing "Director.app") needs this
/// dedicated step - the analog of MobilePackage's side-car-zip handling. It downloads + SHA-256 verifies the zip,
/// extracts the .app with ditto (preserving the bundle's symlinks + exec bits), swaps it into
/// ~/Applications, strips the Gatekeeper quarantine, and marks the launcher executable. Mirrors
/// UpdateInstaller.SwapMac so a fresh install and an auto-update converge on the same on-disk result.
/// </summary>
public static class MacAppPlacer
{
    public const string DirectorAsset = "cc-director-mac-arm64.zip";
    private const string AppName = "Director.app";

    /// <summary>The pre-rename bundle name (issue #1821 alias). A fresh place removes any copy of this
    /// so a host never ends up with both "CC Director.app" and "Director.app".</summary>
    private const string LegacyAppName = "CC Director.app";

    [SupportedOSPlatform("macos")]
    public static async Task<MacAppResult> PlaceAsync(
        InstallLayout layout, ResolvedRelease release, ReleaseSource source,
        Action<string>? log = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(source);
        void Log(string m) => (log ?? (_ => { }))(m);

        var asset = release.Manifest.TryGetAsset(DirectorAsset);
        if (asset is null) return new MacAppResult(false, $"release is missing {DirectorAsset}.", null);

        string? zip = null, stage = null;
        try
        {
            Log($"downloading {DirectorAsset}");
            zip = await source.DownloadAssetAsync(DirectorAsset, release.DownloadUrls, ct);
            if (!Hashing.Sha256Matches(zip, asset.Sha256))
                return new MacAppResult(false, $"{DirectorAsset} SHA-256 mismatch; download rejected.", null);

            stage = Path.Combine(Path.GetTempPath(), $"cc-director-app-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stage);
            // ditto -x -k extracts a PKZip while preserving the .app's symlinks and permissions.
            var (exExit, exOut) = ProcessRunner.Run("/usr/bin/ditto", $"-x -k \"{zip}\" \"{stage}\"");
            if (exExit != 0) return new MacAppResult(false, $"extracting {DirectorAsset} failed: {Trim(exOut)}", null);

            var stagedApp = Path.Combine(stage, AppName);
            if (!Directory.Exists(stagedApp))
                return new MacAppResult(false, $"{AppName} not found inside {DirectorAsset}.", null);

            var target = layout.PathFor(ComponentRegistry.Director); // ~/Applications/Director.app
            Directory.CreateDirectory(layout.MacAppsDir);

            // Collapse the pile-up (issue #1821 rename): before placing the one canonical bundle,
            // remove every stale copy the old distribution model could have left - the legacy
            // "CC Director.app", Finder's auto-suffixed duplicates ("CC Director 2.app", "Director 2.app"),
            // and any copy dragged into the system /Applications instead of ~/Applications. This is what
            // makes reinstalling upgrade-in-place instead of stacking a new icon beside the old ones.
            PurgeStaleBundles(layout, keep: target, Log);

            Log($"installing {AppName} to {layout.MacAppsDir}");
            // Build beside, then this is a fresh place: remove any existing app, then ditto in.
            ProcessRunner.Run("/bin/rm", $"-rf \"{target}\"");
            var (cpExit, cpOut) = ProcessRunner.Run("/usr/bin/ditto", $"\"{stagedApp}\" \"{target}\"");
            if (cpExit != 0) return new MacAppResult(false, $"installing {AppName} failed: {Trim(cpOut)}", null);

            // Post-place: de-quarantine + ensure the launcher binary is executable (mirrors SwapMac).
            ProcessRunner.Run("/usr/bin/xattr", $"-dr com.apple.quarantine \"{target}\"");
            ProcessRunner.Run("/bin/chmod", $"+x \"{Path.Combine(target, "Contents", "MacOS", "cc-director")}\"");

            // Record the Director version for the updater.
            var im = InstalledManifest.Load(layout);
            im.Set(ComponentRegistry.Director.Id, asset.Version);
            im.Save(layout);

            Log($"Director {asset.Version} installed to {target}");
            return new MacAppResult(true, $"Director {asset.Version} installed to {target}.", asset.Version);
        }
        finally
        {
            try { if (zip is not null && File.Exists(zip)) File.Delete(zip); } catch { /* best-effort */ }
            try { if (stage is not null && Directory.Exists(stage)) Directory.Delete(stage, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Remove stale Director bundles the old loose-zip distribution could have left, so a reinstall
    /// converges on the single <paramref name="keep"/> bundle instead of stacking icons. Scans both the
    /// per-user ~/Applications and the system /Applications for the current and legacy bundle names plus
    /// Finder's numbered duplicates ("Director 2.app", "CC Director 3.app"). The <paramref name="keep"/>
    /// path is never removed here - the caller replaces it in place immediately after. Best-effort:
    /// a copy under /Applications the user cannot delete without admin is logged and skipped, not fatal.
    /// </summary>
    private static void PurgeStaleBundles(InstallLayout layout, string keep, Action<string> log)
    {
        var dirs = new[] { layout.MacAppsDir, "/Applications" };
        var baseNames = new[] { "Director", "CC Director" };

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var baseName in baseNames)
            {
                // The exact bundle plus Finder's " N" suffixed duplicates (space + digits), e.g.
                // "Director.app", "Director 2.app", "CC Director 10.app".
                foreach (var bundle in Directory.EnumerateDirectories(dir, $"{baseName}*.app"))
                {
                    var name = Path.GetFileName(bundle);
                    if (!IsBundleName(name, baseName)) continue;
                    if (string.Equals(Path.GetFullPath(bundle), Path.GetFullPath(keep), StringComparison.Ordinal))
                        continue; // the caller replaces this one in place.

                    var (exit, out_) = ProcessRunner.Run("/bin/rm", $"-rf \"{bundle}\"");
                    if (exit == 0) log($"removed stale bundle {bundle}");
                    else log($"could not remove {bundle} (needs admin?); skipping: {Trim(out_)}");
                }
            }
        }
    }

    /// <summary>True for "&lt;base&gt;.app" or Finder's "&lt;base&gt; N.app" duplicate form, and nothing else -
    /// so "Director.app"/"Director 2.app" match "Director" but an unrelated "Directory.app" does not.</summary>
    private static bool IsBundleName(string name, string baseName)
    {
        if (string.Equals(name, $"{baseName}.app", StringComparison.Ordinal)) return true;
        if (!name.StartsWith($"{baseName} ", StringComparison.Ordinal) ||
            !name.EndsWith(".app", StringComparison.Ordinal)) return false;
        var middle = name.Substring(baseName.Length + 1, name.Length - baseName.Length - 1 - ".app".Length);
        return middle.Length > 0 && middle.All(char.IsDigit);
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] : s;
}
