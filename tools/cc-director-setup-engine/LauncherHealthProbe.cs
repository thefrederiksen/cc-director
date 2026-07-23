using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>One /healthz answer from a launcher: liveness plus IDENTITY (version, process id).</summary>
public sealed record LauncherHealth(bool Ok, string? Version, int Pid);

/// <summary>
/// Polls a launcher's /healthz until the answering launcher is provably the one just installed
/// (issue #2042). The old check accepted ANY 200 from the fixed port - so on a machine where a
/// launcher was already running, a completely failed install of the new binary still reported
/// "healthy": the poll was answered by the pre-existing process. Liveness is not identity. The
/// probe reads the version stamped in the health payload and only certifies the install when it
/// matches the version that was just placed; a mismatched responder at the deadline fails loud,
/// naming both versions, instead of certifying a stranger.
/// </summary>
public static class LauncherHealthProbe
{
    /// <summary>
    /// Wait until /healthz at <paramref name="healthUrl"/> answers with ok=true AND, when
    /// <paramref name="expectedVersion"/> is known, the matching version. Returns the final
    /// health answer (identity-verified on success), or null when nothing answered at all.
    /// A responder whose version never matches keeps being polled until the deadline - during a
    /// kickstart swap the OLD launcher can legitimately answer for a moment before the new one
    /// takes the port - and is returned as-is at the deadline so the caller can fail loud.
    /// </summary>
    public static async Task<LauncherHealth?> WaitForHealthyAsync(
        HttpClient http, string healthUrl, string? expectedVersion, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        LauncherHealth? last = null;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var resp = await http.GetAsync(healthUrl, ct);
                if (resp.IsSuccessStatusCode)
                {
                    last = Parse(await resp.Content.ReadAsStringAsync(ct));
                    if (last is { Ok: true } && VersionMatches(expectedVersion, last.Version))
                        return last;
                }
            }
            catch
            {
                // not up yet
            }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return last; }
        }
        return last;
    }

    /// <summary>True when the answer certifies the install: ok, and version-matched when one was expected.</summary>
    public static bool Certifies(LauncherHealth? health, string? expectedVersion) =>
        health is { Ok: true } && VersionMatches(expectedVersion, health.Version);

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
