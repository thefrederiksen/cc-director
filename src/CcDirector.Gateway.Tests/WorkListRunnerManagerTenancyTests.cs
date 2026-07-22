using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Cross-tenant partition of the work-list machine drain slot (audit MED, gap audit-e). The machine key is
/// caller-controlled (a request body field or a cron job's target machine), so two tenants can present the
/// SAME key. Before the fix the single-drain slot was keyed by the bare machine key alone, so one tenant's
/// drain refused another tenant's drain on a shared key and the 409's <see cref="WorkListRunnerManager.ActiveList"/>
/// leaked the other tenant's list name. Each test below reddens if the slot is un-partitioned:
/// <see cref="Two_tenants_draining_the_same_machine_key_do_not_refuse_each_other"/> would see the second
/// admit REFUSED, and <see cref="ActiveList_never_reveals_another_tenants_list"/> would read the other
/// tenant's list name.
/// </summary>
public sealed class WorkListRunnerManagerTenancyTests
{
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Two_tenants_draining_the_same_machine_key_do_not_refuse_each_other()
    {
        var mgr = new WorkListRunnerManager();

        // Both tenants name the SAME machine key. Each is admitted in its OWN partition; neither refuses the
        // other. Reverting the fix (a single bare-key slot) makes the second admit RefusedMachineBusy.
        Assert.Equal(WorkListRunnerManager.AdmitResult.Admitted, mgr.TryAdmit(TenantA, "shared-machine", "list-A"));
        Assert.Equal(WorkListRunnerManager.AdmitResult.Admitted, mgr.TryAdmit(TenantB, "shared-machine", "list-B"));

        Assert.Equal("list-A", mgr.ActiveList(TenantA, "shared-machine"));
        Assert.Equal("list-B", mgr.ActiveList(TenantB, "shared-machine"));
    }

    [Fact]
    public void ActiveList_never_reveals_another_tenants_list()
    {
        var mgr = new WorkListRunnerManager();
        Assert.Equal(WorkListRunnerManager.AdmitResult.Admitted, mgr.TryAdmit(TenantA, "shared-machine", "secret-A"));

        // Tenant B, which has admitted nothing on this key, must see its OWN (empty) view - never tenant A's
        // list name. A shared bare-key slot would hand B the string "secret-A".
        Assert.Null(mgr.ActiveList(TenantB, "shared-machine"));
    }

    [Fact]
    public void Completing_one_tenants_slot_leaves_the_other_tenants_drain_intact()
    {
        var mgr = new WorkListRunnerManager();
        mgr.TryAdmit(TenantA, "shared-machine", "list-A");
        mgr.TryAdmit(TenantB, "shared-machine", "list-B");

        // A finishes its drain; B's slot on the same key is untouched, and A's key is now free for A only.
        mgr.Complete(TenantA, "shared-machine");

        Assert.Null(mgr.ActiveList(TenantA, "shared-machine"));
        Assert.Equal("list-B", mgr.ActiveList(TenantB, "shared-machine"));
        Assert.Equal(WorkListRunnerManager.AdmitResult.Admitted, mgr.TryAdmit(TenantA, "shared-machine", "list-A2"));
        // B is still busy on the key within its own partition.
        Assert.Equal(WorkListRunnerManager.AdmitResult.RefusedMachineBusy, mgr.TryAdmit(TenantB, "shared-machine", "list-B2"));
    }

    [Fact]
    public void Same_tenant_same_machine_key_is_still_refused()
    {
        // The v1 same-machine guard (criterion 8) is preserved WITHIN a tenant: a tenant's second drain on a
        // key it already holds is still refused.
        var mgr = new WorkListRunnerManager();
        Assert.Equal(WorkListRunnerManager.AdmitResult.Admitted, mgr.TryAdmit(TenantA, "machine-1", "first"));
        Assert.Equal(WorkListRunnerManager.AdmitResult.RefusedMachineBusy, mgr.TryAdmit(TenantA, "machine-1", "second"));
        Assert.Equal("first", mgr.ActiveList(TenantA, "machine-1"));
    }
}
