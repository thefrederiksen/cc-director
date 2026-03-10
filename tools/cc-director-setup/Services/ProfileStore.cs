using System.Text.Json;
using CcDirectorSetup.Models;

namespace CcDirectorSetup.Services;

public record SavedSettings(InstallProfile Profile, List<string> Groups);

public static class ProfileStore
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cc-director", "config");

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "install-profile.json");
    private static readonly string LegacyConfigFile = Path.Combine(ConfigDir, "setup-profile.json");

    public static SavedSettings? Load()
    {
        SetupLog.Write("[ProfileStore] Load: checking for saved settings");

        // Try new format first
        if (File.Exists(ConfigFile))
        {
            var json = File.ReadAllText(ConfigFile);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var profile = InstallProfile.Standard;
            if (root.TryGetProperty("profile", out var profileEl))
            {
                var profileStr = profileEl.GetString();
                if (Enum.TryParse<InstallProfile>(profileStr, out var parsed))
                    profile = parsed;
            }

            var groups = new List<string>();
            if (root.TryGetProperty("groups", out var groupsEl))
            {
                groups = groupsEl.EnumerateArray()
                    .Select(e => e.GetString() ?? "")
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
            }

            SetupLog.Write($"[ProfileStore] Load: restored profile={profile}, groups={groups.Count}");
            return new SavedSettings(profile, groups);
        }

        // Migrate from legacy format
        if (File.Exists(LegacyConfigFile))
        {
            SetupLog.Write("[ProfileStore] Load: migrating from legacy profile format");
            var json = File.ReadAllText(LegacyConfigFile);
            using var doc = JsonDocument.Parse(json);
            var profileStr = doc.RootElement.GetProperty("profile").GetString();

            var profile = profileStr == "Developer" ? InstallProfile.Developer : InstallProfile.Standard;
            var groups = profileStr == "Developer"
                ? ToolGroupRegistry.GetPresetGroupNames("Developer")
                : ToolGroupRegistry.GetDefaultGroupNames();

            var settings = new SavedSettings(profile, groups);
            Save(settings);
            File.Delete(LegacyConfigFile);
            SetupLog.Write($"[ProfileStore] Load: migrated '{profileStr}' to profile={profile}, groups={groups.Count}");
            return settings;
        }

        SetupLog.Write("[ProfileStore] Load: no saved settings found");
        return null;
    }

    public static void Save(SavedSettings settings)
    {
        SetupLog.Write($"[ProfileStore] Save: profile={settings.Profile}, groups={settings.Groups.Count}");

        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(new
        {
            profile = settings.Profile.ToString(),
            groups = settings.Groups
        });
        File.WriteAllText(ConfigFile, json);
        SetupLog.Write("[ProfileStore] Save: success");
    }
}
