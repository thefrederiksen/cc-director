using System.IO;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;

namespace CcDirector.Wpf.Teams.Commands;

/// <summary>
/// Handles /new [repo] command - creates a new session for a repository.
/// </summary>
public static class NewSessionCommand
{
    public static async Task<Session?> ExecuteAsync(
        ITurnContext turnContext,
        string repoName,
        SessionManager sessionManager,
        IReadOnlyList<RepositoryConfig> repositories,
        Action<Session> setActiveSession,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repoName))
        {
            // List available repos
            if (repositories.Count == 0)
            {
                await turnContext.SendActivityAsync(
                    MessageFactory.Text("No repositories configured. Add repositories to appsettings.json."),
                    ct);
                return null;
            }

            var repoList = string.Join("\n", repositories.Select(r => $"- {r.Name}"));
            await turnContext.SendActivityAsync(
                MessageFactory.Text($"Usage: /new <repo-name>\n\nAvailable repositories:\n{repoList}"),
                ct);
            return null;
        }

        // Find repository by name (case-insensitive partial match)
        var matches = repositories
            .Where(r => r.Name.Contains(repoName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text($"No repository found matching '{repoName}'"),
                ct);
            return null;
        }

        if (matches.Count > 1)
        {
            // Check for exact match first
            var exactMatch = matches.FirstOrDefault(r => r.Name.Equals(repoName, StringComparison.OrdinalIgnoreCase));
            if (exactMatch != null)
            {
                matches = new List<RepositoryConfig> { exactMatch };
            }
            else
            {
                var names = string.Join(", ", matches.Select(r => r.Name));
                await turnContext.SendActivityAsync(
                    MessageFactory.Text($"Multiple repositories match '{repoName}': {names}\nPlease be more specific."),
                    ct);
                return null;
            }
        }

        var repo = matches[0];

        if (!Directory.Exists(repo.Path))
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text($"Repository path does not exist: {repo.Path}"),
                ct);
            return null;
        }

        await turnContext.SendActivityAsync(
            MessageFactory.Text($"Creating session for {repo.Name}..."),
            ct);

        try
        {
            var session = sessionManager.CreateSession(repo.Path);
            setActiveSession(session);

            await turnContext.SendActivityAsync(
                MessageFactory.Text($"Session created: {repo.Name} ({session.Id.ToString().Substring(0, 8)})"),
                ct);

            return session;
        }
        catch (Exception ex)
        {
            await turnContext.SendActivityAsync(
                MessageFactory.Text($"Failed to create session: {ex.Message}"),
                ct);
            return null;
        }
    }
}
