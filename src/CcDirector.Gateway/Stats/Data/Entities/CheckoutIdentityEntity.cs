namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// A local checkout's surrogate id to its FIRST-SEEN display spelling (<c>checkout_identity</c>) - the
/// working directory a turn was actually driven in.
///
/// Carried forward UNCHANGED from SQLite schema version 5, where it arrived beside the version 4 meaning
/// change to the repository dimension: <see cref="RepoIdentityEntity"/> became the "owner/repo" repo NAME so
/// a repository's worktrees and per-machine clones collapse into one row, and the local path was not thrown
/// away in the process - it became this dimension. Grouping and ranking are by repository; the checkout is
/// retained detail, read back as the set of checkouts that rolled into a repository.
///
/// The display column is write-only to the database and carries NO unique constraint on either provider; the
/// reasoning is written out once, in full, on <see cref="RepoIdentityEntity"/>, and is identical here.
/// </summary>
public sealed class CheckoutIdentityEntity
{
    /// <summary>The surrogate checkout id (<c>checkout_id</c>), generated on add.</summary>
    public long CheckoutId { get; set; }

    /// <summary>The first-seen display spelling (<c>checkout_display</c>), a local path. Write-only to the
    /// database. No unique constraint - see <see cref="RepoIdentityEntity"/>.</summary>
    public string CheckoutDisplay { get; set; } = "";

    /// <summary>The owning tenant (<c>tenant</c>). A plain column, not part of any key.</summary>
    public string Tenant { get; set; } = "";
}
