using System.IO;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Wpf.Teams.Models;

namespace CcDirector.Wpf.Teams;

/// <summary>
/// Manages the whitelist of allowed Teams user IDs.
/// Logs unknown user attempts for discovery.
/// </summary>
public sealed class TeamsWhitelist
{
    private readonly string _whitelistPath;
    private readonly string _unknownUsersLogPath;
    private readonly Action<string> _log;
    private HashSet<string> _allowedUserIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public TeamsWhitelist(TeamsBotConfig config, Action<string> log)
    {
        _whitelistPath = config.ExpandedWhitelistPath;
        _unknownUsersLogPath = Path.Combine(
            Path.GetDirectoryName(_whitelistPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "teams-unknown-users.log");
        _log = log;
    }

    /// <summary>
    /// Load the whitelist from disk. Creates empty whitelist file if it doesn't exist.
    /// </summary>
    public void Load()
    {
        _log($"[TeamsWhitelist] Loading from {_whitelistPath}");

        try
        {
            var dir = Path.GetDirectoryName(_whitelistPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                _log($"[TeamsWhitelist] Created directory: {dir}");
            }

            if (!File.Exists(_whitelistPath))
            {
                // Create empty whitelist file with instructions
                var emptyWhitelist = new WhitelistFile
                {
                    AllowedUserIds = new List<string>(),
                    Comment = "Add Teams user IDs (from 29:xxx format) to allow access. Check teams-unknown-users.log for IDs."
                };
                var json = JsonSerializer.Serialize(emptyWhitelist, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_whitelistPath, json);
                _log($"[TeamsWhitelist] Created empty whitelist file");
                return;
            }

            var content = File.ReadAllText(_whitelistPath);
            var whitelist = JsonSerializer.Deserialize<WhitelistFile>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            lock (_lock)
            {
                _allowedUserIds = new HashSet<string>(
                    whitelist?.AllowedUserIds ?? new List<string>(),
                    StringComparer.OrdinalIgnoreCase);
            }

            _log($"[TeamsWhitelist] Loaded {_allowedUserIds.Count} allowed user ID(s)");
        }
        catch (Exception ex)
        {
            _log($"[TeamsWhitelist] Load FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if a user ID is in the whitelist.
    /// </summary>
    public bool IsAllowed(string userId)
    {
        if (string.IsNullOrEmpty(userId))
            return false;

        lock (_lock)
        {
            return _allowedUserIds.Contains(userId);
        }
    }

    /// <summary>
    /// Log an unknown user attempt for later discovery.
    /// </summary>
    public void LogUnknownUser(string userId, string? userName, string? messageText)
    {
        _log($"[TeamsWhitelist] Unknown user attempt: {userId} ({userName})");

        try
        {
            var truncatedMessage = string.IsNullOrEmpty(messageText)
                ? "(empty)"
                : messageText.Length <= 50
                    ? messageText
                    : messageText[..50] + "...";
            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UserId={userId}, Name={userName ?? "unknown"}, Message={truncatedMessage}";
            File.AppendAllText(_unknownUsersLogPath, entry + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _log($"[TeamsWhitelist] Failed to log unknown user: {ex.Message}");
        }
    }

    /// <summary>
    /// Reload the whitelist from disk.
    /// </summary>
    public void Reload()
    {
        Load();
    }

    private sealed class WhitelistFile
    {
        public List<string> AllowedUserIds { get; set; } = new();
        public string? Comment { get; set; }
    }
}
