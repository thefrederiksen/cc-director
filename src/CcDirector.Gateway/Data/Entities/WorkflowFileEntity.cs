namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One helper file belonging to a workflow version, in the <c>workflow_files</c> table. Files are
/// part of the version's immutable bundle (copied forward when a new draft is cut) and are DISTRIBUTED
/// CONTENT ONLY: the Gateway and the Director never execute them. An agent that uses the workflow runs
/// them in its own session under its own permission model, exactly like a skill's scripts.
///
/// File names are validated by the store (no path separators, a short allow-listed extension set,
/// size-capped) so a bundle can always be materialized to disk safely.
/// </summary>
public sealed class WorkflowFileEntity : TenantScopedEntity
{
    /// <summary>Primary key, minted in code - never a database default.</summary>
    public Guid Id { get; set; }

    /// <summary>The <see cref="WorkflowVersionEntity"/> this file belongs to. Indexed; not a foreign
    /// key (independent lifecycle, like cron runs to cron jobs).</summary>
    public Guid VersionId { get; set; }

    /// <summary>Validated bare file name, e.g. "helpers.py". Never a path.</summary>
    public string FileName { get; set; } = "";

    /// <summary>The file's full text content.</summary>
    public string Content { get; set; } = "";

    /// <summary>SHA-256 of <see cref="Content"/>, used in the version's canonical bundle hash.</summary>
    public string ContentHash { get; set; } = "";
}
