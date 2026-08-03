using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Running;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="WorkListRunnerManager"/> (issue #274): the v1 same-machine single-drain
/// guard (criterion 8 - one slot-5 test Director per machine) and the cross-machine concurrency case
/// (criterion 6 - different machines drain at the same time). The single-tenant behaviour is pinned here
/// with <see cref="TenantId.Local"/>; the cross-tenant partition (audit MED) is pinned by
/// <see cref="WorkListRunnerManagerTenancyTests"/>.
/// </summary>
public sealed class WorkListRunnerManagerTests
{
    private static readonly TenantId Local = TenantId.Local;

    [Fact]
    public void TryAdmit_SecondDrainSameMachine_Refused()
    {
        var mgr = new WorkListRunnerManager();

        Assert.Equal(WorkListRunnerManager.AdmitResult.Admitted, mgr.TryAdmit(Local, "machine-1", "today"));
        // Criterion 8: a second list on a machine already draining one is refused, not run.
        Assert.Equal(WorkListRunnerManager.AdmitResult.RefusedMachineBusy, mgr.TryAdmit(Local, "machine-1", "release-0.7"));
        Assert.Equal("today", mgr.ActiveList(Local, "machine-1"));
    }

    [Fact]
    public void TryAdmit_AfterComplete_SecondDrainAdmitted()
    {
        var mgr = new WorkListRunnerManager();

        Assert.Equal(WorkListRunnerManager.AdmitResult.Admitted, mgr.TryAdmit(Local, "machine-1", "today"));
        mgr.Complete(Local, "machine-1");

        Assert.Null(mgr.ActiveList(Local, "machine-1"));
        Assert.Equal(WorkListRunnerManager.AdmitResult.Admitted, mgr.TryAdmit(Local, "machine-1", "release-0.7"));
    }

    [Fact]
    public void TryAdmit_DifferentMachines_BothAdmitted()
    {
        var mgr = new WorkListRunnerManager();

        // Criterion 6: two lists on two different machines drain at the same time, no interference.
        Assert.Equal(WorkListRunnerManager.AdmitResult.Admitted, mgr.TryAdmit(Local, "machine-1", "today"));
        Assert.Equal(WorkListRunnerManager.AdmitResult.Admitted, mgr.TryAdmit(Local, "machine-2", "release-0.7"));

        Assert.Equal("today", mgr.ActiveList(Local, "machine-1"));
        Assert.Equal("release-0.7", mgr.ActiveList(Local, "machine-2"));
    }
}
