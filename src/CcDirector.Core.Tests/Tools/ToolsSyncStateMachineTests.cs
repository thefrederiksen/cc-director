using System;
using CcDirector.Core.Tools;
using Xunit;

namespace CcDirector.Core.Tests.Tools;

/// <summary>
/// Covers the pure state machine behind the active cc-* tools indicator (issue #829): the
/// Green -> Orange (syncing) -> Green normal cycle, the repeated-failure escalation to red, the
/// auto-update opt-out passive warning, the one-in-flight debounce, and the exponential backoff
/// schedule. No Avalonia, no I/O.
///
/// Issue #1045 reshaped two things here, and the cases at the bottom of the file pin both:
///   - reporting an attempt takes the post-reconcile drift verdict, so "succeeded" cannot be
///     asserted while drift stands;
///   - a tool fault a reconcile cannot repair is a separate input from drift a reconcile can.
/// </summary>
public class ToolsSyncStateMachineTests
{
    [Fact]
    public void Evaluate_NoDrift_IsInSyncAndDoesNotReconcile()
    {
        var sm = new ToolsSyncStateMachine();

        var d = sm.Evaluate(reconcilableDrift: false, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: false);

        Assert.Equal(ToolsIndicatorState.InSync, d.State);
        Assert.False(d.ShouldReconcile);
    }

    [Fact]
    public void Evaluate_DriftAndEnabled_IsSyncingAndAsksToReconcile()
    {
        var sm = new ToolsSyncStateMachine();

        var d = sm.Evaluate(reconcilableDrift: true, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: false);

        Assert.Equal(ToolsIndicatorState.Syncing, d.State);
        Assert.True(d.ShouldReconcile);
    }

    [Fact]
    public void Evaluate_DriftEnabledButReconcileInFlight_IsSyncingButDoesNotStartAnother()
    {
        var sm = new ToolsSyncStateMachine();

        // Debounce: one reconcile at a time - the badge stays orange but no second reconcile is started.
        var d = sm.Evaluate(reconcilableDrift: true, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: true);

        Assert.Equal(ToolsIndicatorState.Syncing, d.State);
        Assert.False(d.ShouldReconcile);
    }

    [Fact]
    public void Evaluate_DriftButAutoUpdateDisabled_IsPassiveWarningAndDoesNotReconcile()
    {
        var sm = new ToolsSyncStateMachine();

        var d = sm.Evaluate(reconcilableDrift: true, unreconcilableFault: false, autoUpdateEnabled: false, reconcileInFlight: false);

        Assert.Equal(ToolsIndicatorState.Warning, d.State);
        Assert.False(d.ShouldReconcile);
    }

    [Fact]
    public void OnReconcileFinished_NoFailureAndDriftGone_ReturnsToInSyncAndClearsFailures()
    {
        var sm = new ToolsSyncStateMachine();
        sm.Evaluate(reconcilableDrift: true, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: false);
        sm.OnReconcileFinished(reconcileFailed: true, driftRemains: true); // one prior failure on the books

        sm.OnReconcileFinished(reconcileFailed: false, driftRemains: false);

        Assert.Equal(ToolsIndicatorState.InSync, sm.State);
        Assert.Equal(0, sm.ConsecutiveFailures);
    }

    [Fact]
    public void OnReconcileFinished_BelowCeiling_StaysSyncing()
    {
        var sm = new ToolsSyncStateMachine();
        sm.Evaluate(reconcilableDrift: true, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: false);

        sm.OnReconcileFinished(reconcileFailed: true, driftRemains: true);

        Assert.Equal(1, sm.ConsecutiveFailures);
        Assert.Equal(ToolsIndicatorState.Syncing, sm.State);
    }

    [Fact]
    public void OnReconcileFinished_AtCeiling_EscalatesToNeedsAttention()
    {
        var sm = new ToolsSyncStateMachine();
        sm.Evaluate(reconcilableDrift: true, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: false);

        for (var i = 0; i < ToolsSyncStateMachine.MaxReconcileAttempts; i++)
            sm.OnReconcileFinished(reconcileFailed: true, driftRemains: true);

        Assert.Equal(ToolsSyncStateMachine.MaxReconcileAttempts, sm.ConsecutiveFailures);
        Assert.Equal(ToolsIndicatorState.NeedsAttention, sm.State);
    }

    [Fact]
    public void Evaluate_DriftEnabledAtCeiling_StaysRedAndStopsRetrying()
    {
        var sm = new ToolsSyncStateMachine();
        sm.Evaluate(reconcilableDrift: true, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: false);
        for (var i = 0; i < ToolsSyncStateMachine.MaxReconcileAttempts; i++)
            sm.OnReconcileFinished(reconcileFailed: true, driftRemains: true);

        // A fresh snapshot with drift still present must NOT ask for another reconcile - the ceiling holds.
        var d = sm.Evaluate(reconcilableDrift: true, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: false);

        Assert.Equal(ToolsIndicatorState.NeedsAttention, d.State);
        Assert.False(d.ShouldReconcile);
    }

    [Fact]
    public void Evaluate_DriftClearsAfterFailures_ResetsToInSyncAndForgetsFailures()
    {
        var sm = new ToolsSyncStateMachine();
        sm.Evaluate(reconcilableDrift: true, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: false);
        sm.OnReconcileFinished(reconcileFailed: true, driftRemains: true);
        sm.OnReconcileFinished(reconcileFailed: true, driftRemains: true);

        // The drift resolves (e.g. another Director fixed it): the badge clears and the budget resets.
        var resolved = sm.Evaluate(reconcilableDrift: false, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: false);
        Assert.Equal(ToolsIndicatorState.InSync, resolved.State);
        Assert.Equal(0, sm.ConsecutiveFailures);

        // A brand-new drift gets a fresh full attempt budget rather than inheriting the old failures.
        var fresh = sm.Evaluate(reconcilableDrift: true, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: false);
        Assert.Equal(ToolsIndicatorState.Syncing, fresh.State);
        Assert.True(fresh.ShouldReconcile);
    }

    [Fact]
    public void Evaluate_AutoUpdateDisabled_ClearsAnyPriorFailureBudget()
    {
        var sm = new ToolsSyncStateMachine();
        sm.Evaluate(reconcilableDrift: true, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: false);
        sm.OnReconcileFinished(reconcileFailed: true, driftRemains: true);

        var d = sm.Evaluate(reconcilableDrift: true, unreconcilableFault: false, autoUpdateEnabled: false, reconcileInFlight: false);

        Assert.Equal(ToolsIndicatorState.Warning, d.State);
        Assert.Equal(0, sm.ConsecutiveFailures);
    }

    [Fact]
    public void NextBackoff_GrowsExponentiallyWithConsecutiveFailures()
    {
        var sm = new ToolsSyncStateMachine();
        sm.Evaluate(reconcilableDrift: true, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: false);

        sm.OnReconcileFinished(reconcileFailed: true, driftRemains: true);
        var first = sm.NextBackoff();
        sm.OnReconcileFinished(reconcileFailed: true, driftRemains: true);
        var second = sm.NextBackoff();

        Assert.Equal(ToolsSyncStateMachine.BaseBackoff, first);
        Assert.Equal(TimeSpan.FromSeconds(ToolsSyncStateMachine.BaseBackoff.TotalSeconds * 2), second);
    }

    [Fact]
    public void FullCycle_GreenToOrangeToGreen()
    {
        var sm = new ToolsSyncStateMachine();

        // Green at rest.
        Assert.Equal(ToolsIndicatorState.InSync,
            sm.Evaluate(reconcilableDrift: false, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: false).State);

        // Drift -> orange + reconcile.
        var drift = sm.Evaluate(reconcilableDrift: true, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: false);
        Assert.Equal(ToolsIndicatorState.Syncing, drift.State);
        Assert.True(drift.ShouldReconcile);

        // Reconcile fixes it -> back to green.
        sm.OnReconcileFinished(reconcileFailed: false, driftRemains: false);
        Assert.Equal(ToolsIndicatorState.InSync, sm.State);
    }

    // ---- Issue #1045: in-sync must be unreachable while drift stands ---------------------------------

    [Fact]
    public void OnReconcileFinished_ReconcileReportsSuccessButDriftRemains_IsNotInSync()
    {
        // The exact contradiction from the clean-install log: the reconcile reported no failure, and the
        // drift it was reconciling was still there. There is no argument list that calls that in-sync.
        var sm = new ToolsSyncStateMachine();
        sm.Evaluate(reconcilableDrift: true, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: false);

        sm.OnReconcileFinished(reconcileFailed: false, driftRemains: true);

        Assert.NotEqual(ToolsIndicatorState.InSync, sm.State);
        Assert.Equal(1, sm.ConsecutiveFailures);
    }

    [Fact]
    public void OnReconcileFinished_ReconcileFailedButDriftGone_IsNotInSync()
    {
        // The other direction of the same rule: a reconcile that errored does not get to claim green just
        // because the drift happened to clear (another Director may have fixed it mid-flight).
        var sm = new ToolsSyncStateMachine();
        sm.Evaluate(reconcilableDrift: true, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: false);

        sm.OnReconcileFinished(reconcileFailed: true, driftRemains: false);

        Assert.NotEqual(ToolsIndicatorState.InSync, sm.State);
    }

    // ---- Issue #1045: a fault no reconcile can repair does not spend the attempt budget --------------

    [Fact]
    public void Evaluate_ToolFaultWithNoReconcilableDrift_GoesStraightToNeedsAttention()
    {
        // cc-pdf installed, shimmed, venv healthy, and failing its own smoke check. Reconciling that three
        // times over is three guaranteed no-ops; the honest answer is the red to-do, first time.
        var sm = new ToolsSyncStateMachine();

        var d = sm.Evaluate(reconcilableDrift: false, unreconcilableFault: true, autoUpdateEnabled: true, reconcileInFlight: false);

        Assert.Equal(ToolsIndicatorState.NeedsAttention, d.State);
        Assert.False(d.ShouldReconcile);
    }

    [Fact]
    public void Evaluate_ToolFaultAlongsideReconcilableDrift_StillReconcilesFirst()
    {
        // A half-installed toolbelt can present as both. The reconcile is worth running, so drift wins and
        // the fault is judged again on the far side of it.
        var sm = new ToolsSyncStateMachine();

        var d = sm.Evaluate(reconcilableDrift: true, unreconcilableFault: true, autoUpdateEnabled: true, reconcileInFlight: false);

        Assert.Equal(ToolsIndicatorState.Syncing, d.State);
        Assert.True(d.ShouldReconcile);
    }

    [Fact]
    public void Evaluate_ToolFaultThenResolved_ReturnsToGreenWithAFreshBudget()
    {
        // The red state must not be a one-way door: once the tool works again the badge clears, and a later
        // drift gets its full attempt budget rather than inheriting the fault's spent one.
        var sm = new ToolsSyncStateMachine();
        sm.Evaluate(reconcilableDrift: false, unreconcilableFault: true, autoUpdateEnabled: true, reconcileInFlight: false);
        Assert.Equal(ToolsIndicatorState.NeedsAttention, sm.State);

        var resolved = sm.Evaluate(reconcilableDrift: false, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: false);
        Assert.Equal(ToolsIndicatorState.InSync, resolved.State);
        Assert.Equal(0, sm.ConsecutiveFailures);

        var fresh = sm.Evaluate(reconcilableDrift: true, unreconcilableFault: false, autoUpdateEnabled: true, reconcileInFlight: false);
        Assert.Equal(ToolsIndicatorState.Syncing, fresh.State);
        Assert.True(fresh.ShouldReconcile);
    }
}
