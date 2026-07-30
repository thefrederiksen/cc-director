namespace CcDirector.Gateway.Stats.Data.Entities;

/// <summary>
/// A model's surrogate id to its FIRST-SEEN display spelling (<c>model_identity</c>).
///
/// Carried forward UNCHANGED from SQLite schema version 5. The display column is write-only to the database
/// and carries NO unique constraint on either provider; the reasoning is written out once, in full, on
/// <see cref="RepoIdentityEntity"/>, and is identical here. Model names need it as much as repositories do:
/// the reported model is free text with unbounded cardinality and casing by convention only.
///
/// There is deliberately no "the Director did not say" identity row here, unlike the repository and agent
/// dimensions which use an empty display spelling for it. An empty model spelling would appear as a model,
/// be ranked among models, and read as a model named nothing - so absence is stored as the absence of a
/// value (a null <see cref="StatDeltaEntity.ModelId"/>), not as a value.
/// </summary>
public sealed class ModelIdentityEntity
{
    /// <summary>The surrogate model id (<c>model_id</c>), generated on add.</summary>
    public long ModelId { get; set; }

    /// <summary>The first-seen display spelling (<c>model_display</c>). Write-only to the database. No unique
    /// constraint - see <see cref="RepoIdentityEntity"/>.</summary>
    public string ModelDisplay { get; set; } = "";

    /// <summary>The owning tenant (<c>tenant</c>). A plain column, not part of any key.</summary>
    public string Tenant { get; set; } = "";
}
