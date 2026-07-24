namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One repository on one machine, as pushed by its Director and served fleet-wide by the Gateway
/// (GET /repositories). The verdict strings are folded on the Director side - clients and agents
/// render them verbatim (the dumb-client rule). Read-only at the Gateway: any destructive action
/// runs on the owning Director after a live re-verify (the trust rule).
/// </summary>
public class RepoStatusDto
{
    public string DirectorId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string? RemoteUrl { get; set; }

    /// <summary>"GitHub", "AzureDevOps", "Other", or "None".</summary>
    public string Provider { get; set; } = "";
    public string? Org { get; set; }

    public string Branch { get; set; } = "";
    public bool IsClean { get; set; }
    public int UncommittedCount { get; set; }
    public DateTime? DirtySinceUtc { get; set; }
    public int AheadCount { get; set; }
    public int BehindCount { get; set; }
    public int BehindMainCount { get; set; }

    public int WorktreeCount { get; set; }
    public int WorktreesSafeToReap { get; set; }
    public int WorktreesInUse { get; set; }
    public int WorktreesNeedAttention { get; set; }
    public long WorktreeBytes { get; set; }

    /// <summary>True when the pushing Director had not yet re-verified this entry (warm-start cache).</summary>
    public bool Provisional { get; set; }

    public List<WorktreeDto> Worktrees { get; set; } = new();
}

/// <summary>One linked worktree of a repository. State strings are folded by the Director.</summary>
public class WorktreeDto
{
    public string Path { get; set; } = "";
    public string? Branch { get; set; }

    /// <summary>"safe-to-reap", "in-use", or "needs-attention" - folded, rendered verbatim.</summary>
    public string State { get; set; } = "";

    /// <summary>The one-line reason for the state, folded by the Director.</summary>
    public string Reason { get; set; } = "";

    /// <summary>Labels of live sessions working in this worktree (empty when none).</summary>
    public List<string> SessionLabels { get; set; } = new();

    public long? SizeBytes { get; set; }
    public DateTime? LastActivityUtc { get; set; }
    public int AheadOfMain { get; set; }
    public int BehindMain { get; set; }
    public int DirtyFileCount { get; set; }
    public bool IsDetachedHead { get; set; }
}

/// <summary>
/// One flattened worktree row for GET /worktrees and `cc-devthrottle worktree list`: the whole
/// fleet's worktrees, each carrying its repository, machine, verdict, and occupying sessions.
/// </summary>
public class FleetWorktreeDto
{
    public string RepoName { get; set; } = "";
    public string RepoPath { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string DirectorId { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Branch { get; set; }
    public string State { get; set; } = "";
    public string Reason { get; set; } = "";
    public List<string> SessionLabels { get; set; } = new();
    public long? SizeBytes { get; set; }
    public DateTime? LastActivityUtc { get; set; }

    /// <summary>How old the pushing Director's data is, in seconds, at serve time.</summary>
    public double DataAgeSeconds { get; set; }
}
