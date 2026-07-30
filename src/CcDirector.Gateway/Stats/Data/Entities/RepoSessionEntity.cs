namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// Membership in the all-time set of sessions seen against a repository (<c>repo_session</c>).
///
/// Carried forward UNCHANGED from SQLite schema version 5, including the composite primary key
/// (<c>repo_id</c>, <c>session_id</c>) and - the part a reader will want to "fix" - the fact that it has
/// **NO TENANT COLUMN**.
///
/// It is not un-partitioned in effect. <see cref="RepoIdentityEntity.RepoId"/> is a surrogate minted PER
/// TENANT (the identity table carries the tenant), so (<c>repo_id</c>, <c>session_id</c>) is partitioned
/// INDIRECTLY through the surrogate. That is a real invariant and it holds - but it holds by construction
/// elsewhere rather than by a column here, which is exactly why it is written down rather than left implied,
/// and why the contract suite asserts the indirect partitioning explicitly.
///
/// Adding a tenant column here is a BEHAVIOUR CHANGE and is deliberately outside this port's scope. The shape
/// is carried forward exactly.
///
/// DELIBERATELY NEVER PRUNED, like every membership set. Writes are insert-if-absent - ON CONFLICT DO
/// NOTHING, never a read-then-insert.
/// </summary>
public sealed class RepoSessionEntity
{
    /// <summary>The surrogate repository id (<c>repo_id</c>). Part of the primary key, and the thing that
    /// carries the tenant indirectly.</summary>
    public long RepoId { get; set; }

    /// <summary>The session (<c>session_id</c>). Part of the primary key.</summary>
    public string SessionId { get; set; } = "";
}
