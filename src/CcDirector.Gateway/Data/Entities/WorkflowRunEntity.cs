using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One execution of a workflow definition, in the <c>workflow_runs</c> table - the governance outcome
/// spine (issue #1771). The run pins the exact published version that governed it at creation
/// (<see cref="WorkflowVersionId"/> + <see cref="WorkflowVersion"/> + <see cref="ContentHash"/>);
/// published versions referenced by runs are never deleted, so the answer to "what conduct governed
/// this run" survives supersede, archive, and reset.
///
/// Lifecycle <see cref="Status"/> is SEPARATE from <see cref="AcceptanceStatus"/> on purpose:
/// "completed" and "counts as delivered" are different questions, and the verified-yield metric
/// reads the second. Criteria results, proof links, and participants are bounded sub-documents
/// mapped as owned JSON columns, reusing the wire contract types (the cron pattern).
///
/// Today's missions become runs of the built-in "mission" workflow: the Gateway's mission-create
/// path opens a run beside the Mission record and references it (<see cref="MissionId"/>) - a
/// reference, not a merge, because standalone runs have no Mission.
/// </summary>
public sealed class WorkflowRunEntity : GatewayMintedKeyEntity
{
    /// <summary>The workflow definition this run executes. Indexed.</summary>
    public string WorkflowId { get; set; } = "";

    /// <summary>The exact published version row pinned at creation.</summary>
    public Guid WorkflowVersionId { get; set; }

    /// <summary>The pinned version number (denormalized for cheap display and CLI reads).</summary>
    public int WorkflowVersion { get; set; }

    /// <summary>The pinned version's canonical bundle hash.</summary>
    public string ContentHash { get; set; } = "";

    /// <summary>The run's display name (for a mission run, the mission name).</summary>
    public string Name { get; set; } = "";

    /// <summary>Lifecycle: created, active, awaiting-human, succeeded, failed, abandoned. Legal
    /// transitions are enforced by the store; a terminal status is final.</summary>
    public string Status { get; set; } = WorkflowRunStatus.Created;

    /// <summary>Acceptance: pending, accepted, rejected, waived - independent of lifecycle.</summary>
    public string AcceptanceStatus { get; set; } = WorkflowRunAcceptance.Pending;

    /// <summary>Who accepted/rejected/waived (a human identity or a delegated agent seat).</summary>
    public string? AcceptedBy { get; set; }

    public DateTime? AcceptedUtc { get; set; }

    /// <summary>A short closing statement of what the run produced.</summary>
    public string? Outcome { get; set; }

    /// <summary>Per-criterion standing, seeded pending from the pinned version's declared criteria.</summary>
    public List<WorkflowRunCriterionResultDto> CriteriaResults { get; set; } = new();

    /// <summary>Labelled evidence links (pull requests, reports, deployments).</summary>
    public List<WorkflowRunProofLinkDto> ProofLinks { get; set; } = new();

    /// <summary>Persisted run-to-session membership with join/leave history.</summary>
    public List<WorkflowRunParticipantDto> Participants { get; set; } = new();

    /// <summary>The Mission record anchoring this run, when it came from the mission path. Indexed;
    /// not a foreign key (independent lifecycle).</summary>
    public Guid? MissionId { get; set; }

    public Guid? ParentRunId { get; set; }

    /// <summary>The repository the run works in, when known.</summary>
    public string? RepoPath { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>Stamped on the first transition to active.</summary>
    public DateTime? StartedUtc { get; set; }

    /// <summary>Stamped on the transition to a terminal status.</summary>
    public DateTime? CompletedUtc { get; set; }
}
