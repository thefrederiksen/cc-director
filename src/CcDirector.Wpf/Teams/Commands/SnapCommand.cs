using System.IO;
using System.Windows.Threading;
using CcDirector.Core.Sessions;
using CcDirector.Wpf.Controls;
using CcDirector.Wpf.Teams.Utilities;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;

namespace CcDirector.Wpf.Teams.Commands;

/// <summary>
/// Handles /snap command - captures screenshot and recent terminal text.
/// </summary>
public static class SnapCommand
{
    private const int LastLinesCount = 50;

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

        // Get terminal control and capture on UI thread
        byte[]? screenshotBytes = null;
        string? terminalText = null;
        string? errorMessage = null;

        await dispatcher.InvokeAsync(() =>
        {
            try
            {
                var terminal = getTerminalControl(activeSession);
                if (terminal == null)
                {
                    errorMessage = "Terminal not available (session may not be displayed)";
                    return;
                }

                // Capture screenshot
                screenshotBytes = TerminalScreenshot.Capture(terminal);

                // Get terminal text
                var fullText = terminal.GetAllTerminalText();
                terminalText = AnsiCleaner.GetLastLines(fullText, LastLinesCount);
            }
            catch (Exception ex)
            {
                errorMessage = $"Failed to capture: {ex.Message}";
            }
        });

        if (errorMessage != null)
        {
            await turnContext.SendActivityAsync(MessageFactory.Text(errorMessage), ct);
            return;
        }

        // Send screenshot as attachment
        if (screenshotBytes != null && screenshotBytes.Length > 0)
        {
            var base64 = Convert.ToBase64String(screenshotBytes);
            var attachment = new Attachment
            {
                ContentType = "image/png",
                ContentUrl = $"data:image/png;base64,{base64}",
                Name = $"{displayName}_snap.png"
            };

            var imageMessage = MessageFactory.Attachment(attachment);
            imageMessage.Text = $"Screenshot: {displayName}";
            await turnContext.SendActivityAsync(imageMessage, ct);
        }

        // Send terminal text
        if (!string.IsNullOrEmpty(terminalText))
        {
            // Truncate if too long for Teams message
            if (terminalText.Length > 3000)
            {
                terminalText = terminalText.Substring(terminalText.Length - 3000);
            }

            var textMessage = MessageFactory.Text($"```\n{terminalText}\n```");
            await turnContext.SendActivityAsync(textMessage, ct);
        }
    }
}
