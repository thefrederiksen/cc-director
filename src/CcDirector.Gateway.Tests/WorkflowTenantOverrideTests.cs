using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workflows;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Per-tenant on/off for BUILT-IN workflows (Shared Workflow Library phase 2, devthrottle_internal
/// issue 514). The built-ins live once in the shared library partition and are read-only to tenants,
/// so a tenant's switch flip lands in the tenant's own <c>workflow_tenant_overrides</c> row and the
/// read paths fold it over the library state. The properties proven here:
///
///  - Off is PER TENANT: tenant A turning mission off hides it from A's briefings, refuses A's
///    default conduct read and A's new runs - while tenant B is completely untouched.
///  - Off never deletes: the flip back restores everything, and a pinned (explicit-version) read
///    keeps resolving while off - a seated run's conduct never disappears under it.
///  - The actor is recorded on the override row (a governance change has an actor, always).
/// </summary>
public sealed class WorkflowTenantOverrideTests : IDisposable
{
    private static readonly TenantId TenantA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly TenantId TenantB = new("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly GatewayDbTestHarness _harness = new();
    private readonly AsyncLocalTenantContext _tenant = new();
    private GatewayDatabase? _db;

    public void Dispose() => _harness.Dispose();

    private (WorkflowStore Workflows, WorkflowRunStore Runs) OpenHostedStores()
    {
        _db = _harness.Open(_tenant);
        using (_tenant.Enter(TenantId.System))
        {
            return (new WorkflowStore(_db), new WorkflowRunStore(_db));
        }
    }

    [Fact]
    public void TenantOff_IsScopedToThatTenantOnly()
    {
        var (workflows, runs) = OpenHostedStores();

        using (_tenant.Enter(TenantA))
        {
            Assert.True(workflows.SetEnabled("mission", false, "tenant-a-admin"));

            var mission = workflows.ListPublished().Single(w => w.Id == "mission");
            Assert.False(mission.Enabled);
            Assert.False(workflows.GetPublished("mission")!.Enabled);
            Assert.Throws<WorkflowValidationException>(() => workflows.GetInstructions("mission", null));
            Assert.Throws<WorkflowValidationException>(() => runs.Create("mission", "Refused run"));
            Assert.Throws<WorkflowValidationException>(() => runs.EnsureRunnable("mission"));
            Assert.False(runs.IsWorkflowEnabled("mission"));
            Assert.Equal(false, runs.GetWorkflowEnabled("mission"));
        }

        using (_tenant.Enter(TenantB))
        {
            // Tenant B never made a choice - the library's shipped state (ON) serves untouched.
            var mission = workflows.ListPublished().Single(w => w.Id == "mission");
            Assert.True(mission.Enabled);
            Assert.NotNull(workflows.GetInstructions("mission", null));
            var run = runs.Create("mission", "Tenant B still runs");
            Assert.Equal("mission", run.WorkflowId);
            Assert.True(runs.IsWorkflowEnabled("mission"));
        }
    }

    [Fact]
    public void TenantOff_NeverDeletes_AndFlippingBackRestoresEverything()
    {
        var (workflows, runs) = OpenHostedStores();

        using (_tenant.Enter(TenantA))
        {
            Assert.True(workflows.SetEnabled("mission", false, "tenant-a-admin"));

            // Pinned history keeps resolving while off - a seated run's conduct never disappears.
            Assert.Equal(BuiltInWorkflows.InstructionsFor("mission"), workflows.GetInstructions("mission", 1));
            // And the workflow is still LISTED (off, not gone).
            Assert.Contains(workflows.ListPublished(), w => w.Id == "mission" && !w.Enabled);

            Assert.True(workflows.SetEnabled("mission", true, "tenant-a-admin"));
            Assert.True(workflows.ListPublished().Single(w => w.Id == "mission").Enabled);
            Assert.NotNull(workflows.GetInstructions("mission", null));
            var run = runs.Create("mission", "Restored run");
            Assert.Equal(1, run.WorkflowVersion);
        }
    }

    [Fact]
    public void TheActorIsRecordedOnTheChoice()
    {
        var (workflows, _) = OpenHostedStores();

        using (_tenant.Enter(TenantA))
        {
            workflows.SetEnabled("mission", false, "qa-reviewer");

            using var ctx = _db!.CreateContext();
            var choice = ctx.WorkflowTenantOverrides.Single(o => o.WorkflowId == "mission");
            Assert.Equal("qa-reviewer", choice.UpdatedBy);
            Assert.False(choice.Enabled);
            Assert.True(choice.UpdatedAtUtc > DateTime.UtcNow.AddMinutes(-5));
        }
    }

    [Fact]
    public void TheSharedLibraryRowIsNeverTouchedByATenantFlip()
    {
        var (workflows, _) = OpenHostedStores();

        using (_tenant.Enter(TenantA))
        {
            workflows.SetEnabled("mission", false, "tenant-a-admin");
        }

        // The library head row keeps its shipped state - the flip landed in tenant A's partition only.
        using (_tenant.Enter(TenantId.System))
        {
            using var ctx = _db!.CreateContext();
            var head = ctx.Workflows.Single(h => h.Id == "mission");
            Assert.True(head.Enabled);
            Assert.Empty(ctx.WorkflowTenantOverrides.ToList()); // no override row in the System partition
        }
    }

    [Fact]
    public void SelfHost_SwitchStillWorks_ThroughTheOverride()
    {
        var db = _harness.Open(); // SingleTenantContext - the local single tenant
        var workflows = new WorkflowStore(db);
        var runs = new WorkflowRunStore(db);

        Assert.True(workflows.SetEnabled("mission", false, "owner"));
        Assert.False(workflows.ListPublished().Single(w => w.Id == "mission").Enabled);
        Assert.Throws<WorkflowValidationException>(() => runs.Create("mission", "Refused"));

        Assert.True(workflows.SetEnabled("mission", true, "owner"));
        Assert.True(workflows.ListPublished().Single(w => w.Id == "mission").Enabled);
    }
}
