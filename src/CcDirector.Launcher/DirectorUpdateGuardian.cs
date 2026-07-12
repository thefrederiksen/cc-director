using System.Net.Http;
using System.Text.Json;
using CcDirector.Core.Update;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

namespace CcDirector.Launcher;

/// <summary>What one guardian check decided and did.</summary>
public enum GuardianOutcome
{
    /// <summary>The Director is running the latest version (or there is no release to compare against).</summary>
    UpToDate,
    /// <summary>The Director was not running; it was started (any staged update applies itself at startup).</summary>
    StartedDirector,
    /// <summary>A newer version exists but the Director has not finished staging it yet; nothing to apply.</summary>
    WaitingForStage,
    /// <summary>The latest version is pinned as a failed update; it will never be re-applied.</summary>
    PinnedBad,
    /// <summary>The restart policy blocked the update; a "new version waiting" notice is surfaced instead.</summary>
    Blocked,
    /// <summary>The Director was restarted and came back healthy on the new version.</summary>
    Updated,
    /// <summary>The Director was restarted and is healthy, but still on the old version (its own startup logic declined or rolled back the update).</summary>
    ApplyDidNotStick,
    /// <summary>The Director stayed dead after the update restart; the ".old" bundle was renamed back, the version pinned, and the Director started.</summary>
    RescuedByRollback,
    /// <summary>The Director stayed dead and no backup existed to restore; a human is needed.</summary>
    Dead,
    /// <summary>The check could not run (unsupported operating system, or a hard failure - see the message).</summary>
    Skipped,
}

/// <summary>The result of one guardian check: the outcome and the steps taken, for the log and the status surface.</summary>
public sealed record GuardianResult(GuardianOutcome Outcome, string Message, IReadOnlyList<string> Steps);

/// <summary>
/// The launcher's side of the mutual-update pact (the CC Launcher mission, part 3.2): make a
/// staged Director update actually happen on an unattended machine. The Director stages
/// updates by itself but applies them only at its next startup - which, unattended, never
/// comes. This guardian closes the loop: when the Director runs an older version than the
/// latest release AND that update is staged, it restarts the Director inside the owner's
/// policy (every session idle or waiting, inside the nightly maintenance window, and only if
/// automatic restarts are enabled - <see cref="DirectorRestartConfig"/>). When the policy says
/// no, nothing is forced: a "new version waiting" notice is surfaced on
/// <see cref="PendingUpdateNotice"/> (the /status endpoint) and the log.
///
/// After a policy-approved restart the guardian health-checks the Director. A Director that
/// comes back on the old version is left alone (its own startup machinery declined or rolled
/// back the update and pinned it). A Director that stays dead is started once more (giving its
/// own startup recovery a chance to run); if it is STILL dead, the guardian renames the
/// ".old" bundle back into place, pins the bad version in the Director's updater state, and
/// starts the restored build - the last-resort rescue for a bundle too broken to reach its own
/// recovery code.
///
/// Every effect is an injectable delegate so the decision flow is unit-testable without a real
/// Director, network, or clock. One instance lives for the launcher's process lifetime.
/// </summary>
public sealed class DirectorUpdateGuardian
{
    /// <summary>One probe of the running Director: its reported version and busy-session count.</summary>
    public sealed record DirectorHealth(string Version, int? BusySessions);

    /// <summary>
    /// The "new version waiting" notice for the owner, set while a staged update is blocked by
    /// policy and cleared when the update applies (or no longer exists). Read by the /status
    /// endpoint. Static: there is one guardian and many readers.
    /// </summary>
    public static string? PendingUpdateNotice { get; private set; }

    private readonly Func<DirectorRestartConfig> _loadConfig;
    private readonly Func<CancellationToken, Task<DirectorHealth?>> _probeDirector;
    private readonly Func<CancellationToken, Task<string?>> _fetchLatestVersion;
    private readonly Func<UpdaterState> _loadUpdaterState;
    private readonly Func<CancellationToken, Task> _restartDirector;
    private readonly Action _startDirector;
    private readonly Func<bool> _restoreDirectorBackup;
    private readonly Action<string> _pinBadVersion;
    private readonly Func<DateTime> _localNow;
    private readonly TimeSpan _healthTimeout;

    public DirectorUpdateGuardian(
        InstallLayout? layout = null,
        DirectorSupervisor? supervisor = null,
        Func<DirectorRestartConfig>? loadConfig = null,
        Func<CancellationToken, Task<DirectorHealth?>>? probeDirector = null,
        Func<CancellationToken, Task<string?>>? fetchLatestVersion = null,
        Func<UpdaterState>? loadUpdaterState = null,
        Func<CancellationToken, Task>? restartDirector = null,
        Action? startDirector = null,
        Func<bool>? restoreDirectorBackup = null,
        Action<string>? pinBadVersion = null,
        Func<DateTime>? localNow = null,
        TimeSpan? healthTimeout = null)
    {
        var resolvedLayout = layout ?? InstallLayout.Default();
        var resolvedSupervisor = supervisor ?? new DirectorSupervisor(resolvedLayout);
        _loadConfig = loadConfig ?? (() => DirectorRestartConfig.Load(resolvedLayout));
        _probeDirector = probeDirector ?? (ct => ProbeInstalledDirectorAsync(resolvedSupervisor, ct));
        _fetchLatestVersion = fetchLatestVersion ?? FetchLatestDirectorVersionAsync;
        _loadUpdaterState = loadUpdaterState ?? UpdaterState.Load;
        _restartDirector = restartDirector ?? (ct => resolvedSupervisor.RestartAsync(ct));
        _startDirector = startDirector ?? resolvedSupervisor.Start;
        _restoreDirectorBackup = restoreDirectorBackup ?? (() => RestoreBundleBackup(resolvedSupervisor.DirectorExePath));
        _pinBadVersion = pinBadVersion ?? PinInDirectorUpdaterState;
        _localNow = localNow ?? (() => DateTime.Now);
        _healthTimeout = healthTimeout ?? TimeSpan.FromSeconds(90);
    }

    /// <summary>
    /// The periodic loop (managed mode): one check every half hour. Never throws; failures
    /// only log, and the next cycle tries again.
    /// </summary>
    public async Task RunLoopAsync(CancellationToken ct)
    {
        // Let the launcher and any booting Director settle before the first check.
        try { await Task.Delay(TimeSpan.FromMinutes(3), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await CheckOnceAsync(ct);
                if (result.Outcome != GuardianOutcome.UpToDate)
                {
                    FileLog.Write($"[DirectorUpdateGuardian] {result.Outcome}: {result.Message}");
                    foreach (var step in result.Steps) FileLog.Write($"[DirectorUpdateGuardian]   {step}");
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorUpdateGuardian] check FAILED: {ex.Message}");
            }
            try { await Task.Delay(TimeSpan.FromMinutes(30), ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One complete guardian check. Public for tests; the loop above is its only production caller.</summary>
    public async Task<GuardianResult> CheckOnceAsync(CancellationToken ct = default)
    {
        var steps = new List<string>();
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            return new GuardianResult(GuardianOutcome.Skipped, "Unsupported operating system.", steps);

        // 1. Is the Director even running? A down Director is started - and that start IS the
        // apply path: any staged update applies itself during startup, no policy needed
        // because a Director that is not running has no sessions to protect.
        var health = await _probeDirector(ct);
        if (health is null)
        {
            steps.Add("Director not running (or health endpoint unreachable); starting it");
            _startDirector();
            return new GuardianResult(GuardianOutcome.StartedDirector,
                "Director was not running; started it (a staged update applies itself at startup).", steps);
        }

        // 2. Is there anything newer?
        var latestText = await _fetchLatestVersion(ct);
        if (latestText is null || !Version.TryParse(TrimVersion(latestText), out var latest))
            return new GuardianResult(GuardianOutcome.UpToDate, "No release version to compare against.", steps);
        if (!Version.TryParse(TrimVersion(health.Version), out var running))
            return new GuardianResult(GuardianOutcome.Skipped, $"Could not parse the Director's version '{health.Version}'.", steps);
        if (Normalize(latest) <= Normalize(running))
        {
            PendingUpdateNotice = null;
            return new GuardianResult(GuardianOutcome.UpToDate, $"Director {running.ToString(3)} is current.", steps);
        }
        steps.Add($"Director runs {running.ToString(3)}, latest release is {latest.ToString(3)}");

        // 3. Respect the Director's own updater state: a pinned version already failed and
        // rolled back once - restarting for it again would loop forever. And an update that
        // is not staged yet has nothing to apply - the Director's own poll stages it.
        var updaterState = _loadUpdaterState();
        var latestNormalized = latest.ToString(3);
        if (string.Equals(updaterState.PinnedBadVersion, latestNormalized, StringComparison.Ordinal))
        {
            PendingUpdateNotice = $"Version {latestNormalized} is available but previously failed on this machine and is blocked from retrying.";
            return new GuardianResult(GuardianOutcome.PinnedBad, PendingUpdateNotice, steps);
        }
        if (!string.Equals(updaterState.StagedVersion, latestNormalized, StringComparison.Ordinal))
        {
            steps.Add($"update {latestNormalized} is not staged yet (staged: {updaterState.StagedVersion ?? "none"}); the Director stages it on its own schedule");
            return new GuardianResult(GuardianOutcome.WaitingForStage, $"Waiting for the Director to stage {latestNormalized}.", steps);
        }

        // 4. The owner's restart policy (version 1, decision 8): every session idle or
        // waiting, inside the nightly window, and the switch on. Blocked means notify, never
        // force. Version 2 (save sessions as handovers, update, restore) is deferred and
        // slots in exactly here.
        var config = _loadConfig();
        var blockReason = config.BlockReason(_localNow(), health.BusySessions);
        if (blockReason is not null)
        {
            PendingUpdateNotice = $"Director update {latestNormalized} is staged and waiting: {blockReason}.";
            steps.Add(PendingUpdateNotice);
            return new GuardianResult(GuardianOutcome.Blocked, PendingUpdateNotice, steps);
        }

        // 5. Apply: restart the Director; the staged build applies itself during startup.
        steps.Add($"policy allows the restart; restarting the Director to apply {latestNormalized}");
        FileLog.Write($"[DirectorUpdateGuardian] restarting the Director to apply {latestNormalized}");
        await _restartDirector(ct);

        var after = await WaitForDirectorAsync(ct);
        if (after is not null)
        {
            PendingUpdateNotice = null;
            if (Version.TryParse(TrimVersion(after.Version), out var now) && Normalize(now) >= Normalize(latest))
            {
                steps.Add($"Director is healthy on {after.Version}");
                return new GuardianResult(GuardianOutcome.Updated, $"Director updated to {after.Version}.", steps);
            }
            steps.Add($"Director is healthy but still on {after.Version}; its startup logic declined or rolled back the update");
            return new GuardianResult(GuardianOutcome.ApplyDidNotStick,
                $"Director restarted healthy on {after.Version}, not {latestNormalized}; see the Director's log.", steps);
        }

        // 6. The Director did not come back. Start it once more - a bootable build runs its
        // own half-swap recovery and health rollback at startup.
        steps.Add($"Director not healthy within {_healthTimeout.TotalSeconds:F0} seconds of the restart; starting it once more");
        FileLog.Write("[DirectorUpdateGuardian] Director dead after update restart; giving its startup recovery one chance");
        try { _startDirector(); } catch (Exception ex) { steps.Add($"start failed: {ex.Message}"); }
        after = await WaitForDirectorAsync(ct);
        if (after is not null)
        {
            steps.Add($"Director recovered on {after.Version}");
            return new GuardianResult(GuardianOutcome.ApplyDidNotStick,
                $"Director recovered on {after.Version} after a failed update restart.", steps);
        }

        // 7. Last resort: the new build is too broken to reach its own recovery code. Restore
        // the ".old" backup by rename, pin the bad version, and start the restored build.
        steps.Add("Director still dead; restoring the .old backup and pinning the bad version");
        FileLog.Write($"[DirectorUpdateGuardian] restoring the Director's .old backup; pinning {latestNormalized}");
        var restored = _restoreDirectorBackup();
        _pinBadVersion(latestNormalized);
        if (!restored)
        {
            return new GuardianResult(GuardianOutcome.Dead,
                $"Director is dead after update {latestNormalized} and no .old backup exists to restore. Manual repair needed.", steps);
        }
        try { _startDirector(); } catch (Exception ex) { steps.Add($"start after restore failed: {ex.Message}"); }
        after = await WaitForDirectorAsync(ct);
        steps.Add(after is not null
            ? $"restored Director is healthy on {after.Version}"
            : "restored Director is still not answering; manual repair needed");
        return new GuardianResult(
            after is not null ? GuardianOutcome.RescuedByRollback : GuardianOutcome.Dead,
            after is not null
                ? $"Update {latestNormalized} failed; restored the previous Director and pinned the bad version."
                : $"Update {latestNormalized} failed and the restore did not come up. Manual repair needed.", steps);
    }

    private async Task<DirectorHealth?> WaitForDirectorAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _healthTimeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var health = await _probeDirector(ct);
            if (health is not null) return health;
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    // ---- Real effect implementations (thin, replaced wholesale in tests) ----------------

    /// <summary>Probe the installed Director's health endpoint via its instance registration port.</summary>
    private static async Task<DirectorHealth?> ProbeInstalledDirectorAsync(DirectorSupervisor supervisor, CancellationToken ct)
    {
        var port = supervisor.InstalledDirectorPort();
        if (port <= 0) return null;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var json = await http.GetStringAsync($"http://127.0.0.1:{port}/healthz", ct);
            using var doc = JsonDocument.Parse(json);
            var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            int? busy = doc.RootElement.TryGetProperty("busySessions", out var b) && b.ValueKind == JsonValueKind.Number
                ? b.GetInt32()
                : null;
            return new DirectorHealth(version, busy);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The latest release's Director version from the release manifest.</summary>
    private static async Task<string?> FetchLatestDirectorVersionAsync(CancellationToken ct)
    {
        var source = new ReleaseSource();
        var release = await source.FetchLatestAsync(ct);
        var assetName = OperatingSystem.IsMacOS()
            ? ComponentRegistry.Director.MacAsset
            : ComponentRegistry.Director.WindowsAsset;
        if (assetName is null) return null;
        return release.Manifest.TryGetAsset(assetName)?.Version;
    }

    /// <summary>Rename the Director's ".old" backup back into place (the bad build is set aside first, then removed).</summary>
    private static bool RestoreBundleBackup(string target)
    {
        var old = target + ".old";
        var oldExists = File.Exists(old) || Directory.Exists(old);
        if (!oldExists)
        {
            FileLog.Write($"[DirectorUpdateGuardian] no backup at {old}; nothing to restore");
            return false;
        }
        var failed = target + ".failed";
        DeleteEntry(failed);
        if (File.Exists(target) || Directory.Exists(target))
            MoveEntry(target, failed);
        MoveEntry(old, target);
        DeleteEntry(failed);
        FileLog.Write($"[DirectorUpdateGuardian] restored {target} from {old}");
        return true;
    }

    /// <summary>Pin the failed version in the Director's own updater state and clear the staged update, so no side re-applies it.</summary>
    private static void PinInDirectorUpdaterState(string version)
    {
        try
        {
            var state = UpdaterState.Load();
            state.PinnedBadVersion = version;
            state.PendingHealthCheckVersion = null;
            state.StagedVersion = null;
            state.StagedExecutable = null;
            state.InstallTarget = null;
            state.Save();
            FileLog.Write($"[DirectorUpdateGuardian] pinned bad Director version {version}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorUpdateGuardian] pinning {version} FAILED: {ex.Message}");
        }
    }

    private static void MoveEntry(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    private static void DeleteEntry(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static string TrimVersion(string text)
    {
        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        var cut = t.IndexOfAny(['-', '+']);
        return cut >= 0 ? t[..cut] : t;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
