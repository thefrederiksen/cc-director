using System.IO;
using System.Text.Json;
using CcDirectorSetup.Models;

namespace CcDirectorSetup.Services;

public static class ProfileStore
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cc-director", "config");

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "setup-profile.json");

    public static InstallProfile? Load()
    {
        SetupLog.Write("[ProfileStore] Load: checking for saved profile");

        if (!File.Exists(ConfigFile))
        {
            SetupLog.Write("[ProfileStore] Load: no saved profile found");
            return null;
        }

        var json = File.ReadAllText(ConfigFile);
        using var doc = JsonDocument.Parse(json);
        var profileStr = doc.RootElement.GetProperty("profile").GetString();

        if (Enum.TryParse<InstallProfile>(profileStr, out var profile))
        {
            SetupLog.Write($"[ProfileStore] Load: restored profile={profile}");
            return profile;
        }

        SetupLog.Write($"[ProfileStore] Load: unknown profile value '{profileStr}'");
        return null;
    }

    public static void Save(InstallProfile profile)
    {
        SetupLog.Write($"[ProfileStore] Save: profile={profile}");

        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(new { profile = profile.ToString() });
        File.WriteAllText(ConfigFile, json);
        SetupLog.Write("[ProfileStore] Save: success");
    }
}
