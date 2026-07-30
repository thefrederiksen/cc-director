namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// An agent's surrogate id to its FIRST-SEEN display spelling (<c>agent_identity</c>).
///
/// Carried forward UNCHANGED from SQLite schema version 5. The display column is write-only to the database
/// and carries NO unique constraint on either provider; the reasoning is written out once, in full, on
/// <see cref="RepoIdentityEntity"/>, and is identical here.
/// </summary>
public sealed class AgentIdentityEntity
{
    /// <summary>The surrogate agent id (<c>agent_id</c>), generated on add.</summary>
    public long AgentId { get; set; }

    /// <summary>The first-seen display spelling (<c>agent_display</c>). Write-only to the database. No unique
    /// constraint - see <see cref="RepoIdentityEntity"/>.</summary>
    public string AgentDisplay { get; set; } = "";

    /// <summary>The owning tenant (<c>tenant</c>). A plain column, not part of any key. This is what makes
    /// <see cref="AgentSessionEntity"/> tenant-partitioned INDIRECTLY: the surrogate is minted per tenant.
    /// </summary>
    public string Tenant { get; set; } = "";
}
