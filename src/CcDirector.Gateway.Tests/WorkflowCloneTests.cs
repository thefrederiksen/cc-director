using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workflows;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Clone (Shared Workflow Library phase 4, devthrottle_internal issue 514) - the sanctioned
/// customization path for the read-only built-ins. A clone copies the source's PUBLISHED content
/// (steps, instructions, outcome criteria, helper files) into version 1 of a fresh tenant-owned id:
/// born published (immediately runnable), fully editable, provenance recorded, and completely
/// independent of the original - editing the clone never moves the built-in, and the clone is
/// invisible to every other tenant.
/// </summary>
public sealed class WorkflowCloneTests : IDisposable
{
    private static readonly TenantId TenantA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly TenantId TenantB = new("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly GatewayDbTestHarness _harness = new();
    private readonly AsyncLocalTenantContext _tenant = new();

    public void Dispose() => _harness.Dispose();

    private WorkflowStore OpenHostedStore()
    {
        var db = _harness.Open(_tenant);
        using (_tenant.Enter(TenantId.System))
        {
            return new WorkflowStore(db);
        }
    }

    [Fact]
    public void CloningABuiltIn_MakesAnEditableTenantOwnedCopy()
    {
        var store = OpenHostedStore();

        using (_tenant.Enter(TenantA))
        {
            var clone = store.Clone("mission", "acme-mission", "test-session")!;

            Assert.Equal("acme-mission", clone.Id);
            Assert.Equal(1, clone.Version);
            Assert.False(clone.IsBuiltIn);
            Assert.True(clone.Editable);
            Assert.True(clone.Enabled);
            Assert.Equal("Mission", clone.Name);
            // The conduct is the shipped mission conduct, byte for byte.
            Assert.Equal(BuiltInWorkflows.InstructionsFor("mission"),
                store.GetInstructions("acme-mission", null));
            // Provenance is recorded on the version row.
            var v1 = store.GetVersionDetail("acme-mission", 1)!;
            Assert.Equal("Cloned from 'mission' v1.", v1.ChangeNote);
            Assert.Equal("test-session", v1.AuthoredBy);
        }
    }

    [Fact]
    public void EditingTheClone_NeverMovesTheBuiltIn()
    {
        var store = OpenHostedStore();

        using (_tenant.Enter(TenantA))
        {
            var clone = store.Clone("mission", "acme-mission", "test-session")!;

            var edit = new WorkflowContentRequest
            {
                Name = "Acme Mission",
                Summary = "Our own mission conduct.",
                Steps = new List<WorkflowStepDto>
                {
                    new() { Name = "Build", Description = "Build it our way.", Doer = "Worker", Done = "Merged." },
                },
                InstructionsMarkdown = "# The Acme way",
                AuthoredBy = "test-session",
            };
            store.UpdateDraft("acme-mission", edit, ifMatchHash: clone.ContentHash);
            store.Publish("acme-mission");

            Assert.Equal("# The Acme way", store.GetInstructions("acme-mission", null));
            // The built-in still serves the shipped conduct, untouched.
            Assert.Equal(BuiltInWorkflows.InstructionsFor("mission"), store.GetInstructions("mission", null));
            Assert.False(store.GetPublished("mission")!.Editable);
        }
    }

    [Fact]
    public void TheClone_IsInvisibleToOtherTenants()
    {
        var store = OpenHostedStore();

        using (_tenant.Enter(TenantA))
        {
            store.Clone("mission", "acme-mission", "test-session");
        }

        using (_tenant.Enter(TenantB))
        {
            Assert.Null(store.GetPublished("acme-mission"));
            Assert.DoesNotContain(store.ListPublished(), w => w.Id == "acme-mission");
        }
    }

    [Fact]
    public void CloneRefusals_AreExact()
    {
        var store = OpenHostedStore();

        using (_tenant.Enter(TenantA))
        {
            // A built-in id can never be the clone's id - the library can never be shadowed.
            Assert.Throws<WorkflowConflictException>(() => store.Clone("mission", "standalone", "t"));
            // A taken id is refused.
            store.Clone("mission", "acme-mission", "t");
            Assert.Throws<WorkflowConflictException>(() => store.Clone("mission", "acme-mission", "t"));
            // A bad slug is refused.
            Assert.Throws<WorkflowValidationException>(() => store.Clone("mission", "Not A Slug", "t"));
            // A missing source is a null (the route's 404), never an invented workflow.
            Assert.Null(store.Clone("no-such-workflow", "fresh-id", "t"));
        }
    }

    [Fact]
    public void CloningOwnWorkflow_CopiesHelperFiles()
    {
        var store = OpenHostedStore();

        using (_tenant.Enter(TenantA))
        {
            store.CreateDraft(new WorkflowContentRequest
            {
                Id = "release-train",
                Name = "Release train",
                Summary = "Cut and announce.",
                Steps = new List<WorkflowStepDto>
                {
                    new() { Name = "Cut", Description = "Cut it.", Doer = "Worker", Done = "Tagged." },
                },
                InstructionsMarkdown = "# Release train",
                Files = new List<WorkflowFileDto>
                {
                    new() { FileName = "verify.py", Content = "print('verify')" },
                },
                AuthoredBy = "test-session",
            });
            store.Publish("release-train");

            var clone = store.Clone("release-train", "release-train-v2", "test-session")!;

            Assert.Equal(1, clone.Version);
            Assert.Equal("print('verify')",
                store.GetFileContent("release-train-v2", "verify.py", version: null));
            Assert.Equal("Cloned from 'release-train' v1.",
                store.GetVersionDetail("release-train-v2", 1)!.ChangeNote);
        }
    }
}
