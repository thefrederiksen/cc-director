namespace CcDirector.Core.Git;

/// <summary>
/// The two states a worktree can be in from the reaper's point of view.
/// The verdict is computed once, on the service side, and the UI only renders it
/// (the "dumb client" rule in CLAUDE.md).
/// </summary>
public enum WorktreeSafety
{
    /// <summary>Work is provably on origin/main and the tree is clean - safe to remove mechanically.</summary>
    SafeToReap,

    /// <summary>Carries unmerged commits, uncommitted content, or is the primary checkout - never auto-removed.</summary>
    NeedsAttention,
}

/// <summary>
/// The specific signal that decided a worktree's safety verdict, so the UI can show a
/// one-line reason and the reaper can log its proof-of-safety.
/// </summary>
public enum WorktreeSafetyReason
{
    // --- Safe-to-reap reasons (each is sufficient proof the work reached origin/main) ---

    /// <summary>C1: the branch's pull request is merged (authoritative; recognises squash merges).</summary>
    PullRequestMerged,

    /// <summary>C2: the origin branch no longer exists after a prune - with delete-branch-on-merge, gone means merged.</summary>
    OriginBranchGone,

    /// <summary>C3: <c>git cherry</c> reports the branch adds nothing origin/main lacks.</summary>
    ContainedInMain,

    /// <summary>Detached-HEAD case: its HEAD commit is an ancestor of origin/main.</summary>
    DetachedHeadAncestorOfMain,

    // --- Needs-attention reasons (never auto-removed) ---

    /// <summary>The repository's primary checkout - never removed.</summary>
    PrimaryCheckout,

    /// <summary>The tree has modified, staged, or untracked content - deleting would lose work.</summary>
    UncommittedChanges,

    /// <summary>No merge signal could be established - fail closed, treat as stranded.</summary>
    NotProvenMerged,

    /// <summary>A required git probe failed, so safety could not be proven - fail closed.</summary>
    InspectionFailed,
}

/// <summary>
/// The plain facts about a worktree, gathered from git, that feed the pure
/// <see cref="WorktreeSafetyEvaluator"/>. Keeping the facts separate from the git
/// calls lets the fail-closed decision be unit-tested exhaustively without a repository.
/// </summary>
public sealed record WorktreeFacts
{
    /// <summary>True when this is the repository's main working tree (guardrail A).</summary>
    public bool IsPrimary { get; init; }

    /// <summary>True when the worktree is on a detached HEAD rather than a branch.</summary>
    public bool IsDetachedHead { get; init; }

    /// <summary>True when <c>git status --porcelain</c> is empty (no content at all).</summary>
    public bool IsClean { get; init; }

    /// <summary>C1: the branch has a merged pull request.</summary>
    public bool PullRequestMerged { get; init; }

    /// <summary>C2: the origin branch was deleted after a prune.</summary>
    public bool OriginBranchGone { get; init; }

    /// <summary>C3: <c>git cherry</c> is clean - the branch adds nothing origin/main lacks.</summary>
    public bool ContainedInMain { get; init; }

    /// <summary>Detached-HEAD case: the HEAD commit is an ancestor of origin/main.</summary>
    public bool DetachedHeadIsAncestorOfMain { get; init; }

    /// <summary>
    /// False when a required git probe failed (e.g. origin/main could not be resolved),
    /// forcing a fail-closed verdict rather than a guess.
    /// </summary>
    public bool InspectionSucceeded { get; init; } = true;
}

/// <summary>The evaluator's decision: the safety bucket, the deciding reason, and a one-line explanation.</summary>
public sealed class WorktreeVerdict
{
    public WorktreeSafety Safety { get; init; }
    public WorktreeSafetyReason Reason { get; init; }
    public string Explanation { get; init; } = "";
}

/// <summary>
/// A raw entry parsed from <c>git worktree list --porcelain</c>, before any safety reasoning.
/// </summary>
public sealed class RawWorktreeEntry
{
    public string Path { get; init; } = "";
    public string Head { get; init; } = "";

    /// <summary>The short branch name, or null for a detached HEAD.</summary>
    public string? Branch { get; init; }

    public bool IsDetached { get; init; }
    public bool IsBare { get; init; }
}

/// <summary>
/// The full, display-ready record for one worktree: its identity, its dirty/ahead/behind
/// counts, and the computed safety verdict. The UI renders these verbatim.
/// </summary>
public sealed class WorktreeInfo
{
    public string Path { get; init; } = "";

    /// <summary>The short branch name, or null for a detached HEAD.</summary>
    public string? Branch { get; init; }

    public string HeadCommit { get; init; } = "";
    public bool IsPrimary { get; init; }
    public bool IsDetachedHead { get; init; }
    public bool IsClean { get; init; }

    /// <summary>Number of modified/staged/untracked entries reported by <c>git status --porcelain</c>.</summary>
    public int DirtyFileCount { get; init; }

    public int AheadOfMain { get; init; }
    public int BehindMain { get; init; }
    public bool HasOpenPullRequest { get; init; }

    public WorktreeSafety Safety { get; init; }
    public WorktreeSafetyReason Reason { get; init; }
    public string Explanation { get; init; } = "";
}

/// <summary>
/// The result of enumerating a repository's worktrees: the full list plus success/error,
/// with convenience projections into the safe-to-reap and needs-attention groups the UI shows.
/// </summary>
public sealed class WorktreeInventory
{
    public string RepositoryPath { get; init; } = "";
    public IReadOnlyList<WorktreeInfo> Worktrees { get; init; } = Array.Empty<WorktreeInfo>();
    public bool Success { get; init; }
    public string? Error { get; init; }

    /// <summary>The worktrees proven safe to reap right now - what the badge counts and the button removes.</summary>
    public IReadOnlyList<WorktreeInfo> SafeToReap =>
        Worktrees.Where(w => w.Safety == WorktreeSafety.SafeToReap).ToList();

    /// <summary>Stranded or dirty worktrees a human or agent must decide about (excludes the primary checkout).</summary>
    public IReadOnlyList<WorktreeInfo> NeedsAttention =>
        Worktrees.Where(w => w.Safety == WorktreeSafety.NeedsAttention && !w.IsPrimary).ToList();

    /// <summary>The badge count: how many worktrees can be reaped now.</summary>
    public int SafeToReapCount => Worktrees.Count(w => w.Safety == WorktreeSafety.SafeToReap);
}
