using System.Net.Http;

namespace CcDirector.Setup.Engine;

/// <summary>The outcome of one launcher rescue check.</summary>
public enum LauncherRescueOutcome
{
    /// <summary>The launcher answered its health endpoint; nothing to do.</summary>
    Healthy,
    /// <summary>The launcher was down and a plain restart brought it back.</summary>
    Restarted,
    /// <summary>The launcher was dead but has not been dead long enough to replace; observing.</summary>
    Observing,
    /// <summary>A fresh binary was placed (previous kept as ".old") and came up healthy.</summary>
    Replaced,
    /// <summary>The fresh binary did not come up; the ".old" backup was restored and the version pinned.</summary>
    RolledBack,
    /// <summary>The rescue could not run (no installed launcher record, or a step failed hard).</summary>
    Skipped,
}

/// <summary>The result of one rescue check: what happened and the steps taken, for the log.</summary>
public sealed record LauncherRescueResult(LauncherRescueOutcome Outcome, string Message, IReadOnlyList<string> Steps);

/// <summary>
/// The Director's side of the mutual-update pact (the CC Launcher mission, part 3.3): if the
/// launcher's health endpoint stays dead past a threshold, or its binary is missing entirely,
/// the Director replaces the launcher with a freshly downloaded, hash-verified build - placed
/// rename-based (the previous binary survives as ".old") and started so launchd (macOS) or the
/// tray (Windows) owns it again. If the fresh build does not come up either, the ".old" backup
/// is restored and the bad version pinned so the rescue never loops on a known-bad release.
///
/// Rescue only, never install: a machine whose launcher was never installed is left alone
/// (installing is the installer's job). One instance lives for the Director's process lifetime -
/// it carries the dead-since observation between periodic checks.
///
/// Every effect is an injectable delegate so the decision flow is unit-testable without a real
/// launcher, launchd, or network.
/// </summary>
public sealed class LauncherRescuer
{
    /// <summary>How long the health endpoint must stay dead (with the binary present) before the launcher is replaced.</summary>
    public static readonly TimeSpan DefaultDeadThreshold = TimeSpan.FromMinutes(10);

    private readonly InstallLayout _layout;
    private readonly TimeSpan _deadThreshold;
    private readonly Func<CancellationToken, Task<bool>> _isHealthy;
    private readonly Func<CancellationToken, Task<LauncherInstallResult>> _startInstalled;
    private readonly Func<CancellationToken, Task<(string Path, string Version)?>> _fetchFreshBinary;
    private readonly Func<DateTime> _utcNow;

    private DateTime? _deadSince;

    public LauncherRescuer(
        InstallLayout? layout = null,
        TimeSpan? deadThreshold = null,
        Func<CancellationToken, Task<bool>>? isHealthy = null,
        Func<CancellationToken, Task<LauncherInstallResult>>? startInstalled = null,
        Func<CancellationToken, Task<(string Path, string Version)?>>? fetchFreshBinary = null,
        Func<DateTime>? utcNow = null)
    {
        _layout = layout ?? InstallLayout.Default();
        _deadThreshold = deadThreshold ?? DefaultDeadThreshold;
        _isHealthy = isHealthy ?? ProbeHealthAsync;
        _startInstalled = startInstalled ?? StartInstalledLauncherAsync;
        _fetchFreshBinary = fetchFreshBinary ?? FetchLatestLauncherAsync;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// One periodic check: probe the launcher, and rescue it when it is missing or has been
    /// dead past the threshold. Never throws; failures are logged and reported in the result.
    /// </summary>
    public async Task<LauncherRescueResult> CheckAndRescueAsync(CancellationToken ct = default)
    {
        var steps = new List<string>();
        try
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
                return new LauncherRescueResult(LauncherRescueOutcome.Skipped, "Unsupported operating system.", steps);

            if (await _isHealthy(ct))
            {
                _deadSince = null;
                return new LauncherRescueResult(LauncherRescueOutcome.Healthy, "Launcher is healthy.", steps);
            }

            // Rescue only, never install: no installed-version record means the launcher was
            // never installed on this machine, and that is not ours to change.
            var installedVersion = InstalledManifest.Load(_layout).Get(ComponentRegistry.Launcher.Id);
            if (installedVersion is null)
                return new LauncherRescueResult(LauncherRescueOutcome.Skipped, "Launcher is not installed; rescue does not install.", steps);

            var binary = _layout.PathFor(ComponentRegistry.Launcher);
            var binaryMissing = !File.Exists(binary);
            var now = _utcNow();
            _deadSince ??= now;
            steps.Add($"launcher dead (since {_deadSince:HH:mm:ss}Z), binary {(binaryMissing ? "MISSING" : "present")} at {binary}");
            EngineLog.Write($"[LauncherRescuer] {steps[^1]}");

            // A dead launcher whose binary is fine may just need a start (a crash loop launchd
            // gave up on, or a clean exit nothing restarted). Try that before replacing bytes.
            if (!binaryMissing)
            {
                var restart = await _startInstalled(ct);
                steps.AddRange(restart.Steps);
                if (restart.Success)
                {
                    _deadSince = null;
                    EngineLog.Write("[LauncherRescuer] plain restart brought the launcher back");
                    return new LauncherRescueResult(LauncherRescueOutcome.Restarted, "Launcher was down; a restart brought it back.", steps);
                }

                if (now - _deadSince < _deadThreshold)
                {
                    steps.Add($"dead for {(now - _deadSince.Value).TotalMinutes:F1} minutes, below the {_deadThreshold.TotalMinutes:F0}-minute replace threshold; observing");
                    return new LauncherRescueResult(LauncherRescueOutcome.Observing, "Launcher is down; restart failed; waiting for the replace threshold.", steps);
                }
            }

            // Replace: download and verify the latest launcher, place it rename-based (the
            // previous binary survives as ".old"), and start it.
            var fresh = await _fetchFreshBinary(ct);
            if (fresh is null)
                return new LauncherRescueResult(LauncherRescueOutcome.Skipped, "No launcher asset available to rescue with (no release asset for this operating system, or the latest version is pinned as bad).", steps);

            var backup = InstallSwapper.Place(binary, fresh.Value.Path);
            steps.Add($"placed fresh launcher {fresh.Value.Version} (backup: {backup ?? "none - target was missing"})");
            EngineLog.Write($"[LauncherRescuer] placed fresh launcher {fresh.Value.Version} at {binary}");

            var start = await _startInstalled(ct);
            steps.AddRange(start.Steps);
            if (start.Success)
            {
                _deadSince = null;
                var manifest = InstalledManifest.Load(_layout);
                manifest.Set(ComponentRegistry.Launcher.Id, fresh.Value.Version);
                manifest.Save(_layout);
                EngineLog.Write($"[LauncherRescuer] rescue succeeded: launcher {fresh.Value.Version} is healthy");
                return new LauncherRescueResult(LauncherRescueOutcome.Replaced, $"Launcher replaced with {fresh.Value.Version} and healthy.", steps);
            }

            // The fresh build did not come up either: restore the backup, pin the bad version so
            // the rescue never loops on it, and start whatever is restored.
            steps.Add($"fresh launcher {fresh.Value.Version} did NOT come up; restoring the .old backup and pinning the version");
            EngineLog.Write($"[LauncherRescuer] fresh launcher {fresh.Value.Version} unhealthy; rolling back");
            var restored = InstallSwapper.Rollback(binary);
            var pins = PinStore.Load(_layout);
            pins.Pin(ComponentRegistry.Launcher.Id, fresh.Value.Version);
            PinStore.Save(_layout, pins);
            var afterRollback = await _startInstalled(ct);
            steps.AddRange(afterRollback.Steps);
            steps.Add(restored
                ? $"restored the previous launcher (healthy={afterRollback.Success})"
                : "ROLLBACK had no .old backup to restore");
            return new LauncherRescueResult(LauncherRescueOutcome.RolledBack,
                $"Fresh launcher {fresh.Value.Version} failed; rolled back (healthy after rollback={afterRollback.Success}).", steps);
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[LauncherRescuer] CheckAndRescueAsync FAILED: {ex.Message}");
            steps.Add($"rescue FAILED: {ex.Message}");
            return new LauncherRescueResult(LauncherRescueOutcome.Skipped, $"Rescue failed: {ex.Message}", steps);
        }
    }

    /// <summary>Probe the launcher's public health endpoint on the default port.</summary>
    private async Task<bool> ProbeHealthAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var resp = await http.GetAsync(
                $"http://127.0.0.1:{LauncherTrayInstaller.LauncherDefaultPort}/healthz", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Start the already-placed launcher via the platform installer (kickstart or tray start plus health wait).</summary>
    private Task<LauncherInstallResult> StartInstalledLauncherAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS())
            return new LauncherMacInstaller(_layout).InstallAsync(ct);
        if (OperatingSystem.IsWindows())
            return new LauncherTrayInstaller(_layout).InstallAsync(ct);
        throw new PlatformNotSupportedException("The launcher rescue runs on Windows and macOS only.");
    }

    /// <summary>
    /// Download and SHA-256 verify the latest release's launcher binary, returning its staged
    /// path and version - or null when the release has no launcher for this operating system or
    /// the latest version is pinned as a known-bad build.
    /// </summary>
    private async Task<(string Path, string Version)?> FetchLatestLauncherAsync(CancellationToken ct)
    {
        var assetName = LauncherUpdater.AssetNameForThisOs();
        if (assetName is null) return null;

        var source = new ReleaseSource();
        var release = await source.FetchLatestAsync(ct);
        var asset = release.Manifest.TryGetAsset(assetName);
        if (asset is null)
        {
            EngineLog.Write($"[LauncherRescuer] latest release has no {assetName}; cannot rescue");
            return null;
        }
        if (PinStore.Load(_layout).IsPinned(ComponentRegistry.Launcher.Id, asset.Version))
        {
            EngineLog.Write($"[LauncherRescuer] latest launcher {asset.Version} is pinned as a failed build; not re-downloading it");
            return null;
        }

        var downloaded = await source.DownloadAssetAsync(asset.Name, release.DownloadUrls, ct);
        if (!Hashing.Sha256Matches(downloaded, asset.Sha256))
        {
            try { File.Delete(downloaded); } catch { /* best effort */ }
            throw new InvalidOperationException($"Launcher asset {asset.Name} SHA-256 mismatch; rescue aborted.");
        }
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(downloaded,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return (downloaded, asset.Version);
    }
}
