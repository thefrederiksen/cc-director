using Microsoft.Bot.Schema;

namespace CcDirector.Wpf.Teams.Models;

/// <summary>
/// State for a connected Teams user, including conversation reference for proactive messaging.
/// </summary>
public sealed class TeamsUserState
{
    /// <summary>Teams user ID (e.g., 29:xxx).</summary>
    public string UserId { get; set; } = "";

    /// <summary>User display name.</summary>
    public string? UserName { get; set; }

    /// <summary>Conversation reference for sending proactive messages.</summary>
    public ConversationReference? ConversationReference { get; set; }

    /// <summary>Time of last activity.</summary>
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
}
