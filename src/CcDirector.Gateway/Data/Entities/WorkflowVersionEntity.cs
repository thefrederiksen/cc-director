using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One version of a workflow's content in the <c>workflow_versions</c> table. A version is a complete
/// snapshot - metadata, steps, outcome criteria, and the instruction markdown - so nothing about a
/// version can drift after publication: a published or superseded row is IMMUTABLE, and only the
/// single draft row of a workflow may change. Runs pin a version row (issue #1771), which is why
/// published content is frozen rather than audited.
///
/// The structured <see cref="Steps"/> are a display/reporting summary; the
/// <see cref="InstructionsMarkdown"/> is the authoritative conduct. Both are versioned together.
/// Steps and criteria reuse the wire contract types as EF owned types serialized to JSON columns
/// (the cron store's "bulky sub-doc -> JSON in a column" pattern).
/// </summary>
public sealed class WorkflowVersionEntity : GatewayMintedKeyEntity
{
    /// <summary>The workflow this version belongs to. (WorkflowId, Version) is unique.</summary>
    public string WorkflowId { get; set; } = "";

    /// <summary>1-based version number within the workflow.</summary>
    public int Version { get; set; }

    /// <summary>"draft", "published", or "superseded". At most ONE draft and ONE published row exist
    /// per workflow; publish flips draft to published and published to superseded in one transaction.</summary>
    public string Status { get; set; } = WorkflowVersionStatus.Draft;

    // ---- content snapshot -------------------------------------------------------------------------

    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string WhenToUse { get; set; } = "";
    public string HumanCheckpoint { get; set; } = "";

    /// <summary>The structured step/seat summary. Owned type serialized to a JSON column.</summary>
    public List<WorkflowStepDto> Steps { get; set; } = new();

    /// <summary>The authoritative conduct text (markdown). Capped by the store at 200 KB.</summary>
    public string InstructionsMarkdown { get; set; } = "";

    /// <summary>The author-declared outcome criteria runs are judged against (issue #1771). Owned type
    /// serialized to a JSON column.</summary>
    public List<WorkflowOutcomeCriterionDto> OutcomeCriteria { get; set; } = new();

    // ---- provenance -------------------------------------------------------------------------------

    /// <summary>SHA-256 over the canonical complete bundle (metadata + steps + criteria + instructions
    /// + ordered file hashes). Doubles as the optimistic-concurrency token for draft edits.</summary>
    public string ContentHash { get; set; } = "";

    /// <summary>Who authored this version: a session id, an agent name, or "human:&lt;user&gt;".</summary>
    public string AuthoredBy { get; set; } = "";

    /// <summary>Optional one-line note describing what changed in this version.</summary>
    public string? ChangeNote { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? PublishedUtc { get; set; }
}

/// <summary>The legal <see cref="WorkflowVersionEntity.Status"/> values.</summary>
public static class WorkflowVersionStatus
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Superseded = "superseded";
}
