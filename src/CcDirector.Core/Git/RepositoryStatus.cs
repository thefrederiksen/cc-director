namespace CcDirector.Core.Git;

/// <summary>Which hosting service a repository's origin remote points at.</summary>
public enum RepoProvider
{
    /// <summary>No origin remote (or the URL could not be read).</summary>
    None,

    /// <summary>github.com.</summary>
    GitHub,

    /// <summary>dev.azure.com / *.visualstudio.com.</summary>
    AzureDevOps,

    /// <summary>Has a remote, but not one we recognise.</summary>
    Other,
}

/// <summary>
/// The at-a-glance state of one on-disk repository: where its remote lives, how far its branch has
/// drifted, how much is uncommitted, and a summary of its worktrees. Composed on the service side
/// from the existing git providers plus the worktree detector; the UI renders it.
/// </summary>
public sealed class RepositoryStatus
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>The origin remote URL, or null when there is no origin.</summary>
    public string? RemoteUrl { get; init; }

    public RepoProvider Provider { get; init; }

    /// <summary>GitHub owner or Azure DevOps org, parsed from the remote URL; null when unknown.</summary>
    public string? Org { get; init; }

    public string Branch { get; init; } = "";
    public bool IsDetachedHead { get; init; }
    public bool HasUpstream { get; init; }

    public int AheadCount { get; init; }
    public int BehindCount { get; init; }

    /// <summary>How far the branch is behind origin/main; -1 when it is on main already or unknown.</summary>
    public int BehindMainCount { get; init; }

    public bool IsClean { get; init; }

    /// <summary>Modified, staged, and untracked entries reported by <c>git status --porcelain</c>.</summary>
    public int UncommittedCount { get; init; }

    /// <summary>Linked worktrees for this repository (excludes the primary checkout).</summary>
    public int WorktreeCount { get; init; }

    public int WorktreesSafeToReap { get; init; }
    public int WorktreesInUse { get; init; }
    public int WorktreesNeedAttention { get; init; }

    public bool Success { get; init; }
    public string? Error { get; init; }
}
