namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One supporting file belonging to a skill version, in the <c>skill_files</c> table. Files are part
/// of the version's immutable bundle (copied forward when a new draft is cut) and are DISTRIBUTED
/// CONTENT ONLY: the Gateway and the Director never execute them. An agent that uses the skill
/// materializes them into its own session and runs them under its own permission model.
///
/// Files are why a skill is not a single document: the skills this fleet already has include one that
/// is seven reference documents and one that ships a Python script, and neither can be represented as
/// a lone markdown body. They are fetched WITH the skill that needs them, at the moment of use -
/// never ahead of time, and never for a skill the session did not reach for.
///
/// File names are validated by the store (no path separators, a short allow-listed extension set,
/// size-capped) so a bundle can always be materialized to disk safely.
/// </summary>
public sealed class SkillFileEntity : GatewayMintedKeyEntity
{
    /// <summary>The <see cref="SkillVersionEntity"/> this file belongs to. Indexed; not a foreign key
    /// (independent lifecycle, matching the workflow file store).</summary>
    public Guid VersionId { get; set; }

    /// <summary>Validated bare file name, e.g. "checklist.md". Never a path.</summary>
    public string FileName { get; set; } = "";

    /// <summary>The file's full text content.</summary>
    public string Content { get; set; } = "";

    /// <summary>SHA-256 of <see cref="Content"/>, used in the version's canonical bundle hash.</summary>
    public string ContentHash { get; set; } = "";
}
