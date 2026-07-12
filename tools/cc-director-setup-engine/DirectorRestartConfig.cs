using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>
/// The owner's policy for when the launcher may restart the Director to apply a staged update
/// (the CC Launcher mission, owner ruling of 2026-07-11, decision 8 in the mission brief).
/// Version 1, deliberately conservative: an automatic restart is allowed only when EVERY
/// session on the Director is idle or waiting AND the local time is inside the nightly
/// maintenance window - and the whole mechanism has its own off switch. When the policy blocks
/// a restart, nothing is forced; the launcher surfaces a "new version waiting" notice instead.
///
/// Read from the shared config.json "directorRestart" section:
///   { "directorRestart": { "enabled": true, "windowStartHour": 2, "windowEndHour": 5 } }
/// Defaults: enabled, window 02:00 to 05:00 local time. A missing section or file, or a parse
/// error, falls back to the defaults. Hours are 0 to 23; a window may span midnight (start 22,
/// end 6); start equal to end means the window is the entire day.
///
/// Version 2 (explicitly deferred, decided later with version 1 evidence): possibly save the
/// running sessions as handover documents, update, and restore them. The policy lives behind
/// this one seam so that change will not touch the update loop.
/// </summary>
public sealed record DirectorRestartConfig(bool Enabled, int WindowStartHour, int WindowEndHour)
{
    public static readonly DirectorRestartConfig Default = new(Enabled: true, WindowStartHour: 2, WindowEndHour: 5);

    public static DirectorRestartConfig Load(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        try
        {
            if (!File.Exists(layout.ConfigPath)) return Default;
            using var doc = JsonDocument.Parse(File.ReadAllText(layout.ConfigPath));
            if (!doc.RootElement.TryGetProperty("directorRestart", out var section) || section.ValueKind != JsonValueKind.Object)
                return Default;

            var enabled = section.TryGetProperty("enabled", out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? e.GetBoolean()
                : Default.Enabled;
            var start = section.TryGetProperty("windowStartHour", out var s) && s.ValueKind == JsonValueKind.Number
                ? s.GetInt32()
                : Default.WindowStartHour;
            var end = section.TryGetProperty("windowEndHour", out var w) && w.ValueKind == JsonValueKind.Number
                ? w.GetInt32()
                : Default.WindowEndHour;
            return new DirectorRestartConfig(enabled, Clamp(start), Clamp(end));
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[DirectorRestartConfig] load failed ({layout.ConfigPath}): {ex.Message}; using defaults");
            return Default;
        }
    }

    private static int Clamp(int hour) => hour < 0 ? 0 : hour > 23 ? 23 : hour;

    /// <summary>
    /// The version 1 restart decision, pure and unit-tested: null when a restart is allowed
    /// right now, otherwise the human-readable reason it is blocked. <paramref name="busySessions"/>
    /// is the Director's count of actively working sessions; null means the Director did not
    /// report one (an older build), which blocks - never restart on missing evidence.
    /// </summary>
    public string? BlockReason(DateTime localNow, int? busySessions)
    {
        if (!Enabled)
            return "automatic Director restarts are disabled (directorRestart.enabled is false)";
        if (busySessions is null)
            return "the Director did not report session activity; not restarting on missing evidence";
        if (busySessions > 0)
            return $"{busySessions} session(s) are actively working";
        if (!InWindow(localNow.Hour))
            return $"outside the maintenance window ({WindowStartHour:D2}:00 to {WindowEndHour:D2}:00)";
        return null;
    }

    /// <summary>Whether an hour falls inside the window. Start equal to end means always; a window may span midnight.</summary>
    public bool InWindow(int hour)
    {
        if (WindowStartHour == WindowEndHour) return true;
        return WindowStartHour < WindowEndHour
            ? hour >= WindowStartHour && hour < WindowEndHour
            : hour >= WindowStartHour || hour < WindowEndHour;
    }
}
