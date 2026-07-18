using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workflows;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Store tests for the workflow-run spine (Workflows mission, phase 4 - issue #1771). The claims
/// that matter: a run pins the exact published version at creation and keeps it as the catalog moves
/// on; criteria are seeded from the pinned version's declared outcome criteria and cannot be
/// invented on the run; lifecycle transitions are legal-moves-only with terminal FINAL; acceptance
/// is independent of lifecycle; participants carry join/leave history.
/// </summary>
public sealed class WorkflowRunStoreTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private (WorkflowStore Workflows, WorkflowRunStore Runs) NewStores()
    {
        var db = _h.Open();
        return (new WorkflowStore(db), new WorkflowRunStore(db));
    }

    private static WorkflowContentRequest PublishableWorkflow(string id = "qa-loop") => new()
    {
        Id = id,
        Name = "QA loop",
        Summary = "Verify ready items.",
        Steps = new List<WorkflowStepDto>
        {
            new() { Name = "Verify", Doer = "Reviewer", Done = "Issue passed QA." },
        },
        InstructionsMarkdown = "# QA loop",
        OutcomeCriteria = new List<WorkflowOutcomeCriterionDto>
        {
            new() { CriterionId = "issue-passed", Description = "The issue passed QA." },
            new() { CriterionId = "defect-filed", Description = "Failures got a written defect." },
        },
        AuthoredBy = "test",
    };

    [Fact]
    public void Create_pins_the_published_version_and_seeds_pending_criteria()
    {
        var (workflows, runs) = NewStores();
        workflows.CreateDraft(PublishableWorkflow());
        var published = workflows.Publish("qa-loop")!;

        var run = runs.Create("qa-loop", "Nightly QA");

        Assert.Equal("qa-loop", run.WorkflowId);
        Assert.Equal(1, run.WorkflowVersion);
        Assert.Equal(published.ContentHash, run.ContentHash);
        Assert.Equal("created", run.Status);
        Assert.Equal("pending", run.AcceptanceStatus);
        Assert.Equal(new[] { "issue-passed", "defect-filed" },
            run.CriteriaResults.Select(c => c.CriterionId).ToArray());
        Assert.All(run.CriteriaResults, c => Assert.Equal("pending", c.Status));
    }

    [Fact]
    public void A_run_keeps_its_pinned_version_when_the_catalog_moves_on()
    {
        var (workflows, runs) = NewStores();
        workflows.CreateDraft(PublishableWorkflow());
        workflows.Publish("qa-loop");
        var run = runs.Create("qa-loop", "Before the edit");

        var v2 = PublishableWorkflow();
        v2.InstructionsMarkdown = "# QA loop v2";
        workflows.UpdateDraft("qa-loop", v2, ifMatchHash: null);
        workflows.Publish("qa-loop");

        var reread = runs.Get(run.Id)!;
        Assert.Equal(1, reread.WorkflowVersion);
        Assert.Equal(run.ContentHash, reread.ContentHash);
        // A new run pins the new version.
        Assert.Equal(2, runs.Create("qa-loop", "After the edit").WorkflowVersion);
    }

    [Fact]
    public void Create_refuses_unknown_draft_only_and_archived_workflows()
    {
        var (workflows, runs) = NewStores();
        workflows.CreateDraft(PublishableWorkflow("draft-only"));

        Assert.Throws<WorkflowValidationException>(() => runs.Create("no-such", "x"));
        Assert.Throws<WorkflowValidationException>(() => runs.Create("draft-only", "x"));

        workflows.CreateDraft(PublishableWorkflow("gone"));
        workflows.Publish("gone");
        workflows.Archive("gone");
        Assert.Throws<WorkflowValidationException>(() => runs.Create("gone", "x"));
    }

    [Fact]
    public void Lifecycle_moves_are_legal_only_and_terminal_is_final()
    {
        var (_, runs) = NewStores();
        var run = runs.Create("mission", "Lifecycle proof");

        // created cannot jump straight to succeeded.
        Assert.Throws<WorkflowValidationException>(() =>
            runs.Patch(run.Id, new PatchWorkflowRunRequest { Status = "succeeded" }));

        var active = runs.Patch(run.Id, new PatchWorkflowRunRequest { Status = "active" })!;
        Assert.NotNull(active.StartedUtc);

        var waiting = runs.Patch(run.Id, new PatchWorkflowRunRequest { Status = "awaiting-human" })!;
        Assert.Equal("awaiting-human", waiting.Status);

        var done = runs.Patch(run.Id, new PatchWorkflowRunRequest { Status = "succeeded" })!;
        Assert.NotNull(done.CompletedUtc);

        Assert.Throws<WorkflowValidationException>(() =>
            runs.Patch(run.Id, new PatchWorkflowRunRequest { Status = "active" }));
        Assert.Throws<WorkflowValidationException>(() =>
            runs.Patch(run.Id, new PatchWorkflowRunRequest { Status = "not-a-status" }));
    }

    [Fact]
    public void Acceptance_is_independent_of_lifecycle()
    {
        var (_, runs) = NewStores();
        var run = runs.Create("mission", "Acceptance proof");
        runs.Patch(run.Id, new PatchWorkflowRunRequest { Status = "active" });

        // Accepted while still ACTIVE - lifecycle does not gate acceptance.
        var accepted = runs.Patch(run.Id, new PatchWorkflowRunRequest
        {
            AcceptanceStatus = "accepted",
            AcceptedBy = "human:owner",
        })!;
        Assert.Equal("active", accepted.Status);
        Assert.Equal("accepted", accepted.AcceptanceStatus);
        Assert.Equal("human:owner", accepted.AcceptedBy);
        Assert.NotNull(accepted.AcceptedUtc);

        // Back to pending clears the acceptance stamp.
        var pending = runs.Patch(run.Id, new PatchWorkflowRunRequest { AcceptanceStatus = "pending" })!;
        Assert.Null(pending.AcceptedBy);
        Assert.Null(pending.AcceptedUtc);
    }

    [Fact]
    public void Criteria_update_the_seeded_set_and_cannot_be_invented()
    {
        var (workflows, runs) = NewStores();
        workflows.CreateDraft(PublishableWorkflow());
        workflows.Publish("qa-loop");
        var run = runs.Create("qa-loop", "Criteria proof");

        var patched = runs.Patch(run.Id, new PatchWorkflowRunRequest
        {
            Criteria = new List<WorkflowRunCriterionResultDto>
            {
                new()
                {
                    CriterionId = "issue-passed",
                    Status = "met",
                    ProofUrl = "https://github.com/x/pull/1",
                    Evaluator = "session:qa",
                },
            },
        })!;

        var criterion = patched.CriteriaResults.Single(c => c.CriterionId == "issue-passed");
        Assert.Equal("met", criterion.Status);
        Assert.Equal("https://github.com/x/pull/1", criterion.ProofUrl);
        Assert.NotNull(criterion.EvaluatedUtc);
        Assert.Equal("pending", patched.CriteriaResults.Single(c => c.CriterionId == "defect-filed").Status);

        Assert.Throws<WorkflowValidationException>(() => runs.Patch(run.Id, new PatchWorkflowRunRequest
        {
            Criteria = new List<WorkflowRunCriterionResultDto>
            {
                new() { CriterionId = "invented", Status = "met" },
            },
        }));
    }

    [Fact]
    public void Participants_join_once_and_leave_with_history()
    {
        var (_, runs) = NewStores();
        var run = runs.Create("mission", "Participants proof");

        var join = new WorkflowRunParticipantDto
        {
            SessionId = "abc123",
            AgentKind = "ClaudeCode",
            Role = "Architect",
            Machine = "SOREN_NORTH",
        };
        runs.Patch(run.Id, new PatchWorkflowRunRequest
        {
            AddParticipants = new List<WorkflowRunParticipantDto> { join, join },
        });
        var afterJoin = runs.Get(run.Id)!;
        var participant = Assert.Single(afterJoin.Participants); // duplicate join is a no-op
        Assert.Equal("Architect", participant.Role);
        Assert.Null(participant.LeftUtc);

        runs.Patch(run.Id, new PatchWorkflowRunRequest
        {
            LeaveSessionIds = new List<string> { "abc123" },
        });
        Assert.NotNull(runs.Get(run.Id)!.Participants.Single().LeftUtc);
    }

    [Fact]
    public void List_filters_by_workflow_status_and_mission()
    {
        var (_, runs) = NewStores();
        var missionId = Guid.NewGuid();
        var a = runs.Create("mission", "A", missionId: missionId);
        runs.Create("standalone", "B");
        runs.Patch(a.Id, new PatchWorkflowRunRequest { Status = "active" });

        Assert.Single(runs.List(workflowId: "mission"));
        Assert.Single(runs.List(status: "active"));
        Assert.Single(runs.List(missionId: missionId));
        Assert.Equal(2, runs.List().Count);
    }
}
