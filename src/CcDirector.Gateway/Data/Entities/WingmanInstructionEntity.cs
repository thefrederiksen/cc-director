namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// The persisted form of the wingman instructions state (issue #537) in the EF data layer: ONE row per
/// tenant in the <c>wingman_instructions</c> table, holding the whole editable/versioned document exactly as
/// the legacy <c>wingman-instructions.json</c> held it.
///
/// The store is a single state DOCUMENT, not a keyed collection: an active-version pointer, the acknowledged
/// deployed-default snapshot, and the ordered list of saved versions. So it is modelled as one row carrying:
/// <list type="bullet">
/// <item><see cref="ActiveVersionId"/> - the nullable pointer to the active custom version (null = ride the
/// deployed default). This is the exact "active" resolution the legacy store used: the version it points to
/// when set and found, otherwise the deployed default. NOT "the latest version".</item>
/// <item><see cref="AckDefaultVersion"/> / <see cref="AckDefaultContent"/> - the acknowledged (based-on)
/// default snapshot, the left side of the "our changes" diff and the basis for the update-available banner.</item>
/// <item><see cref="Versions"/> - the saved versions, an OWNED collection serialized to a JSON column (the
/// cron/workflow "sub-doc -> JSON in a column" pattern), preserving each version's fields and the list order.</item>
/// </list>
/// A surrogate GUID <see cref="Id"/> is the key (the document has no external id); the store keeps exactly one
/// row per tenant. <c>tenant_id</c> + the global query filter are inherited from the base.
/// </summary>
public sealed class WingmanInstructionEntity : GatewayMintedKeyEntity
{
    /// <summary>The active custom version's id, or null to ride the deployed default. The active-version
    /// pointer, reproduced exactly.</summary>
    public string? ActiveVersionId { get; set; }

    /// <summary>The acknowledged deployed-default version stamp (the based-on default for the diff/banner).</summary>
    public string AckDefaultVersion { get; set; } = "";

    /// <summary>The acknowledged deployed-default content snapshot (the left side of the "our changes" diff).</summary>
    public string AckDefaultContent { get; set; } = "";

    /// <summary>The saved versions, in stored order, as an owned JSON collection.</summary>
    public List<WingmanInstructionVersionOwned> Versions { get; set; } = new();
}

/// <summary>
/// One saved wingman instruction version, owned by <see cref="WingmanInstructionEntity"/> and serialized into
/// its JSON column. Mirrors the legacy <c>InstructionVersion</c> field-for-field so the import is lossless and
/// the store maps back to its unchanged public record.
/// </summary>
public sealed class WingmanInstructionVersionOwned
{
    public string Id { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public string? Label { get; set; }
    public string Source { get; set; } = "user";
    public string Hash { get; set; } = "";
}
