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
/// File paths are validated by the store (relative, no traversal, no reserved device name, depth- and
/// size-capped) so a bundle can always be materialized to disk safely.
/// </summary>
public sealed class SkillFileEntity : GatewayMintedKeyEntity
{
    /// <summary>The <see cref="SkillVersionEntity"/> this file belongs to. Indexed; not a foreign key
    /// (independent lifecycle, matching the workflow file store).</summary>
    public Guid VersionId { get; set; }

    /// <summary>The file's RELATIVE PATH inside the skill's directory, forward slashes always, e.g.
    /// "references/tracing.md". A skill is a directory in the Agent Skills standard, so a bare name is
    /// simply the case where the path has one segment.</summary>
    public string FileName { get; set; } = "";

    /// <summary>The file's content: the text itself when <see cref="Encoding"/> is "utf8", the base64
    /// of its bytes when "base64".
    ///
    /// WHY BASE64 IN A TEXT COLUMN rather than a binary column: this is an additive change on both
    /// database providers, where a column type change would have to rewrite every existing row on the
    /// hosted Gateway and on every self-hosted one. The encoding discriminator makes the intent
    /// explicit rather than implied, and the size caps are applied to the DECODED bytes so a limit
    /// still means what it says.</summary>
    public string Content { get; set; } = "";

    /// <summary>"utf8" or "base64". Older rows written before binary support carry text and are read
    /// as "utf8", which is exactly what they always were.</summary>
    public string Encoding { get; set; } = "utf8";

    /// <summary>Whether the file gets the executable bit when materialized on Linux or macOS. Ignored
    /// on Windows. Part of the file's identity, so it is covered by the version's bundle hash.</summary>
    public bool Executable { get; set; }

    /// <summary>SHA-256 of the file's DECODED bytes, used in the version's canonical bundle hash.
    /// Hashing the bytes rather than the carrier string means the same file has the same hash whether
    /// it travelled as text or as base64.</summary>
    public string ContentHash { get; set; } = "";
}
