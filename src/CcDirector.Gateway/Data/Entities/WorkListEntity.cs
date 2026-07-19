namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// The persisted form of a named work list (<see cref="Contracts.WorkListDto"/>) in the EF data layer: one
/// row in the <c>worklists</c> table. Keyed by a surrogate code-generated GUID primary key (never a database
/// default), so the human <see cref="Name"/> is not the key.
///
/// Name uniqueness is case-insensitive, but it is enforced in CODE by the store
/// (<see cref="System.StringComparer.OrdinalIgnoreCase"/>) under its write lock - there is NO database-level
/// name-unique constraint. The legacy store was a <c>Dictionary(OrdinalIgnoreCase)</c> with no database at
/// all, and code-side OrdinalIgnoreCase reproduces that EXACTLY across the full Unicode range. No public
/// string transform matches OrdinalIgnoreCase exactly - both whole-string and per-character
/// <c>ToUpperInvariant</c> over-merge U+017F (LATIN SMALL LETTER LONG S) onto 'S', which OrdinalIgnoreCase
/// keeps distinct - so a stored fold column plus a unique index could NOT preserve the legacy behaviour and
/// would risk bricking an import on a unique-index violation. Code-side comparison has neither problem, and
/// the single-writer lock gives the same uniqueness guarantee the old Dictionary did.
///
/// (The #340 case-insensitive nickname is a GREENFIELD feature with no legacy behaviour to preserve; it will
/// make its own case-insensitivity decision - a provider-level normalized or collation index is fine there -
/// and does NOT reuse this exact code-side approach.)
///
/// <see cref="Items"/> is an ORDERED child table (<see cref="WorkListItemEntity"/>, an explicit position
/// column) so reorder and targeted remove stay exact and queryable - the same child-table pattern the cron
/// run history uses.
/// </summary>
public sealed class WorkListEntity : GatewayMintedKeyEntity
{
    /// <summary>The list's human name, in its original case.</summary>
    public string Name { get; set; } = "";

    /// <summary>The single active draining consumer's claim token, or null when unclaimed.</summary>
    public string? Consumer { get; set; }

    /// <summary>The ordered item references (child rows).</summary>
    public List<WorkListItemEntity> Items { get; set; } = new();
}
