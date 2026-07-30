using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>One /healthz answer from a launcher: liveness plus IDENTITY (version, process id).</summary>
public sealed record LauncherHealth(bool Ok, string? Version, int Pid);

/// <summary>Why a readiness wait stopped. "Not ready yet" and "never going to be ready" are
/// different facts and the caller has to be able to tell them apart.</summary>
public enum LauncherWaitStop
{
    /// <summary>The launcher answered and the answer certifies this install. The only good end.</summary>
    Healthy,

    /// <summary>The process the caller started is gone, so no answer is ever coming from it.</summary>
    StarterExited,

    /// <summary>The process is still running but the ceiling elapsed without a certifying answer.</summary>
    CeilingReached,

    /// <summary>The caller cancelled the wait.</summary>
    Cancelled,
}

/// <summary>The end of a readiness wait: the last answer seen (null when nothing ever answered) and
/// why the waiting stopped.</summary>
public sealed record LauncherWaitResult(LauncherHealth? Health, LauncherWaitStop Stop);

/// <summary>
/// Polls a launcher's /healthz until the answering launcher is provably the one just installed
/// (issue #2042). The old check accepted ANY 200 from the fixed port - so on a machine where a
/// launcher was already running, a completely failed install of the new binary still reported
/// "healthy": the poll was answered by the pre-existing process. Liveness is not identity.
///
/// The version was not enough either, and a Mac proved it. The installer started process 35158; the
/// answer came from orphan 34084, up for seventy-three minutes from a path the installer had just
/// overwritten. <see cref="VersionUtil.TryParse"/> strips build metadata, so the orphan's
/// "1.8.4+71f90bad..." and the freshly placed "1.8.4" compared EQUAL. A version check can only catch
/// a version CHANGE, which is the case that matters least; on a same-version reinstall it was blind.
///
/// So the caller states which PROCESS it started and the answer has to come from that process. The
/// version stays as a second signal, never the only one.
/// </summary>
public static class LauncherHealthProbe
{
    /// <summary>
    /// Wait until /healthz at <paramref name="healthUrl"/> answers with ok=true AND, when known, the
    /// matching version AND the matching process. Returns the final health answer (identity-verified
    /// on success), or null when nothing answered at all.
    ///
    /// A responder that is not the expected process keeps being polled until the deadline - during a
    /// swap the OLD launcher can legitimately answer for a moment before the new one takes the port -
    /// and is returned as-is at the deadline so the caller can fail loud, naming what answered.
    /// </summary>
    /// <param name="expectedPid">
    /// The process the caller started, or 0 when it has none to expect (a plain liveness check rather
    /// than certifying an install). Zero keeps the old version-only behaviour.
    /// </param>
    public static async Task<LauncherHealth?> WaitForHealthyAsync(
        HttpClient http, string healthUrl, string? expectedVersion, TimeSpan timeout, CancellationToken ct,
        int expectedPid = 0)
    {
        var result = await WaitForReadyAsync(
            http, healthUrl, expectedVersion, expectedPid, static () => true, timeout, ct);
        return result.Health;
    }

    /// <summary>
    /// Wait for the launcher to become ready on the CONDITION rather than on a clock: poll /healthz
    /// until it certifies this install, and stop early only when something is genuinely wrong - the
    /// process the caller started is no longer running, so nothing is ever going to answer for it.
    ///
    /// This exists because a fixed clock reported a healthy install as a failure (issue #1152). On a
    /// clean Windows 11 machine the installer allowed about twenty seconds, called the launcher dead,
    /// and painted a red ERROR and a Failed row - while /healthz on that exact port answered 200 with
    /// ok:true from the process the installer had started. cc-launcher.exe is a ~134 MB single-file
    /// binary that unpacks itself on first run, so it is slow exactly once: on the machine where a
    /// first-time user is watching. A bigger fixed number is the same defect with a longer fuse, and
    /// the next slow machine walks into it.
    ///
    /// So the ceiling is a backstop for a genuine hang, not the thing that decides the verdict. The
    /// verdict is the port answering.
    /// </summary>
    /// <param name="expectedPid">The process the caller started, or 0 when it has none to expect.</param>
    /// <param name="starterIsRunning">
    /// True while the process the caller started is still alive. Returning false ends the wait at once:
    /// a launcher that has exited will never answer, and waiting out the ceiling only delays a failure
    /// that is already certain.
    /// </param>
    /// <param name="ceiling">The backstop for a process that is alive but wedged.</param>
    /// <param name="onStillWaiting">
    /// Called after each poll that did not certify, with the elapsed time, so a caller can tell the
    /// user this is a slow first start rather than a frozen screen.
    /// </param>
    public static async Task<LauncherWaitResult> WaitForReadyAsync(
        HttpClient http,
        string healthUrl,
        string? expectedVersion,
        int expectedPid,
        Func<bool> starterIsRunning,
        TimeSpan ceiling,
        CancellationToken ct,
        TimeSpan? pollInterval = null,
        Action<TimeSpan>? onStillWaiting = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(starterIsRunning);

        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        var started = DateTime.UtcNow;
        var deadline = started + ceiling;
        LauncherHealth? last = null;

        while (true)
        {
            if (ct.IsCancellationRequested) return new LauncherWaitResult(last, LauncherWaitStop.Cancelled);

            try
            {
                using var resp = await http.GetAsync(healthUrl, ct);
                if (resp.IsSuccessStatusCode)
                {
                    last = Parse(await resp.Content.ReadAsStringAsync(ct));
                    if (Certifies(last, expectedVersion, expectedPid))
                        return new LauncherWaitResult(last, LauncherWaitStop.Healthy);
                }
            }
            catch
            {
                // Not up yet. On a first start the port is not even listening, so this is the normal
                // shape of a cold start rather than a symptom of anything.
            }

            // Ask AFTER polling, so a launcher that answered and then exited still counts as healthy.
            if (!starterIsRunning()) return new LauncherWaitResult(last, LauncherWaitStop.StarterExited);

            var elapsed = DateTime.UtcNow - started;
            if (DateTime.UtcNow >= deadline) return new LauncherWaitResult(last, LauncherWaitStop.CeilingReached);
            onStillWaiting?.Invoke(elapsed);

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { return new LauncherWaitResult(last, LauncherWaitStop.Cancelled); }
        }
    }

    /// <summary>True when the answer certifies the install: ok, version-matched when one was expected,
    /// and from the process the caller started when it knows which one that is.</summary>
    public static bool Certifies(LauncherHealth? health, string? expectedVersion, int expectedPid = 0) =>
        health is { Ok: true }
        && VersionMatches(expectedVersion, health.Version)
        && PidMatches(expectedPid, health.Pid);

    /// <summary>
    /// Does this answer come from the process the caller started? With no expectation (0) anything
    /// passes, because there is nothing to compare. With an expectation, an answer carrying NO process
    /// id fails: a launcher too old to report one cannot be the current binary we just placed, so
    /// silence is a refusal rather than the benefit of the doubt.
    /// </summary>
    public static bool PidMatches(int expected, int reported) =>
        expected <= 0 || reported == expected;

    /// <summary>No expectation always matches (legacy launchers without a version field cannot be
    /// checked); otherwise both sides must parse and compare equal, build metadata ignored.</summary>
    public static bool VersionMatches(string? expected, string? reported)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        var e = VersionUtil.TryParse(expected);
        var r = VersionUtil.TryParse(reported);
        return e is not null && r is not null && e == r;
    }

    /// <summary>Parse a /healthz body; unparseable input reads as a not-ok answer, never a throw.</summary>
    public static LauncherHealth Parse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            var version = root.TryGetProperty("version", out var vEl) && vEl.ValueKind == JsonValueKind.String
                ? vEl.GetString() : null;
            var pid = root.TryGetProperty("pid", out var pEl) && pEl.TryGetInt32(out var p) ? p : 0;
            return new LauncherHealth(ok, version, pid);
        }
        catch (JsonException)
        {
            return new LauncherHealth(false, null, 0);
        }
    }
}
