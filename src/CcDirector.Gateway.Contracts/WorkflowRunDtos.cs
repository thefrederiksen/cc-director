namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One criterion's standing on a workflow run. Seeded from the pinned version's declared outcome
/// criteria (all "pending") when the run is created, so a run always answers "what did this workflow
/// promise, and where does each promise stand" (the governance outcome spine, issue #1771).
/// </summary>
public sealed class WorkflowRunCriterionResultDto
{
    public string CriterionId { get; set; } = "";

    /// <summary>"pending", "met", "not-met", or "waived".</summary>
    public string Status { get; set; } = WorkflowRunCriterionStatus.Pending;

    /// <summary>Evidence for the standing (a pull request, a report, an artifact). A link, never prose.</summary>
    public string? ProofUrl { get; set; }

    public string? Note { get; set; }

    /// <summary>Who evaluated the criterion (a session id, an agent name, or "human:&lt;user&gt;").</summary>
    public string? Evaluator { get; set; }

    public DateTime? EvaluatedUtc { get; set; }
}

/// <summary>The legal criterion statuses.</summary>
public static class WorkflowRunCriterionStatus
{
    public const string Pending = "pending";
    public const string Met = "met";
    public const string NotMet = "not-met";
    public const string Waived = "waived";
}

/// <summary>A labelled evidence link on a run (pull requests, quality reports, deployments).</summary>
public sealed class WorkflowRunProofLinkDto
{
    public string Label { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>
/// One session's membership in a run - the persisted run-to-session record #1771 asks for directly
/// (standalone runs have no Mission to derive membership from). Leaving stamps <see cref="LeftUtc"/>;
/// the row is never removed, because replacement history is part of the record.
/// </summary>
public sealed class WorkflowRunParticipantDto
{
    /// <summary>The CANONICAL fleet session id - the Director-minted session GUID every other
    /// per-session record (token/spend statistics, transcripts, the roster) also keys on. This is
    /// governance's join from a run to the effort it consumed (issue #1771, spine item 3); never put
    /// any other identifier here.</summary>
    public string SessionId { get; set; } = "";
    public string AgentKind { get; set; } = "";
    public string Role { get; set; } = "";
    public string Machine { get; set; } = "";
    public DateTime JoinedUtc { get; set; }
    public DateTime? LeftUtc { get; set; }
}

/// <summary>The legal run lifecycle statuses. Lifecycle is deliberately SEPARATE from acceptance:
/// "the run completed" and "its outcome counts as delivered" are different questions.</summary>
public static class WorkflowRunStatus
{
    public const string Created = "created";
    public const string Active = "active";
    public const string AwaitingHuman = "awaiting-human";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Abandoned = "abandoned";

    public static readonly string[] Terminal = { Succeeded, Failed, Abandoned };
}

/// <summary>The legal acceptance statuses.</summary>
public static class WorkflowRunAcceptance
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Waived = "waived";
}

/// <summary>
/// One execution of a workflow definition. The run pins the exact published version (id, number,
/// canonical content hash) that governed it at creation - published versions referenced by runs are
/// never deleted, so a run can always answer what conduct it ran under.
/// </summary>
public sealed class WorkflowRunDto
{
    public Guid Id { get; set; }
    public string WorkflowId { get; set; } = "";
    public Guid WorkflowVersionId { get; set; }
    public int WorkflowVersion { get; set; }
    public string ContentHash { get; set; } = "";
    public string Name { get; set; } = "";

    public string Status { get; set; } = WorkflowRunStatus.Created;

    public string AcceptanceStatus { get; set; } = WorkflowRunAcceptance.Pending;
    public string? AcceptedBy { get; set; }
    public DateTime? AcceptedUtc { get; set; }

    /// <summary>A short closing statement of what the run produced (distinct from acceptance).</summary>
    public string? Outcome { get; set; }

    public List<WorkflowRunCriterionResultDto> CriteriaResults { get; set; } = new();
    public List<WorkflowRunProofLinkDto> ProofLinks { get; set; } = new();
    public List<WorkflowRunParticipantDto> Participants { get; set; } = new();

    /// <summary>The Mission record this run is anchored to, when the run came from the mission path.
    /// A reference, not a merge - standalone runs have no Mission.</summary>
    public Guid? MissionId { get; set; }

    public Guid? ParentRunId { get; set; }
    public string? RepoPath { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}

/// <summary>Body of POST /gateway/workflow-runs.</summary>
public sealed class NewWorkflowRunRequest
{
    public string? WorkflowId { get; set; }
    public string? Name { get; set; }
    public Guid? MissionId { get; set; }
    public Guid? ParentRunId { get; set; }
    public string? RepoPath { get; set; }
}

/// <summary>
/// Body of PATCH /gateway/workflow-runs/{id}. Every field is optional; present fields apply. Criteria
/// entries update the seeded criterion with the same id (an unknown id is rejected). Participants in
/// <see cref="AddParticipants"/> join; session ids in <see cref="LeaveSessionIds"/> get their
/// <see cref="WorkflowRunParticipantDto.LeftUtc"/> stamped.
/// </summary>
public sealed class PatchWorkflowRunRequest
{
    public string? Status { get; set; }
    public string? Outcome { get; set; }
    public string? AcceptanceStatus { get; set; }
    public string? AcceptedBy { get; set; }
    public List<WorkflowRunCriterionResultDto>? Criteria { get; set; }
    public List<WorkflowRunProofLinkDto>? ProofLinks { get; set; }
    public List<WorkflowRunParticipantDto>? AddParticipants { get; set; }
    public List<string>? LeaveSessionIds { get; set; }
}
