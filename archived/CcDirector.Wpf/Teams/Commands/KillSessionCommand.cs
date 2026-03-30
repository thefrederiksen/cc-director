using System.IO;
using CcDirector.Core.Sessions;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;

namespace CcDirector.Wpf.Teams.Commands;

/// <summary>
/// Handles /kill [id] command - terminates a session.
/// </summary>
public static class KillSessionCommand
{
    public static async Task<bool> ExecuteAsync(
        ITurnContext turnContext,
        string idPrefix,
        SessionManager sessionManager,
        Session? activeSession,
        Action clearActiveSession,
        CancellationToken ct)
    {
        Session? sessionToKill;

        if (string.IsNullOrWhiteSpace(idPrefix))
        {
            // Kill active session if no ID provided
            if (activeSession == null)
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text("No session selected. Use /kill <id> or select a session first."),
                    ct);
                return false;
            }
            sessionToKill = activeSession;
        }
        else
        {
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
                return false;
            }

            if (matches.Count > 1)
            {
                var ids = string.Join(", ", matches.Select(s => s.Id.ToString().Substring(0, 8)));
                await turnContext.SendActivityAsync(
                    MessageFactory.Text($"Multiple sessions match '{idPrefix}': {ids}\nPlease be more specific."),
                    ct);
                return false;
            }

            sessionToKill = matches[0];
        }

        var repoName = Path.GetFileName(sessionToKill.RepoPath);
        var displayName = sessionToKill.CustomName ?? repoName;
        var shortId = sessionToKill.Id.ToString().Substring(0, 8);

        await turnContext.SendActivityAsync(
            MessageFactory.Text($"Killing session: {displayName} ({shortId})..."),
            ct);

        try
        {
            await sessionManager.KillSessionAsync(sessionToKill.Id);

            // Clear active session if it was the killed one
            if (activeSession?.Id == sessionToKill.Id)
            {
                clearActiveSession();
            }

            await turnContext.SendActivityAsync(
                MessageFactory.Text($"Session killed: {displayName}"),
                ct);
            return true;
        }
        catch (Exception ex)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text($"Failed to kill session: {ex.Message}"),
                ct);
            return false;
        }
    }
}
