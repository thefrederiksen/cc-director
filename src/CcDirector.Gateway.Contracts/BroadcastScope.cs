namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The scope a session belongs to for broadcast purposes (issue #1229). A broadcast may reach the
/// sender's own team freely; reaching beyond it needs a human-issued grant. "Team" is derived from
/// the fleet view the Gateway already aggregates, NOT from anything the caller claims, in this order
/// of precedence:
///
///   * A session attached to a MISSION (the first-class unit of work) travels with that Mission - the
///     Architect, Manager, and Workers of one Mission share a <see cref="MissionId"/>, so they are in
///     scope for each other. This is the primary team boundary.
///   * Otherwise a GROUPED session (issue #225 <c>GroupId</c>) travels with its group.
///   * Otherwise a SOLO session is scoped to its own working area: the same repository on the same
///     machine. Sessions sharing that checkout are the ones a shared-tree notice actually concerns.
///
/// Lives in the Contracts assembly so BOTH the Director (which narrows an "all" broadcast to the
/// sender's team before relaying) and the Gateway (which enforces the same rule as the authority)
/// share one definition and cannot drift apart.
/// </summary>
public readonly record struct BroadcastScope(string? MissionId, string? GroupId, string RepoPath, string MachineName)
{
    /// <summary>True when this session is attached to a Mission - its primary team boundary.</summary>
    public bool HasMission => !string.IsNullOrWhiteSpace(MissionId);

    /// <summary>True when this session belongs to a group (issue #225) rather than running solo.</summary>
    public bool IsGrouped => !string.IsNullOrWhiteSpace(GroupId);

    /// <summary>
    /// Build a scope from an aggregated fleet <see cref="SessionDto"/> - the shape the Gateway's
    /// GET /sessions returns, where <see cref="SessionDto.MachineName"/> is populated. Use the
    /// Gateway-internal builder instead when the session record is Director-local (machine empty).
    /// </summary>
    public static BroadcastScope FromAggregatedSession(SessionDto session)
        => new(session.MissionId?.ToString(), session.GroupId, session.RepoPath, session.MachineName);

    /// <summary>
    /// True when <paramref name="target"/> is inside THIS sender's team, by the precedence above: a
    /// Mission-attached sender includes every session in the same Mission; else a grouped sender
    /// includes its group; else a solo sender includes every session in the same repository on the
    /// same machine. Repository comparison is case- and separator-insensitive so "D:\Repo" and
    /// "d:/repo/" match the same checkout.
    /// </summary>
    public bool Includes(BroadcastScope target)
    {
        if (HasMission)
            return string.Equals(MissionId, target.MissionId, StringComparison.OrdinalIgnoreCase);

        if (IsGrouped)
            return string.Equals(GroupId, target.GroupId, StringComparison.OrdinalIgnoreCase);

        return NormalizeRepo(RepoPath) == NormalizeRepo(target.RepoPath)
            && string.Equals(MachineName ?? "", target.MachineName ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Normalize a repository path for comparison: trim, unify separators, drop a trailing
    /// separator, lowercase. Windows paths are case-insensitive, so "D:\Repo\" and "d:/repo" are one
    /// checkout.</summary>
    private static string NormalizeRepo(string? repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return "";
        return repoPath.Trim().Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
    }
}
