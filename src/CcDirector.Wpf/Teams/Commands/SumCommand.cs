using System.IO;
using System.Windows.Threading;
using CcDirector.Core.Sessions;
using CcDirector.Wpf.Controls;
using CcDirector.Wpf.Teams.Utilities;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;

namespace CcDirector.Wpf.Teams.Commands;

/// <summary>
/// Handles /sum command - summarizes the active session's terminal output.
/// </summary>
public static class SumCommand
{
    private const int LastLinesCount = 100;

    public static async Task ExecuteAsync(
        ITurnContext turnContext,
        Session? activeSession,
        Func<Session, TerminalControl?> getTerminalControl,
        Dispatcher dispatcher,
        CancellationToken ct)
    {
        if (activeSession == null)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text("No session selected. Use /ls to list sessions and /s <id> to select one."),
                ct);
            return;
        }

        var repoName = Path.GetFileName(activeSession.RepoPath);
        var displayName = activeSession.CustomName ?? repoName;

        // Get terminal text on UI thread
        string? terminalText = null;
        string? errorMessage = null;

        await dispatcher.InvokeAsync(() =>
        {
            try
            {
                var terminal = getTerminalControl(activeSession);
                if (terminal == null)
                {
                    errorMessage = "Terminal not available";
                    return;
                }

                var fullText = terminal.GetAllTerminalText();
                terminalText = AnsiCleaner.GetLastLines(fullText, LastLinesCount);
            }
            catch (Exception ex)
            {
                errorMessage = $"Failed to get terminal text: {ex.Message}";
            }
        });

        if (errorMessage != null)
        {
            await turnContext.SendActivityAsync(MessageFactory.Text(errorMessage), ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(terminalText))
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text($"No terminal output for {displayName}"),
                ct);
            return;
        }

        // Truncate if too long
        if (terminalText.Length > 4000)
        {
            terminalText = "...(truncated)\n" + terminalText.Substring(terminalText.Length - 4000);
        }

        // Build response
        var statusText = GetStatusText(activeSession);
        var response = $"**{displayName}** - {statusText}\n\n```\n{terminalText}\n```";

        await turnContext.SendActivityAsync(MessageFactory.Text(response), ct);
    }

    private static string GetStatusText(Session session)
    {
        return session.ActivityState switch
        {
            ActivityState.Idle => "Idle",
            ActivityState.Starting => "Starting...",
            ActivityState.Working => "Working...",
            ActivityState.WaitingForInput => "Ready for input",
            ActivityState.WaitingForPerm => "Waiting for permission",
            ActivityState.Exited => "Exited",
            _ => "Unknown"
        };
    }
}
