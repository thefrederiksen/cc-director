namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// A repository's surrogate id to its FIRST-SEEN display spelling (<c>repo_identity</c>).
///
/// Carried forward UNCHANGED from SQLite schema version 5, where the full reasoning lives. The part that
/// governs every one of the four identity tables and MUST NOT be tidied up:
///
/// <see cref="RepoDisplay"/> is, from the database's point of view, WRITE-ONLY. It is read once at startup to
/// rebuild the in-memory identity map, and the database is never asked to compare or group by it. That is the
/// whole point - the only component that decides whether two repository strings are equal is the same
/// <c>StringComparer.OrdinalIgnoreCase</c> that decides it today.
///
/// So the database is NEVER asked whether two DIFFERENT spellings are one identity. That question is
/// case-INSENSITIVE, a database can only answer it under some collation, and there is no repair by storing a
/// folded string either: that needs a normalizer, and none exists
/// (<c>StringComparer.OrdinalIgnoreCase</c> is a COMPARER, not a normalizer; <c>ToLowerInvariant</c> is a
/// different function and can even change a string's length, at U+0130). The in-memory map is what
/// guarantees one id per distinct-ignoring-case spelling, and that is unchanged.
///
/// WHAT THE DATABASE IS ASKED, since schema version 6: that no two rows carry the IDENTICAL (tenant,
/// spelling) pair - <c>ux_repo_identity_tenant_display</c>. That is a different and much weaker question: a
/// byte-for-byte duplicate is a duplicate under every comparer, the case-insensitive one included, so no
/// collation is being trusted to decide anything. It exists so a mint can be an upsert that RETURNS the id
/// that won, rather than an insert that assumes it minted one - without a conflict target, two hosted
/// containers minting the same spelling for one tenant each get their own id and that tenant's turns split
/// silently in two. Spellings that differ only by case still mint separate ids, exactly as before.
///
/// Since version 4 the repository dimension is the session's "owner/repo" repo NAME, not its local path -
/// the path became <see cref="CheckoutIdentityEntity"/>.
/// </summary>
public sealed class RepoIdentityEntity
{
    /// <summary>The surrogate repository id (<c>repo_id</c>), generated on add.</summary>
    public long RepoId { get; set; }

    /// <summary>The first-seen display spelling (<c>repo_display</c>). Write-only to the database; never
    /// compared or grouped by it. No unique constraint - see the type remarks.</summary>
    public string RepoDisplay { get; set; } = "";

    /// <summary>The owning tenant (<c>tenant</c>). A plain column, not part of any key. This is what makes
    /// <see cref="RepoSessionEntity"/> tenant-partitioned INDIRECTLY: the surrogate is minted per tenant.
    /// </summary>
    public string Tenant { get; set; } = "";
}
