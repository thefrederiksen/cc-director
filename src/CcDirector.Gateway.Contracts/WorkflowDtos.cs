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

    /// <summary>The owner's switch (register redesign): false = OFF - hidden from agents' launch
    /// briefings, default conduct reads refused, no new runs or seats; nothing deleted. The catalog
    /// still LISTS off workflows so the register can show and flip them. Defaults true so a reader
    /// of an older Gateway that omits the field treats everything as in force. On a built-in this is
    /// the CALLING TENANT's effective state (the tenant's own choice folded over the library value).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The Gateway's verdict on whether this workflow's CONTENT can be changed by the caller
    /// (Shared Workflow Library phase 3): false for the built-in DevThrottle workflows (read-only,
    /// maintained by DevThrottle, customize by cloning), true for tenant-owned workflows. Clients
    /// render this verbatim (rule 7 - the client is dumb) and never derive editability themselves.
    /// Defaults false: when in doubt, offer no edit affordance.</summary>
    public bool Editable { get; set; }
}

/// <summary>A helper file carried by a workflow version, with full content (authoring payloads).</summary>
public sealed class WorkflowFileDto
{
    public string FileName { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>A helper file in a version-detail response. Carries the full content: the detail route is
/// the authoring read (the CLI pulls a complete directory from it, drafts included - the raw
/// <c>files/{fileName}</c> route serves only pinned, non-draft versions).</summary>
public sealed class WorkflowFileInfoDto
{
    public string FileName { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>One row of a workflow's version history (no content bodies).</summary>
public sealed class WorkflowVersionInfoDto
{
    public int Version { get; set; }
    public string Status { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string AuthoredBy { get; set; } = "";
    public string? ChangeNote { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? PublishedUtc { get; set; }
}

/// <summary>The complete content snapshot of one workflow version (authoring reads).</summary>
public sealed class WorkflowVersionDetailDto
{
    public string WorkflowId { get; set; } = "";
    public int Version { get; set; }
    public string Status { get; set; } = "";
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public string WhenToUse { get; set; } = "";
    public string HumanCheckpoint { get; set; } = "";
    public List<WorkflowStepDto> Steps { get; set; } = new();
    public string InstructionsMarkdown { get; set; } = "";
    public List<WorkflowOutcomeCriterionDto> OutcomeCriteria { get; set; } = new();
    public List<WorkflowFileInfoDto> Files { get; set; } = new();
    public string ContentHash { get; set; } = "";
    public string AuthoredBy { get; set; } = "";
    public string? ChangeNote { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? PublishedUtc { get; set; }
}

/// <summary>
/// Body of <c>POST /gateway/workflows</c> (create a workflow as a draft) and
/// <c>PUT /gateway/workflows/{id}/draft</c> (replace the draft's content wholesale). The draft is a
/// FULL REPLACEMENT on every write - the CLI round-trips a complete directory, so there is no partial
/// patch to reason about. Fields a minimal creation (the Cockpit's add dialog) does not supply may be
/// empty on a DRAFT; publishing enforces the strict rules (instructions present, at least one step
/// with a doer and a done).
/// </summary>
public sealed class WorkflowContentRequest
{
    /// <summary>Required on POST (the new workflow's slug id); ignored on PUT (the route id wins).</summary>
    public string? Id { get; set; }

    public string? Name { get; set; }
    public string? Summary { get; set; }
    public string? WhenToUse { get; set; }
    public string? HumanCheckpoint { get; set; }
    public List<WorkflowStepDto>? Steps { get; set; }
    public string? InstructionsMarkdown { get; set; }
    public List<WorkflowOutcomeCriterionDto>? OutcomeCriteria { get; set; }
    public List<WorkflowFileDto>? Files { get; set; }

    /// <summary>Who is authoring: a session id, an agent name, or "human:&lt;user&gt;".</summary>
    public string? AuthoredBy { get; set; }

    public string? ChangeNote { get; set; }
}
