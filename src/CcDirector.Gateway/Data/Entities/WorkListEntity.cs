namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// The persisted form of a named work list (<see cref="Contracts.WorkListDto"/>) in the EF data layer: one
/// row in the <c>worklists</c> table. Keyed by a surrogate code-generated GUID (never a database default),
/// so the human <see cref="Name"/> is free to be renamed or compared case-insensitively without being the
/// primary key.
///
/// The name is unique per tenant, case-insensitively - enforced by a .NET-computed normalized FOLD column
/// (<see cref="NameFold"/> = <c>Name.ToUpperInvariant()</c>) with a unique <c>(tenant_id, NameFold)</c> index,
/// and every lookup compares the fold. ToUpperInvariant reproduces <see cref="System.StringComparer.OrdinalIgnoreCase"/>
/// across the FULL Unicode range (a database collation like SQLite NOCASE folds only ASCII and would not
/// match), and it is provider-AGNOSTIC (identical on SQLite and Postgres - no NOCASE, no citext, no ILike).
/// This folded-shadow-column is the case-insensitive-key discipline the #340 nickname will reuse.
///
/// <see cref="Items"/> is an ORDERED child table (<see cref="WorkListItemEntity"/>, an explicit position
/// column) so reorder and targeted remove stay exact and queryable - the same child-table pattern the cron
/// run history uses.
/// </summary>
public sealed class WorkListEntity : TenantScopedEntity
{
    /// <summary>Surrogate primary key. A GUID generated in code (never a database default).</summary>
    public Guid Id { get; set; }

    /// <summary>The list's human name, in its original case (for display).</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The case-folded name (<c>Name.ToUpperInvariant()</c>), kept in sync by the store on every write. The
    /// unique <c>(tenant_id, NameFold)</c> index and every name lookup compare this, so uniqueness and
    /// matching are case-insensitive across the full Unicode range exactly like the store's old
    /// OrdinalIgnoreCase dictionary - provider-agnostically, with no database collation.
    /// </summary>
    public string NameFold { get; set; } = "";

    /// <summary>The single active draining consumer's claim token, or null when unclaimed.</summary>
    public string? Consumer { get; set; }

    /// <summary>The ordered item references (child rows).</summary>
    public List<WorkListItemEntity> Items { get; set; } = new();
}
