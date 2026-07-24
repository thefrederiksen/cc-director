namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// The LATEST repo-state snapshot a Director pushed for one repository (issue #2118), in the
/// <c>repo_state</c> table: the branches and worktrees the morning report's hygiene recommendations are
/// built from. The Gateway knows a repository exists; only the machine holding the checkout can say what
/// its git hygiene looks like, so this is the one feed the report cannot assemble from Gateway-side stores.
///
/// OVERWRITE, NOT APPEND. One row per (tenant, director, repository path); a new push replaces the row.
/// The report answers "what does this look like NOW", so a history would be storage and retention work
/// bought for a question nobody asks. <see cref="ReceivedAtUtc"/> is stamped by the Gateway and
/// <see cref="CollectedAtUtc"/> by the Director, so a stale feed is visible as itself rather than as a
/// clean repository - a snapshot the report considers too old is dropped, never quietly aged into a
/// recommendation.
///
/// KEY: composite <c>(tenant_id, DirectorId, RepoPath)</c> - a caller-supplied key, which is exactly why
/// the tenant is part of it: two accounts can register the same repository path on identically-named
/// machines, and without the tenant in the key one would fail to insert over the other and learn from the
/// failure that the row exists.
///
/// THE PAYLOAD CARRIES NAMES, PATHS, COUNTS AND DATES - NEVER CONTENT. <see cref="BranchesJson"/> and
/// <see cref="WorktreesJson"/> are serialized <c>RepoStateBranchDto</c> / <c>RepoStateWorktreeDto</c> lists,
/// whose shape has no room for a file body, a diff, or a commit message. That boundary matters most here,
/// because this is the row where a private repository's contents would come to rest in a hosted,
/// multi-tenant database.
///
/// <c>tenant_id</c> + the global query filter are inherited from <see cref="TenantScopedEntity"/>.
/// </summary>
public sealed class RepoStateEntity : TenantScopedEntity
{
    /// <summary>The Director that pushed this snapshot. Part of the composite primary key.</summary>
    public string DirectorId { get; set; } = "";

    /// <summary>The repository's primary checkout path on that machine. Part of the composite primary key.</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>The pushing machine's display name, for the report's deep links.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>The repository's display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The default branch, or null when the Director could not determine it - in which case every
    /// merged-ness in the payload is null too and no hygiene recommendation is made about this repository.</summary>
    public string? DefaultBranch { get; set; }

    /// <summary>The branch checked out in the main working tree, or null on a detached head.</summary>
    public string? CurrentBranch { get; set; }

    /// <summary>True when the main working tree has any uncommitted change.</summary>
    public bool IsDirty { get; set; }

    /// <summary>When the DIRECTOR collected the snapshot.</summary>
    public DateTime CollectedAtUtc { get; set; }

    /// <summary>When the GATEWAY received it. Both times are kept: a Director whose clock is wrong, or whose
    /// pushes stopped, is then visible as itself rather than as a repository with nothing to report.</summary>
    public DateTime ReceivedAtUtc { get; set; }

    /// <summary>The serialized branch list (a JSON array of <c>RepoStateBranchDto</c>).</summary>
    public string BranchesJson { get; set; } = "[]";

    /// <summary>The serialized worktree list (a JSON array of <c>RepoStateWorktreeDto</c>).</summary>
    public string WorktreesJson { get; set; } = "[]";
}
