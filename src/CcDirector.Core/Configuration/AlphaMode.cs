using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// Global alpha mode toggle. When enabled, alpha/experimental features are visible.
/// Persisted in config.json as "alpha_mode": true/false.
/// </summary>
public static class AlphaMode
{
    private static bool _isEnabled;
    private static bool _loaded;

    /// <summary>Raised when alpha mode is toggled.</summary>
    public static event Action? Changed;

    /// <summary>Whether alpha mode is currently enabled.</summary>
    public static bool IsEnabled
    {
        get
        {
            if (!_loaded) Load();
            return _isEnabled;
        }
    }

    /// <summary>Toggle alpha mode on or off and persist to config.json.</summary>
    public static void SetEnabled(bool enabled)
    {
        FileLog.Write($"[AlphaMode] SetEnabled: {enabled}");
        _isEnabled = enabled;
        _loaded = true;
        Save();
        Changed?.Invoke();
    }

    private static void Load()
    {
        _loaded = true;
        _isEnabled = false;

        var configPath = CcStorage.ConfigJson();
        if (!File.Exists(configPath)) return;

        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("alpha_mode", out var prop) && prop.ValueKind == JsonValueKind.True)
            {
                _isEnabled = true;
            }
            FileLog.Write($"[AlphaMode] Load: alpha_mode={_isEnabled}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AlphaMode] Load FAILED: {ex.Message}");
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
                obj["alpha_mode"] = _isEnabled;
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(configPath, root.ToJsonString(options));
                FileLog.Write($"[AlphaMode] Save: wrote alpha_mode={_isEnabled} to {configPath}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AlphaMode] Save FAILED: {ex.Message}");
        }
    }
}
