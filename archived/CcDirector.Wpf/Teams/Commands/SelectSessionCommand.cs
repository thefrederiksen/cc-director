using System.IO;
using CcDirector.Core.Sessions;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;

namespace CcDirector.Wpf.Teams.Commands;

/// <summary>
/// Handles /s [id] command - selects a session as active.
/// </summary>
public static class SelectSessionCommand
{
    public static async Task<Session?> ExecuteAsync(
        ITurnContext turnContext,
        string idPrefix,
        SessionManager sessionManager,
        Action<Session> setActiveSession,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idPrefix))
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Usage: /s <id-prefix>\nExample: /s a1b2c3d4"),
                ct);
            return null;
        }

        // Find session by ID prefix
        var sessions = sessionManager.ListSessions().ToList();
        var matches = sessions
            .Where(s => s.Id.ToString().StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text($"No session found matching '{idPrefix}'"),
                ct);
            return null;
        }

        if (matches.Count > 1)
        {
            var ids = string.Join(", ", matches.Select(s => s.Id.ToString().Substring(0, 8)));
            await turnContext.SendActivityAsync(
                MessageFactory.Text($"Multiple sessions match '{idPrefix}': {ids}\nPlease be more specific."),
                ct);
            return null;
        }

        var session = matches[0];
        setActiveSession(session);

        var repoName = Path.GetFileName(session.RepoPath);
        var displayName = session.CustomName ?? repoName;

        await turnContext.SendActivityAsync(
            MessageFactory.Text($"Selected: {displayName} ({session.Id.ToString().Substring(0, 8)})"),
            ct);

        return session;
    }
}
