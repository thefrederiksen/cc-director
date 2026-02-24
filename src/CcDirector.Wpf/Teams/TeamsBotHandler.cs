using System.Windows.Threading;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Wpf.Controls;
using CcDirector.Wpf.Teams.Commands;
using CcDirector.Wpf.Teams.Models;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;

namespace CcDirector.Wpf.Teams;

/// <summary>
/// Bot Framework activity handler for Teams messages.
/// Routes commands to appropriate handlers.
/// </summary>
public sealed class TeamsBotHandler : ActivityHandler
{
    private readonly SessionManager _sessionManager;
    private readonly TeamsWhitelist _whitelist;
    private readonly IReadOnlyList<RepositoryConfig> _repositories;
    private readonly Dispatcher _dispatcher;
    private readonly Func<Session, TerminalControl?> _getTerminalControl;
    private readonly Action<string> _log;

    // State managed by controller
    private readonly Func<Session?> _getActiveSession;
    private readonly Action<Session> _setActiveSession;
    private readonly Action _clearActiveSession;
    private readonly Action<TeamsUserState> _updateUserState;
    private readonly Action _startQuiescenceMonitor;

    public TeamsBotHandler(
        SessionManager sessionManager,
        TeamsWhitelist whitelist,
        IReadOnlyList<RepositoryConfig> repositories,
        Dispatcher dispatcher,
        Func<Session, TerminalControl?> getTerminalControl,
        Func<Session?> getActiveSession,
        Action<Session> setActiveSession,
        Action clearActiveSession,
        Action<TeamsUserState> updateUserState,
        Action startQuiescenceMonitor,
        Action<string> log)
    {
        _sessionManager = sessionManager;
        _whitelist = whitelist;
        _repositories = repositories;
        _dispatcher = dispatcher;
        _getTerminalControl = getTerminalControl;
        _getActiveSession = getActiveSession;
        _setActiveSession = setActiveSession;
        _clearActiveSession = clearActiveSession;
        _updateUserState = updateUserState;
        _startQuiescenceMonitor = startQuiescenceMonitor;
        _log = log;
    }

    protected override async Task OnMessageActivityAsync(
        ITurnContext<IMessageActivity> turnContext,
        CancellationToken ct)
    {
        var userId = turnContext.Activity.From?.Id ?? "";
        var userName = turnContext.Activity.From?.Name;
        var messageText = turnContext.Activity.Text?.Trim() ?? "";

        _log($"[TeamsBotHandler] Message from {userId} ({userName}): {messageText}");

        // Whitelist check
        if (!_whitelist.IsAllowed(userId))
        {
            _whitelist.LogUnknownUser(userId, userName, messageText);
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Access denied. Contact the administrator to add your user ID to the whitelist."),
                ct);
            return;
        }

        // Update user state with conversation reference
        var userState = new TeamsUserState
        {
            UserId = userId,
            UserName = userName,
            ConversationReference = turnContext.Activity.GetConversationReference(),
            LastActivity = DateTime.UtcNow
        };
        _updateUserState(userState);

        // Parse and route command
        await RouteCommandAsync(turnContext, messageText, ct);
    }

    private async Task RouteCommandAsync(
        ITurnContext turnContext,
        string messageText,
        CancellationToken ct)
    {
        // Normalize command
        var text = messageText.Trim();

        // Handle commands
        if (text.Equals("/ls", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("/list", StringComparison.OrdinalIgnoreCase))
        {
            await ListSessionsCommand.ExecuteAsync(
                turnContext,
                _sessionManager,
                _getActiveSession(),
                ct);
            return;
        }

        if (text.StartsWith("/s ", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("/s", StringComparison.OrdinalIgnoreCase))
        {
            var idPrefix = text.Length > 3 ? text.Substring(3).Trim() : "";
            await SelectSessionCommand.ExecuteAsync(
                turnContext,
                idPrefix,
                _sessionManager,
                _setActiveSession,
                ct);
            return;
        }

        if (text.StartsWith("/new ", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("/new", StringComparison.OrdinalIgnoreCase))
        {
            var repoName = text.Length > 5 ? text.Substring(5).Trim() : "";
            await NewSessionCommand.ExecuteAsync(
                turnContext,
                repoName,
                _sessionManager,
                _repositories,
                _setActiveSession,
                ct);
            return;
        }

        if (text.Equals("/snap", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("/screenshot", StringComparison.OrdinalIgnoreCase))
        {
            await SnapCommand.ExecuteAsync(
                turnContext,
                _getActiveSession(),
                _getTerminalControl,
                _dispatcher,
                ct);
            return;
        }

        if (text.Equals("/sum", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("/summary", StringComparison.OrdinalIgnoreCase))
        {
            await SumCommand.ExecuteAsync(
                turnContext,
                _getActiveSession(),
                _getTerminalControl,
                _dispatcher,
                ct);
            return;
        }

        if (text.StartsWith("/kill ", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("/kill", StringComparison.OrdinalIgnoreCase))
        {
            var idPrefix = text.Length > 6 ? text.Substring(6).Trim() : "";
            await KillSessionCommand.ExecuteAsync(
                turnContext,
                idPrefix,
                _sessionManager,
                _getActiveSession(),
                _clearActiveSession,
                ct);
            return;
        }

        if (text.Equals("/help", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("/?", StringComparison.OrdinalIgnoreCase))
        {
            await SendHelpAsync(turnContext, ct);
            return;
        }

        if (text.Equals("/reload", StringComparison.OrdinalIgnoreCase))
        {
            _whitelist.Reload();
            await turnContext.SendActivityAsync(
                MessageFactory.Text("Whitelist reloaded."),
                ct);
            return;
        }

        // Plain text - send to active session
        await SendInputCommand.ExecuteAsync(
            turnContext,
            text,
            _getActiveSession(),
            _startQuiescenceMonitor,
            ct);
    }

    private static async Task SendHelpAsync(ITurnContext turnContext, CancellationToken ct)
    {
        var help = @"**CC Director Remote Commands**

/ls - List all sessions
/s <id> - Select a session by ID prefix
/new <repo> - Create new session for repository
/snap - Screenshot + recent terminal text
/sum - Summarize terminal output
/kill [id] - Kill session (active or by ID)
/reload - Reload whitelist
/help - Show this help

**Plain text** is sent to the active session as input.";

        await turnContext.SendActivityAsync(MessageFactory.Text(help), ct);
    }

    protected override async Task OnMembersAddedAsync(
        IList<ChannelAccount> membersAdded,
        ITurnContext<IConversationUpdateActivity> turnContext,
        CancellationToken ct)
    {
        foreach (var member in membersAdded)
        {
            if (member.Id != turnContext.Activity.Recipient.Id)
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text("CC Director Remote is ready. Type /help for available commands."),
                    ct);
            }
        }
    }
}
