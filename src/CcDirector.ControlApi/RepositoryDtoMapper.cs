using CcDirector.Core.Git;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps the Director's live repository model to the wire DTOs pushed to the Gateway and served by
/// the local relay. The fold happens HERE, once: worktree safety becomes a finished state string
/// every client renders verbatim.
/// </summary>
public static class RepositoryDtoMapper
{
    /// <summary>
    /// Builds the raw DTO and then applies the ONE shared repository-level fold
    /// (<see cref="FleetWorktreeFold.FoldRepositoryForServe"/>, ruling R2-3): a provisional
    /// entry's safe count folds to zero and its worktrees to "verifying". The Gateway applies
    /// the same fold at serve time, so a pre-fix Director's pushed shape cannot bypass it.
    /// </summary>
    public static RepoStatusDto Map(RepositoryStatus s, string directorId, string machineName)
        => FleetWorktreeFold.FoldRepositoryForServe(new RepoStatusDto
        {
            DirectorId = directorId,
            MachineName = machineName,
            Path = s.Path,
            Name = s.Name,
            RemoteUrl = s.RemoteUrl,
            Provider = s.Provider.ToString(),
            Org = s.Org,
            Branch = s.Branch,
            IsClean = s.IsClean,
            UncommittedCount = s.UncommittedCount,
            DirtySinceUtc = s.DirtySinceUtc,
            AheadCount = s.AheadCount,
            BehindCount = s.BehindCount,
            BehindMainCount = s.BehindMainCount,
            WorktreeCount = s.WorktreeCount,
            WorktreesSafeToReap = s.WorktreesSafeToReap,
            WorktreesInUse = s.WorktreesInUse,
            WorktreesNeedAttention = s.WorktreesNeedAttention,
            WorktreeBytes = s.WorktreeBytes,
            Provisional = s.Provisional,
            Worktrees = s.Worktrees.Select(Map).ToList(),
        });

    public static WorktreeDto Map(WorktreeInfo w) => new()
    {
        Path = w.Path,
        Branch = w.Branch,
        State = StateString(w.Safety),
        Reason = w.Explanation,
        SessionLabels = w.OpenSessions.ToList(),
        SizeBytes = w.SizeBytes,
        LastActivityUtc = w.LastActivityUtc,
        AheadOfMain = w.AheadOfMain,
        BehindMain = w.BehindMain,
        DirtyFileCount = w.DirtyFileCount,
        IsDetachedHead = w.IsDetachedHead,
    };

    /// <summary>The folded state string. Every surface renders this; nothing re-derives it.</summary>
    public static string StateString(WorktreeSafety safety) => safety switch
    {
        WorktreeSafety.SafeToReap => "safe-to-reap",
        WorktreeSafety.InUseBySession => "in-use",
        _ => "needs-attention",
    };

    /// <summary>
    /// Flattens repository DTOs into fleet worktree rows (GET /worktrees). Delegates to the one
    /// shared fold in the contracts assembly, which fails closed for provisional repositories.
    /// </summary>
    public static List<FleetWorktreeDto> Flatten(IEnumerable<RepoStatusDto> repositories, double dataAgeSeconds = 0)
        => FleetWorktreeFold.Flatten(repositories, dataAgeSeconds);
}
