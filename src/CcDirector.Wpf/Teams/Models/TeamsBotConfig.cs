namespace CcDirector.Wpf.Teams.Models;

/// <summary>
/// Configuration for the Teams bot integration.
/// Loaded from appsettings.json "TeamsBot" section.
/// </summary>
public sealed class TeamsBotConfig
{
    /// <summary>Whether the Teams bot is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Azure Bot App ID (from Azure Bot registration).</summary>
    public string MicrosoftAppId { get; set; } = "";

    /// <summary>Azure Bot App Password/Secret.</summary>
    public string MicrosoftAppPassword { get; set; } = "";

    /// <summary>Local port for the bot endpoint (default 3978).</summary>
    public int Port { get; set; } = 3978;

    /// <summary>Dev Tunnel name for persistent URL.</summary>
    public string TunnelName { get; set; } = "cc-director-bot";

    /// <summary>
    /// Path to whitelist JSON file.
    /// Supports environment variables like %LOCALAPPDATA%.
    /// </summary>
    public string WhitelistPath { get; set; } = "%LOCALAPPDATA%/CcDirector/teams-whitelist.json";

    /// <summary>
    /// Milliseconds to wait after output stops before sending "task complete" notification.
    /// Prevents notification spam during rapid output bursts.
    /// </summary>
    public int NotificationQuiescenceMs { get; set; } = 3000;

    /// <summary>Expand environment variables in WhitelistPath.</summary>
    public string ExpandedWhitelistPath => Environment.ExpandEnvironmentVariables(WhitelistPath);
}
