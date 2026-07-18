namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One step of a workflow on the wire: a named piece of work, who does it, who reviews it, and what
/// finishing it means. Reviewer is null when the step has no separate review seat - which is itself a
/// statement the workflow is making, not an omission. The field names and shape are FROZEN as the
/// legacy catalog contract (issue #1617); the Cockpit's workflowsClient.ts types mirror them.
/// </summary>
public sealed class WorkflowStepDto
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Doer { get; set; } = "";
    public string? Reviewer { get; set; }
    public string Done { get; set; } = "";
}

/// <summary>
/// One declared outcome criterion of a workflow: what "done and accepted" means for a run of it. The
/// workflow AUTHOR declares these; a run seeds its per-criterion results from them (the governance
/// outcome spine, issue #1771). Structured on purpose - governance needs per-criterion evaluation -
/// while the conduct itself stays in the instruction markdown.
/// </summary>
public sealed class WorkflowOutcomeCriterionDto
{
    /// <summary>Stable slug identifying the criterion within its workflow (e.g. "merged-pr").</summary>
    public string CriterionId { get; set; } = "";

    /// <summary>What must be true for a run to count as delivered on this criterion.</summary>
    public string Description { get; set; } = "";

    /// <summary>Optional hint about what evidence proves the criterion (e.g. "the merged pull request URL").</summary>
    public string? ProofHint { get; set; }
}

/// <summary>
/// A workflow on the wire: the legacy catalog fields (id, name, summary, whenToUse, humanCheckpoint,
/// steps - frozen since issue #1617, the Cockpit reads them) plus the ADDITIVE fields the persisted
/// store carries (version, isBuiltIn, updatedUtc, hasDraft, contentHash). Existing clients ignore the
/// additions; nothing may rename or remove a legacy field.
/// </summary>
public sealed class WorkflowDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string WhenToUse { get; set; } = "";
    public string HumanCheckpoint { get; set; } = "";
    public List<WorkflowStepDto> Steps { get; set; } = new();

    /// <summary>The published version number this projection reflects.</summary>
    public int Version { get; set; }

    /// <summary>True for the workflows the Gateway ships (mission, standalone, ...). Built-ins are
    /// editable and versioned like any workflow but can never be deleted.</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>When the workflow head last changed (UTC).</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>True when an unpublished draft version exists beside the published one.</summary>
    public bool HasDraft { get; set; }

    /// <summary>The canonical content hash of the published version (the exact bundle a run pins).</summary>
    public string ContentHash { get; set; } = "";
}
