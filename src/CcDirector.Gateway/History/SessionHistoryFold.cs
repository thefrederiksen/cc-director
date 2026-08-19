using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.History;

/// <summary>
/// THE fold for the work-history record (issue #2194): every display verdict a history row carries -
/// the ending label, the display tone, and the one-line description - is computed HERE, once, on the
/// Gateway, and stamped onto what clients read. The standing dumb-client rule: a client that rules
/// for itself renders something plausible instead of something true the moment it meets a state it
/// did not expect. Adding a new ending or description source is one edit here, never a branch in a
/// client.
/// </summary>
public static class SessionHistoryFold
{
    /// <summary>Display tone for a row that has not ended.</summary>
    public const string ToneLive = "live";

    /// <summary>
    /// The wording for an ending. Interrupted endings say "last seen" rather than pretending the
    /// Gateway knows the instant the session died - the end time of an interrupted row is the last
    /// observation, and the label must not read as a measurement (issue #2157's honesty rule).
    /// </summary>
    public static string EndingLabel(string endingKind, bool crashed, DateTime? endedAtUtc)
        => endingKind switch
        {
            SessionHistoryEndings.Closed => "Closed",
            SessionHistoryEndings.Finished => crashed ? "Agent exited unexpectedly" : "Finished",
            SessionHistoryEndings.DirectorStopped => "Director stopped",
            SessionHistoryEndings.Interrupted => endedAtUtc is { } seen
                ? $"Interrupted - last seen {seen:yyyy-MM-dd HH:mm} UTC"
                : "Interrupted",
            _ => endingKind,
        };

    /// <summary>The display tone for a row: live (open), neutral (deliberate endings), or attention
    /// (interrupted - the group the owner came back for).</summary>
    public static string EndingTone(string? endingKind)
        => endingKind switch
        {
            null or "" => ToneLive,
            SessionHistoryEndings.Interrupted => "attention",
            SessionHistoryEndings.Finished => "ok",
            _ => "neutral",
        };

    /// <summary>
    /// The one-line description of what the session is doing / was for, folded from the row's facts in
    /// the #1862 priority order: the mission (with the role when declared), then the first prompt, then
    /// the session name plus repository as the floor. Never empty - a row that says only an id is a row
    /// the owner cannot act on.
    /// </summary>
    public static string DescriptionLine(string? missionName, string? sessionRole, string? firstPromptLine,
        string? sessionName, string? repoName, string? repoPath)
    {
        if (!string.IsNullOrWhiteSpace(missionName))
            return string.IsNullOrWhiteSpace(sessionRole)
                ? $"Mission: {missionName.Trim()}"
                : $"Mission: {missionName.Trim()} ({sessionRole.Trim()})";

        if (!string.IsNullOrWhiteSpace(firstPromptLine))
            return firstPromptLine.Trim();

        var name = string.IsNullOrWhiteSpace(sessionName) ? "Unnamed session" : sessionName.Trim();
        var where = !string.IsNullOrWhiteSpace(repoName) ? repoName.Trim()
            : !string.IsNullOrWhiteSpace(repoPath) ? repoPath.Trim()
            : null;
        return where is null ? name : $"{name} in {where}";
    }

    /// <summary>
    /// One user prompt collapsed to a single description-sized line: whitespace folded, trimmed to
    /// <paramref name="maxChars"/>. Returns null for blank input.
    /// </summary>
    public static string? FirstPromptLine(string? promptText, int maxChars = 200)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return null;
        var folded = string.Join(' ',
            promptText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (folded.Length == 0) return null;
        return folded.Length <= maxChars ? folded : folded[..maxChars].TrimEnd() + "...";
    }

    /// <summary>
    /// The grouping key for the repository roll-up: owner/repo from the origin remote when known
    /// (worktrees of one repository share it - which is exactly why it is the group), else the
    /// repository path, else a literal bucket for sessions with no repository at all.
    /// </summary>
    public static string RepoKey(string? repoName, string? repoPath)
        => !string.IsNullOrWhiteSpace(repoName) ? repoName.Trim()
            : !string.IsNullOrWhiteSpace(repoPath) ? repoPath.Trim()
            : "(no repository)";

    /// <summary>Project a history row to its wire DTO, stamping every folded verdict.</summary>
    public static WorkHistorySessionDto ToDto(SessionHistoryEntity e) => new()
    {
        SessionId = e.SessionId,
        SessionNumber = e.SessionNumber,
        SessionName = e.SessionName,
        MachineName = e.MachineName,
        DirectorVersion = e.DirectorVersion,
        DirectorId = e.DirectorId,
        RepoPath = e.RepoPath,
        RepoName = e.RepoName,
        AgentKind = e.AgentKind,
        Model = e.Model,
        MissionName = e.MissionName,
        MissionId = e.MissionId,
        SessionRole = e.SessionRole,
        // Birth facts (issue #982). Passed through as stored, INCLUDING null: a row from before the
        // fields existed is not the same as a session whose origin was asked for and unknown, and the
        // reader is entitled to tell them apart.
        OriginKind = e.OriginKind,
        OriginSurface = e.OriginSurface,
        ParentSessionId = e.ParentSessionId,
        StartedAtUtc = e.StartedAtUtc,
        LastActivityUtc = e.LastActivityUtc,
        LastSeenUtc = e.LastSeenUtc,
        EndingKind = string.IsNullOrEmpty(e.EndingKind) ? null : e.EndingKind,
        EndingLabel = string.IsNullOrEmpty(e.EndingLabel) ? null : e.EndingLabel,
        EndingTone = EndingTone(e.EndingKind),
        EndedAtUtc = e.EndedAtUtc,
        DescriptionLine = DescriptionLine(e.MissionName, e.SessionRole, e.FirstPromptLine,
            e.SessionName, e.RepoName, e.RepoPath),
        TurnCount = e.TurnCount,
        AgentTurnCount = e.AgentTurnCount,
        IdleSeconds = e.CumulativeIdleSeconds,
        WaitingStretchCount = e.WaitingStretchCount,
        InputCharacterCount = e.InputCharacterCount,
        InputTokens = e.InputTokens,
        OutputTokens = e.OutputTokens,
        CacheReadTokens = e.CacheReadTokens,
        CacheCreationTokens = e.CacheCreationTokens,
        PeakContextTokens = e.PeakContextTokens,
        SummaryKind = string.IsNullOrEmpty(e.SummaryKind) ? null : e.SummaryKind,
        SummaryIsPartial = e.SummaryIsPartial,
        SummaryText = e.SummaryText,
        WhatWasBuilt = ParseList(e.WhatWasBuiltJson),
        LeftUnverified = ParseList(e.LeftUnverifiedJson),
        Branches = ParseList(e.BranchesJson),
        PullRequests = ParseList(e.PullRequestsJson),
        Commits = ParseList(e.CommitsJson),
    };

    /// <summary>Parse a stored JSON string array; null in, null out. A corrupt value returns null and is
    /// logged by the caller's read path rather than failing the whole read.</summary>
    public static IReadOnlyList<string>? ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Serialize a list for storage; null/empty in, null out.</summary>
    public static string? ToJsonList(IReadOnlyList<string>? values)
    {
        if (values is null) return null;
        var cleaned = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        return cleaned.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(cleaned);
    }
}
