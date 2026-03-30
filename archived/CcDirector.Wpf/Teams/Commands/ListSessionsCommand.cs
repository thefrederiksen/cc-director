using System.IO;
using AdaptiveCards;
using CcDirector.Core.Pipes;
using CcDirector.Core.Sessions;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;

namespace CcDirector.Wpf.Teams.Commands;

/// <summary>
/// Handles /ls command - lists all active sessions with status.
/// </summary>
public static class ListSessionsCommand
{
    public static async Task ExecuteAsync(
        ITurnContext turnContext,
        SessionManager sessionManager,
        Session? activeSession,
        CancellationToken ct)
    {
        var sessions = sessionManager.ListSessions().ToList();

        if (sessions.Count == 0)
        {
            await turnContext.SendActivityAsync(MessageFactory.Text("No active sessions."), ct);
            return;
        }

        var card = BuildSessionsCard(sessions, activeSession);
        var attachment = new Attachment
        {
            ContentType = AdaptiveCard.ContentType,
            Content = card
        };

        await turnContext.SendActivityAsync(MessageFactory.Attachment(attachment), ct);
    }

    private static AdaptiveCard BuildSessionsCard(List<Session> sessions, Session? activeSession)
    {
        var card = new AdaptiveCard(new AdaptiveSchemaVersion(1, 4))
        {
            Body = new List<AdaptiveElement>
            {
                new AdaptiveTextBlock
                {
                    Text = "Active Sessions",
                    Size = AdaptiveTextSize.Large,
                    Weight = AdaptiveTextWeight.Bolder
                }
            }
        };

        foreach (var session in sessions)
        {
            card.Body.Add(BuildSessionRow(session, activeSession?.Id == session.Id));
        }

        card.Body.Add(new AdaptiveTextBlock
        {
            Text = "Use /s <id> to select, /new <repo> to create",
            Size = AdaptiveTextSize.Small,
            Color = AdaptiveTextColor.Light,
            IsSubtle = true,
            Spacing = AdaptiveSpacing.Medium
        });

        return card;
    }

    private static AdaptiveContainer BuildSessionRow(Session session, bool isActive)
    {
        var repoName = Path.GetFileName(session.RepoPath);
        var shortId = session.Id.ToString().Substring(0, 8);
        var statusIcon = GetStatusIcon(session.ActivityState);

        return new AdaptiveContainer
        {
            Items = new List<AdaptiveElement>
            {
                new AdaptiveColumnSet
                {
                    Columns = new List<AdaptiveColumn>
                    {
                        new AdaptiveColumn
                        {
                            Width = "auto",
                            Items = new List<AdaptiveElement>
                            {
                                new AdaptiveTextBlock { Text = statusIcon, Size = AdaptiveTextSize.Medium }
                            }
                        },
                        new AdaptiveColumn
                        {
                            Width = "stretch",
                            Items = new List<AdaptiveElement>
                            {
                                new AdaptiveTextBlock
                                {
                                    Text = session.CustomName ?? repoName,
                                    Weight = isActive ? AdaptiveTextWeight.Bolder : AdaptiveTextWeight.Default,
                                    Color = isActive ? AdaptiveTextColor.Accent : AdaptiveTextColor.Default
                                }
                            }
                        },
                        new AdaptiveColumn
                        {
                            Width = "auto",
                            Items = new List<AdaptiveElement>
                            {
                                new AdaptiveTextBlock
                                {
                                    Text = shortId,
                                    Size = AdaptiveTextSize.Small,
                                    Color = AdaptiveTextColor.Light,
                                    IsSubtle = true
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    private static string GetStatusIcon(ActivityState state)
    {
        return state switch
        {
            ActivityState.Idle => "[OK]",
            ActivityState.Starting => "[..]",
            ActivityState.Working => "[..]",
            ActivityState.WaitingForInput => "[OK]",
            ActivityState.WaitingForPerm => "[!]",
            ActivityState.Exited => "[X]",
            _ => "[?]"
        };
    }
}
