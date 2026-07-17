namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// The persisted form of a named work list (<see cref="Contracts.WorkListDto"/>) in the EF data layer: one
/// row in the <c>worklists</c> table. Keyed by a surrogate code-generated GUID (never a database default),
/// so the human <see cref="Name"/> is free to be renamed or compared case-insensitively without being the
/// primary key.
///
/// The name is unique per tenant, case-insensitively - enforced provider-aware (a SQLite NOCASE column
/// collation plus a unique index locally; a Postgres citext or lower() unique index later), NOT with
/// EF.Functions.ILike (which does not translate on SQLite). This is the case-insensitive-key discipline the
/// #340 nickname will reuse.
///
/// <see cref="Items"/> is an ORDERED child table (<see cref="WorkListItemEntity"/>, an explicit position
/// column) so reorder and targeted remove stay exact and queryable - the same child-table pattern the cron
/// run history uses.
/// </summary>
public sealed class WorkListEntity : TenantScopedEntity
{
    /// <summary>Surrogate primary key. A GUID generated in code (never a database default).</summary>
    public Guid Id { get; set; }

    /// <summary>The list's human name. Unique per tenant, case-insensitive (provider-aware collation).</summary>
    public string Name { get; set; } = "";

    /// <summary>The single active draining consumer's claim token, or null when unclaimed.</summary>
    public string? Consumer { get; set; }

    /// <summary>The ordered item references (child rows).</summary>
    public List<WorkListItemEntity> Items { get; set; } = new();
}
