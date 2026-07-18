using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workflows;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Store-level tests for workflow authoring (Workflows mission, phase 2): create-as-draft, the
/// full-replacement draft write with If-Match concurrency, the strict publish gate, versioned reads
/// (pinned history must resolve forever), reset-to-shipped on built-ins, and archive semantics.
/// </summary>
public sealed class WorkflowAuthoringTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private WorkflowStore NewStore() => new(_h.Open());

    private static WorkflowContentRequest Minimal(string id = "release-train") => new()
    {
        Id = id,
        Name = "Release train",
        Summary = "Cut, verify, and announce a release.",
        AuthoredBy = "test-session",
    };

    private static WorkflowContentRequest Complete(string id = "release-train") => new()
    {
        Id = id,
        Name = "Release train",
        Summary = "Cut, verify, and announce a release.",
        WhenToUse = "When a release is due.",
        HumanCheckpoint = "Once, before the announcement goes out.",
        Steps = new List<WorkflowStepDto>
        {
            new() { Name = "Cut", Description = "Cut the release.", Doer = "Worker", Reviewer = null, Done = "Tag pushed." },
        },
        InstructionsMarkdown = "# Release train\n\nCut the tag, verify, announce.",
        OutcomeCriteria = new List<WorkflowOutcomeCriterionDto>
        {
            new() { CriterionId = "announced", Description = "The release notes went out.", ProofHint = "The announcement URL." },
        },
        Files = new List<WorkflowFileDto>
        {
            new() { FileName = "verify.py", Content = "print('verify')" },
        },
        AuthoredBy = "test-session",
    };

    // ---- create ------------------------------------------------------------------------------------

    [Fact]
    public void Create_makes_a_draft_invisible_to_the_catalog_until_published()
    {
        var store = NewStore();

        var draft = store.CreateDraft(Minimal());

        Assert.Equal("release-train", draft.WorkflowId);
        Assert.Equal(1, draft.Version);
        Assert.Equal("draft", draft.Status);
        Assert.Null(store.GetPublished("release-train"));
        Assert.DoesNotContain(store.ListPublished(), w => w.Id == "release-train");
        var versions = store.ListVersions("release-train");
        Assert.NotNull(versions);
        Assert.Single(versions);
    }

    [Fact]
    public void Create_rejects_a_taken_id_and_a_bad_slug()
    {
        var store = NewStore();
        store.CreateDraft(Minimal());

        Assert.Throws<WorkflowConflictException>(() => store.CreateDraft(Minimal()));
        Assert.Throws<WorkflowConflictException>(() => store.CreateDraft(Minimal("mission")));
        Assert.Throws<WorkflowValidationException>(() => store.CreateDraft(Minimal("Not A Slug")));
        Assert.Throws<WorkflowValidationException>(() =>
            store.CreateDraft(new WorkflowContentRequest { Id = "x-flow", Name = "", Summary = "s" }));
    }

    // ---- draft writes + If-Match -------------------------------------------------------------------

    [Fact]
    public void UpdateDraft_is_a_full_replacement_and_recomputes_the_hash()
    {
        var store = NewStore();
        var first = store.CreateDraft(Minimal());

        var updated = store.UpdateDraft("release-train", Complete(), ifMatchHash: null)!;

        Assert.Equal(1, updated.Version);
        Assert.NotEqual(first.ContentHash, updated.ContentHash);
        Assert.Single(updated.Files);
        Assert.Equal("verify.py", updated.Files[0].FileName);

        // A DRAFT is never served by the pinned-read routes (it is mutable, so it cannot be pinned
        // history); the file becomes readable the moment the version publishes. The AUTHORING read
        // (the version detail) does carry the draft's file content - that is what the CLI's pull
        // round-trips, drafts included.
        Assert.Null(store.GetFileContent("release-train", "verify.py", version: 1));
        Assert.Null(store.GetInstructions("release-train", version: 1));
        Assert.Equal("print('verify')",
            store.GetVersionDetail("release-train", 1)!.Files.Single().Content);
        store.Publish("release-train");
        Assert.Equal("print('verify')",
            store.GetFileContent("release-train", "verify.py", version: 1));
    }

    [Fact]
    public void Null_collection_entries_and_oversized_fields_are_rejected_not_crashes()
    {
        var store = NewStore();

        var nullStep = Minimal("null-step");
        nullStep.Steps = new List<WorkflowStepDto> { null! };
        Assert.Throws<WorkflowValidationException>(() => store.CreateDraft(nullStep));

        var nullFile = Minimal("null-file");
        nullFile.Files = new List<WorkflowFileDto> { null! };
        Assert.Throws<WorkflowValidationException>(() => store.CreateDraft(nullFile));

        var hugeName = Minimal("huge-name");
        hugeName.Name = new string('x', WorkflowValidation.MaxShortFieldChars + 1);
        Assert.Throws<WorkflowValidationException>(() => store.CreateDraft(hugeName));

        var tooManySteps = Minimal("many-steps");
        tooManySteps.Steps = Enumerable.Range(0, WorkflowValidation.MaxStepsPerVersion + 1)
            .Select(i => new WorkflowStepDto { Name = $"s{i}", Doer = "Worker", Done = "d" })
            .ToList();
        Assert.Throws<WorkflowValidationException>(() => store.CreateDraft(tooManySteps));
    }

    [Fact]
    public void UpdateDraft_refuses_a_stale_IfMatch_and_accepts_the_current_one()
    {
        var store = NewStore();
        var draft = store.CreateDraft(Minimal());

        Assert.Throws<WorkflowConflictException>(() =>
            store.UpdateDraft("release-train", Complete(), ifMatchHash: "stale-hash"));

        var updated = store.UpdateDraft("release-train", Complete(), ifMatchHash: draft.ContentHash);
        Assert.NotNull(updated);
    }

    [Fact]
    public void UpdateDraft_on_a_published_workflow_mints_the_next_version_as_the_draft()
    {
        var store = NewStore();
        store.CreateDraft(Complete());
        store.Publish("release-train");

        var draft = store.UpdateDraft("release-train", Complete(), ifMatchHash: null)!;

        Assert.Equal(2, draft.Version);
        Assert.Equal("draft", draft.Status);
        Assert.True(store.GetPublished("release-train")!.HasDraft);
        // The catalog still serves v1 until the draft publishes.
        Assert.Equal(1, store.GetPublished("release-train")!.Version);
    }

    // ---- publish -----------------------------------------------------------------------------------

    [Fact]
    public void Publish_demands_instructions_and_at_least_one_complete_step()
    {
        var store = NewStore();
        store.CreateDraft(Minimal()); // skeletal: no instructions, no steps

        Assert.Throws<WorkflowValidationException>(() => store.Publish("release-train"));
    }

    [Fact]
    public void Publish_supersedes_the_previous_version_atomically()
    {
        var store = NewStore();
        store.CreateDraft(Complete());
        store.Publish("release-train");
        store.UpdateDraft("release-train", Complete(), ifMatchHash: null);
        store.Publish("release-train");

        var published = store.GetPublished("release-train")!;
        Assert.Equal(2, published.Version);
        var versions = store.ListVersions("release-train")!;
        Assert.Equal("published", versions.Single(v => v.Version == 2).Status);
        Assert.Equal("superseded", versions.Single(v => v.Version == 1).Status);
        Assert.Throws<WorkflowValidationException>(() => store.Publish("release-train")); // no draft left
    }

    // ---- pinned history ----------------------------------------------------------------------------

    [Fact]
    public void An_explicit_version_read_resolves_forever_even_after_supersede_and_archive()
    {
        var store = NewStore();
        store.CreateDraft(Complete());
        store.Publish("release-train");
        var v2 = Complete();
        v2.InstructionsMarkdown = "# Release train v2\n\nNew conduct.";
        store.UpdateDraft("release-train", v2, ifMatchHash: null);
        store.Publish("release-train");
        store.Archive("release-train");

        // The default read follows catalog semantics: archived = gone.
        Assert.Null(store.GetInstructions("release-train", version: null));
        // A pinned read is immutable history: both versions resolve, archived or not.
        Assert.Equal("# Release train\n\nCut the tag, verify, announce.",
            store.GetInstructions("release-train", version: 1));
        Assert.Equal("# Release train v2\n\nNew conduct.",
            store.GetInstructions("release-train", version: 2));
    }

    // ---- built-ins: customize + reset --------------------------------------------------------------

    [Fact]
    public void A_built_in_can_be_customized_and_then_reset_to_shipped()
    {
        var store = NewStore();
        var shipped = store.GetPublished("mission")!;

        // Customize: draft from the published baseline, edit, publish.
        var edit = new WorkflowContentRequest
        {
            Name = "Mission",
            Summary = "The customized mission conduct.",
            WhenToUse = shipped.WhenToUse,
            HumanCheckpoint = shipped.HumanCheckpoint,
            Steps = shipped.Steps,
            InstructionsMarkdown = "# Custom mission conduct",
            AuthoredBy = "test-session",
        };
        store.UpdateDraft("mission", edit, ifMatchHash: shipped.ContentHash);
        var customized = store.Publish("mission")!;
        Assert.Equal(2, customized.Version);
        Assert.Equal("# Custom mission conduct", store.GetInstructions("mission", null));

        // Reset: the shipped conduct comes back as a NEW version; nothing is rewritten.
        var reset = store.ResetToShipped("mission")!;
        Assert.Equal(3, reset.Version);
        Assert.Equal(BuiltInWorkflows.InstructionsFor("mission"), store.GetInstructions("mission", null));
        // The customized version remains pinned history.
        Assert.Equal("# Custom mission conduct", store.GetInstructions("mission", version: 2));
    }

    [Fact]
    public void Reset_only_applies_to_built_ins()
    {
        var store = NewStore();
        store.CreateDraft(Complete());
        store.Publish("release-train");

        Assert.Throws<WorkflowValidationException>(() => store.ResetToShipped("release-train"));
        Assert.Null(store.ResetToShipped("no-such-workflow"));
    }

    // ---- archive -----------------------------------------------------------------------------------

    [Fact]
    public void Archive_hides_a_user_workflow_but_never_a_built_in()
    {
        var store = NewStore();
        store.CreateDraft(Complete());
        store.Publish("release-train");

        Assert.True(store.Archive("release-train"));
        Assert.Null(store.GetPublished("release-train"));
        Assert.DoesNotContain(store.ListPublished(), w => w.Id == "release-train");

        Assert.Throws<WorkflowValidationException>(() => store.Archive("mission"));
        Assert.False(store.Archive("no-such-workflow"));
    }

    // ---- file rules --------------------------------------------------------------------------------

    [Fact]
    public void File_rules_reject_paths_bad_extensions_and_duplicates()
    {
        var store = NewStore();

        WorkflowContentRequest WithFile(string name) => new()
        {
            Id = "x-flow",
            Name = "X",
            Summary = "s",
            Files = new List<WorkflowFileDto> { new() { FileName = name, Content = "x" } },
        };

        Assert.Throws<WorkflowValidationException>(() => store.CreateDraft(WithFile("../escape.py")));
        Assert.Throws<WorkflowValidationException>(() => store.CreateDraft(WithFile("run.exe")));
        var duplicate = WithFile("a.py");
        duplicate.Files!.Add(new WorkflowFileDto { FileName = "a.py", Content = "y" });
        Assert.Throws<WorkflowValidationException>(() => store.CreateDraft(duplicate));
    }
}
