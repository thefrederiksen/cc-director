using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Backends;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Default session mode setting. Controls whether new sessions default to Terminal or Studio mode.
/// Persisted in config.json as "default_session_mode": "Terminal" | "Studio".
/// </summary>
public static class SessionModeConfig
{
    private static SessionBackendType _defaultMode = SessionBackendType.ConPty;
    private static bool _loaded;

    /// <summary>The default backend type for new sessions.</summary>
    public static SessionBackendType DefaultMode
    {
        get
        {
            if (!_loaded) Load();
            return _defaultMode;
        }
    }

    /// <summary>Set the default session mode and persist to config.json.</summary>
    public static void SetDefaultMode(SessionBackendType mode)
    {
        FileLog.Write($"[SessionModeConfig] SetDefaultMode: {mode}");
        _defaultMode = mode;
        _loaded = true;
        Save();
    }

    private static void Load()
    {
        _loaded = true;
        _defaultMode = SessionBackendType.ConPty;

        var configPath = CcStorage.ConfigJson();
        if (!File.Exists(configPath)) return;

        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("default_session_mode", out var prop)
                && prop.ValueKind == JsonValueKind.String)
            {
                var value = prop.GetString();
                if (string.Equals(value, "Studio", StringComparison.OrdinalIgnoreCase))
                    _defaultMode = SessionBackendType.Studio;
            }
            FileLog.Write($"[SessionModeConfig] Load: default_session_mode={_defaultMode}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionModeConfig] Load FAILED: {ex.Message}");
        }
    }

    private static void Save()
    {
        var configPath = CcStorage.ConfigJson();
        try
        {
            JsonNode? root;
            if (File.Exists(configPath))
            {
                var json = File.ReadAllText(configPath);
                root = JsonNode.Parse(json);
            }
            else
            {
                var configDir = Path.GetDirectoryName(configPath);
                if (configDir is null)
                    throw new InvalidOperationException($"Cannot determine directory for config path: {configPath}");
                Directory.CreateDirectory(configDir);
                root = new JsonObject();
            }

            if (root is JsonObject obj)
            {
                obj["default_session_mode"] = _defaultMode == SessionBackendType.Studio ? "Studio" : "Terminal";
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(configPath, root.ToJsonString(options));
                FileLog.Write($"[SessionModeConfig] Save: wrote default_session_mode={_defaultMode} to {configPath}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionModeConfig] Save FAILED: {ex.Message}");
        }
    }
}
