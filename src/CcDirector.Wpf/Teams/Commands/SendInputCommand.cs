using System.IO;
using CcDirector.Core.Sessions;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;

namespace CcDirector.Wpf.Teams.Commands;

/// <summary>
/// Handles plain text input - sends it to the active session.
/// </summary>
public static class SendInputCommand
{
    public static async Task<bool> ExecuteAsync(
        ITurnContext turnContext,
        string text,
        Session? activeSession,
        Action startQuiescenceMonitor,
        CancellationToken ct)
    {
        if (activeSession == null)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("No session selected. Use /ls to list sessions and /s <id> to select one."),
                ct);
            return false;
        }

        if (activeSession.Status == SessionStatus.Exited || activeSession.Status == SessionStatus.Failed)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text($"Session has ended (status: {activeSession.Status}). Create a new session with /new."),
                ct);
            return false;
        }

        var repoName = Path.GetFileName(activeSession.RepoPath);
        var displayName = activeSession.CustomName ?? repoName;

        try
        {
            await activeSession.SendTextAsync(text);

            // Start monitoring for task completion
            startQuiescenceMonitor();

            // Show truncated confirmation
            var truncatedText = text.Length > 50 ? text.Substring(0, 47) + "..." : text;
            await turnContext.SendActivityAsync(
                MessageFactory.Text($"Sent to {displayName}: \"{truncatedText}\""),
                ct);

            return true;
        }
        catch (Exception ex)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text($"Failed to send: {ex.Message}"),
                ct);
            return false;
        }
    }
}
