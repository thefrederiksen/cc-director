using CcDirector.Core.Claude;
using CcDirector.Core.Git;
using CcDirector.Core.History;
using CcDirector.Core.Sessions;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Gateway Cleanup mission, Phase 0 (spine): the CATALOG and DIRECTOR-LEVEL READ area of the tunnel command
/// surface. It owns the reads that are not addressed to one session (git status is per-session but grouped
/// here with the catalogs). Worker R2 filled it with: <c>git-status</c>, <c>coaching-categories</c>,
/// <c>claude-sessions</c>, <c>interrupted-list</c>, and <c>fs-list</c> (the list-directory read).
///
/// Each core is extracted verbatim from that read's Director REST lambda. That REST route now calls this
/// SAME core, so the tunnel verb and the route cannot drift (the core is the single source of truth); Phase 1
/// deletes the routes and leaves the cores reached only over the tunnel. A preserved try/catch is kept ONLY
/// where the source route had one (<c>fs-list</c>), so behaviour is byte-identical.
///
/// Two of the group's REST reads were NOT lifted, because they depend on host state that the tunnel command
/// surface does not carry (routing either faithfully would require a spine change, not a single-area edit):
/// <c>GET /facts</c> embeds the host's injected version string (<c>ControlApiHost._version</c>), which is
/// absent from <see cref="SessionCommandContext"/>; and <c>GET /repos</c> reads the live per-host
/// <c>RepositoryRegistry</c> instance, which is a Map-time dependency not carried by the context or the
/// stream client's <see cref="SessionCommandServices"/>. Both stay as their own REST routes for now.
/// </summary>
internal sealed class CatalogReadExecutor : ISessionCommandArea
{
    /// <summary>
    /// The git-status provider, ONE shared instance (its own ten-second cache), exactly as the REST route
    /// held a single provider across requests. The REST route now reaches this same instance through the
    /// core, so both callers share the cache.
    /// </summary>
    private static readonly GitStatusProvider GitProvider = new();

    public IReadOnlyCollection<string> Verbs { get; } = new[]
    {
        "git-status",
        "coaching-categories",
        "claude-sessions",
        "interrupted-list",
        "fs-list",
    };

    public async Task<DirectorCommandResult> ExecuteAsync(SessionCommandContext context, DirectorCommand command, CancellationToken cancellationToken)
    {
        return command.Verb switch
        {
            "git-status" => await GitStatus(context.SessionManager, command, cancellationToken),
            "coaching-categories" => CoachingCategories(),
            "claude-sessions" => ClaudeSessions(command),
            "interrupted-list" => InterruptedList(),
            "fs-list" => FsList(command),
            _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"verb '{command.Verb}' is not handled by the catalog read area"),
        };
    }

    /// <summary>
    /// The <c>git-status</c> verb: a read-only source-control snapshot for a session's repo. Mirrors the
    /// Director's <c>GET /sessions/{sid}/git</c> lambda - invalid id -&gt; BadRequest, missing session -&gt;
    /// NotFound - and returns a serialized <see cref="GitSnapshot"/>. The summary fields come from
    /// <see cref="WingmanService.GitSnapshotAsync"/>; when the repo reads "ok" the snapshot is additively
    /// enriched with the per-file staged/unstaged lists from <see cref="GitStatusProvider"/>, exactly as the
    /// route did. The source route had no try/catch (GitSnapshotAsync owns its own), so none is added here.
    /// </summary>
    internal static async Task<DirectorCommandResult> GitStatus(SessionManager sessionManager, DirectorCommand command, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(command.SessionId, out var guid))
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, "invalid session id format");

        var session = sessionManager.GetSession(guid);
        if (session is null)
            return DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        var snap = await WingmanService.GitSnapshotAsync(session.RepoPath, cancellationToken);
        if (snap.Status == "ok")
        {
            var files = await GitProvider.GetStatusAsync(session.RepoPath);
            if (files.Success)
                GitChangeMapper.Enrich(snap, files);
        }
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(snap));
    }

    /// <summary>
    /// The <c>coaching-categories</c> verb: the coaching quick-launch cards (Assistant / Coach) with their
    /// Director-resolved on-disk paths. Mirrors the Director's <c>GET /coaching/categories</c> lambda - a
    /// static two-entry list, always a 200 - and returns a serialized <see cref="List{CoachingCategoryDto}"/>.
    /// No target session and no host state, so this always succeeds.
    /// </summary>
    internal static DirectorCommandResult CoachingCategories()
    {
        FileLog.Write("[CatalogReadExecutor] coaching-categories");
        var cats = new List<CoachingCategoryDto>
        {
            new()
            {
                Key = "assistant",
                Label = "Assistant",
                Description = "Tasks, contacts, daily briefing",
                Path = CcStorage.CoachingCategory("assistant"),
            },
            new()
            {
                Key = "coach",
                Label = "Coach",
                Description = "Life coaching across all domains",
                Path = CcStorage.CoachingCategory("coach"),
            },
        };
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(cats));
    }

    /// <summary>
    /// The <c>claude-sessions</c> verb: the resumable Claude Code sessions (the Resume Session tab). Mirrors
    /// the Director's <c>GET /claude-sessions</c> lambda - merges the workspace history entries (which carry
    /// custom name/color) with the Claude session-index metadata, de-duplicates by Claude session id, applies
    /// the optional <c>repo</c> filter from the payload, orders by last-used descending, and returns a
    /// serialized <see cref="List{ClaudeSessionDto}"/>. Always a 200 (there is no session to miss).
    /// </summary>
    internal static DirectorCommandResult ClaudeSessions(DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<ClaudeSessionsRequest>(command.PayloadJson);
        var repo = request?.Repo;
        FileLog.Write($"[CatalogReadExecutor] claude-sessions: repo={repo}");

        var claudeMeta = new Dictionary<string, ClaudeSessionMetadata>(StringComparer.Ordinal);
        foreach (var cm in ClaudeSessionReader.ScanAllProjects())
            claudeMeta.TryAdd(cm.SessionId, cm);

        var history = new SessionHistoryStore().LoadAll();
        var dtos = new List<ClaudeSessionDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // History entries first (they carry custom name/color), enriched with Claude metadata.
        foreach (var entry in history)
        {
            if (string.IsNullOrEmpty(entry.ClaudeSessionId) || !seen.Add(entry.ClaudeSessionId))
                continue;
            claudeMeta.TryGetValue(entry.ClaudeSessionId, out var meta);
            dtos.Add(new ClaudeSessionDto
            {
                ClaudeSessionId = entry.ClaudeSessionId,
                RepoPath = entry.RepoPath,
                ProjectName = ControlEndpoints.ProjectNameOf(entry.RepoPath),
                CustomName = entry.CustomName,
                CustomColor = entry.CustomColor,
                MessageCount = meta?.MessageCount ?? 0,
                Summary = meta?.Summary ?? meta?.FirstPrompt ?? entry.FirstPromptSnippet,
                LastUsedUtc = entry.LastUsedAt == default ? meta?.Modified : entry.LastUsedAt.UtcDateTime,
            });
        }

        // Then any Claude sessions not tracked by a workspace history entry.
        foreach (var meta in claudeMeta.Values)
        {
            if (string.IsNullOrEmpty(meta.SessionId) || !seen.Add(meta.SessionId))
                continue;
            dtos.Add(new ClaudeSessionDto
            {
                ClaudeSessionId = meta.SessionId,
                RepoPath = meta.ProjectPath ?? string.Empty,
                ProjectName = ControlEndpoints.ProjectNameOf(meta.ProjectPath ?? string.Empty),
                MessageCount = meta.MessageCount,
                Summary = meta.Summary ?? meta.FirstPrompt,
                LastUsedUtc = meta.Modified == DateTime.MinValue ? null : meta.Modified,
            });
        }

        if (!string.IsNullOrWhiteSpace(repo))
        {
            var wanted = ControlEndpoints.NormalizeRepoPath(repo);
            dtos = dtos.Where(d => !string.IsNullOrEmpty(d.RepoPath)
                                   && ControlEndpoints.NormalizeRepoPath(d.RepoPath) == wanted).ToList();
        }

        var ordered = dtos
            .OrderByDescending(d => d.LastUsedUtc ?? DateTime.MinValue)
            .ToList();
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(ordered));
    }

    /// <summary>
    /// The <c>interrupted-list</c> verb: the pending crash-journal recoveries this Director found on startup
    /// (dead Directors and their recoverable session rosters). Mirrors the Director's <c>GET /interrupted</c>
    /// lambda - a read of <see cref="DirectorCrashJournal.ListPendingRecoveries"/>, always a 200 - and returns
    /// a serialized <see cref="IReadOnlyList{DirectorCrashJournalData}"/>.
    /// </summary>
    internal static DirectorCommandResult InterruptedList()
    {
        FileLog.Write("[CatalogReadExecutor] interrupted-list");
        var pending = DirectorCrashJournal.ListPendingRecoveries();
        return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(pending));
    }

    /// <summary>
    /// The <c>fs-list</c> verb (the list-directory read): the remote folder browser. Mirrors the Director's
    /// <c>GET /fs/list</c> lambda - lists the drive roots when the payload path is null/blank, otherwise the
    /// named directory - and returns a serialized <see cref="DirectoryListingDto"/>. The source route wrapped
    /// the listing in a try/catch that turned any fault (a missing directory, an access error) into a 400 with
    /// the message, so that try/catch is preserved here and surfaced as a <see cref="DirectorCommandStatus.BadRequest"/>.
    /// </summary>
    internal static DirectorCommandResult FsList(DirectorCommand command)
    {
        var request = SessionCommandExecutor.Deserialize<FsListRequest>(command.PayloadJson);
        var path = request?.Path;
        FileLog.Write($"[CatalogReadExecutor] fs-list: path={path}");
        try
        {
            return DirectorCommandResult.Success(SessionCommandExecutor.Serialize(ControlEndpoints.ListDirectory(path)));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CatalogReadExecutor] fs-list FAILED: {ex.Message}");
            return DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, ex.Message);
        }
    }
}
