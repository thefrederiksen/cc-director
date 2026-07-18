namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// The head record of a workflow in the <c>workflows</c> table: identity and lifecycle only, never
/// content. Content lives on immutable <see cref="WorkflowVersionEntity"/> rows so a workflow run can
/// pin the exact bundle that governed it (the governance outcome spine, issue #1771) and distribution
/// can diff by content hash.
///
/// The key is the workflow's public slug id ("mission", "standalone", ...), minted by the author and
/// validated by the store - never a database default. Built-in workflows (<see cref="IsBuiltIn"/>) are
/// seeded from embedded resources at startup, are editable and versioned like any workflow (owner
/// ruling, 2026-07-17), but can never be deleted; <see cref="ShippedContentHash"/> remembers what the
/// running binary ships so upgrades auto-publish newer shipped content ONLY while the user has not
/// customized the workflow (the injected-text ours/yours trade).
/// </summary>
public sealed class WorkflowEntity : TenantScopedEntity
{
    /// <summary>The workflow's slug id (primary key), e.g. "mission". Lowercase, validated in code.</summary>
    public string Id { get; set; } = "";

    /// <summary>True for the workflows the Gateway ships. Built-ins can never be deleted.</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Soft delete for user-defined workflows. Archived workflows leave the catalog but their
    /// versions remain - a run that pinned one must always be able to resolve it.</summary>
    public bool Archived { get; set; }

    /// <summary>
    /// The owner's switch (register redesign, owner ruling 2026-07-18): DevThrottle configures to
    /// what the USER wants, so any workflow - built-ins included - can be turned off. Off means:
    /// hidden from every agent's launch briefing, the default conduct read refused with a clear
    /// message, and no new runs or seats - but NOTHING deleted: versions, history, and past runs
    /// stay, pinned explicit-version reads keep resolving, and the switch is instant both ways
    /// fleet-wide. Distinct from <see cref="Archived"/>: off is a standing choice shown in the
    /// register; archived is removal from it.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The highest version number ever minted for this workflow (the draft counter).</summary>
    public int LatestVersion { get; set; }

    /// <summary>The currently published version number, or null when nothing is published yet (a
    /// draft-only workflow, invisible to the legacy catalog list).</summary>
    public int? PublishedVersion { get; set; }

    /// <summary>Built-ins only: the canonical content hash of what THIS binary ships for the workflow.
    /// The seeder compares the published hash against the previously recorded shipped hash to decide
    /// whether an upgrade may auto-publish (uncustomized) or must leave the user's edit alone.</summary>
    public string? ShippedContentHash { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
