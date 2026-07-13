using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tailscale;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Pure decision-logic tests for the Tailscale Serve provisioner (issue #179): which
/// Directors get a serve mapping, and what the self-healing reconcile asserts/sweeps.
/// No tailscale.exe and no network involved - these run keyless in CI.
/// </summary>
public class TailscaleServeProvisionerTests
{
    // Gateway Cleanup mission (the cut): the per-Director serve mapping (ShouldMap and its tests) is
    // deleted - only the Gateway front-door 443 is provisioned now, so the ComputeReconcileActions front-door
    // logic below is the whole surface. The PortsToMap/PortsToRemove machinery stays for the front-door
    // watch's "assert 443, sweep orphans" contract.

    // ------------------------------------------------- ComputeReconcileActions

    private const int GatewayPort = 7878;

    private static string StatusJson(params (int httpsPort, string proxy)[] entries)
    {
        var web = string.Join(",", entries.Select(e =>
            $"\"machine-a.tail0123.ts.net:{e.httpsPort}\": {{ \"Handlers\": {{ \"/\": {{ \"Proxy\": \"{e.proxy}\" }} }} }}"));
        return $"{{ \"TCP\": {{}}, \"Web\": {{ {web} }} }}";
    }

    [Fact]
    public void Reconcile_ConsistentTable_NoActions()
    {
        var json = StatusJson((443, "http://localhost:7878"), (7884, "http://localhost:7884"));

        var a = TailscaleServeProvisioner.ComputeReconcileActions(json, GatewayPort, [7884]);

        Assert.False(a.AssertFrontDoor);
        Assert.Empty(a.PortsToMap);
        Assert.Empty(a.PortsToRemove);
    }

    [Fact]
    public void Reconcile_FrontDoorMissing_Asserts()
    {
        // The live incident: 443 vanished from the serve table while Director mappings survived.
        var json = StatusJson((7884, "http://localhost:7884"));

        var a = TailscaleServeProvisioner.ComputeReconcileActions(json, GatewayPort, [7884]);

        Assert.True(a.AssertFrontDoor);
        Assert.Empty(a.PortsToMap);
        Assert.Empty(a.PortsToRemove);
    }

    [Fact]
    public void Reconcile_FrontDoorWrongBackend_Asserts()
    {
        var json = StatusJson((443, "http://localhost:7470"), (7884, "http://localhost:7884"));

        var a = TailscaleServeProvisioner.ComputeReconcileActions(json, GatewayPort, [7884]);

        Assert.True(a.AssertFrontDoor);
    }

    [Fact]
    public void Reconcile_FrontDoorClobbered_ReportsObservedBackend()
    {
        // The issue #200 incident: the whole table was replaced with a single mapping
        // 443 -> dead ephemeral port. The observed backend is the only forensic trace of
        // the clobberer, so it must come back for logging - and the Director mapping that
        // was wiped alongside it must be re-asserted.
        var json = StatusJson((443, "http://localhost:54550"));

        var a = TailscaleServeProvisioner.ComputeReconcileActions(json, GatewayPort, [7886]);

        Assert.True(a.AssertFrontDoor);
        Assert.Equal("http://localhost:54550", a.FrontDoorBackend);
        Assert.Equal([7886], a.PortsToMap);
    }

    [Fact]
    public void Reconcile_FrontDoorMissing_ObservedBackendIsNull()
    {
        var json = StatusJson((7884, "http://localhost:7884"));

        var a = TailscaleServeProvisioner.ComputeReconcileActions(json, GatewayPort, [7884]);

        Assert.True(a.AssertFrontDoor);
        Assert.Null(a.FrontDoorBackend);
    }

    [Fact]
    public void Reconcile_FrontDoorNonLoopbackBackend_AssertsAndReportsIt()
    {
        // A non-loopback 443 backend is wrong for us, but is still recorded verbatim:
        // "who clobbered the front door" matters more than passing the loopback filter.
        var json = StatusJson((443, "http://machine-b.tail0123.ts.net:8080"));

        var a = TailscaleServeProvisioner.ComputeReconcileActions(json, GatewayPort, []);

        Assert.True(a.AssertFrontDoor);
        Assert.Equal("http://machine-b.tail0123.ts.net:8080", a.FrontDoorBackend);
    }

    [Fact]
    public void Reconcile_HealthyFrontDoor_ReportsBackendAndNoAssert()
    {
        var json = StatusJson((443, "http://localhost:7878"));

        var a = TailscaleServeProvisioner.ComputeReconcileActions(json, GatewayPort, []);

        Assert.False(a.AssertFrontDoor);
        Assert.Equal("http://localhost:7878", a.FrontDoorBackend);
    }

    [Fact]
    public void Reconcile_EmptyDesired_FrontDoorOnlyShape_SweepsAreIgnorable()
    {
        // The front-door watch (issue #200) calls ComputeReconcileActions with an empty
        // desired set and acts ONLY on AssertFrontDoor. Managed mappings then all land in
        // PortsToRemove - that list must still be computed correctly so the watch's
        // "ignore the sweep" contract stays a deliberate choice, not an accident.
        var json = StatusJson((443, "http://localhost:54550"), (7886, "http://localhost:7886"));

        var a = TailscaleServeProvisioner.ComputeReconcileActions(json, GatewayPort, []);

        Assert.True(a.AssertFrontDoor);
        Assert.Equal("http://localhost:54550", a.FrontDoorBackend);
        Assert.Empty(a.PortsToMap);
        Assert.Equal([7886], a.PortsToRemove);
    }

    [Fact]
    public void Reconcile_LiveDirectorMappingMissing_ReAsserts()
    {
        // The live incident's second shape: a Director mapping vanished ("handler does not
        // exist" on our own removal four minutes after a successful map).
        var json = StatusJson((443, "http://localhost:7878"));

        var a = TailscaleServeProvisioner.ComputeReconcileActions(json, GatewayPort, [7879, 7884]);

        Assert.Equal([7879, 7884], a.PortsToMap);
        Assert.Empty(a.PortsToRemove);
    }

    [Fact]
    public void Reconcile_OrphanedEphemeralMappings_RemovedOnAnyPort()
    {
        // The pile-up: provisioner-shaped mappings (same-port localhost proxy) far outside
        // the fixed Director range must be swept once no live Director claims them.
        var json = StatusJson(
            (443, "http://localhost:7878"),
            (7884, "http://localhost:7884"),
            (50682, "http://localhost:50682"),
            (61602, "http://127.0.0.1:61602"));

        var a = TailscaleServeProvisioner.ComputeReconcileActions(json, GatewayPort, [7884]);

        Assert.False(a.AssertFrontDoor);
        Assert.Empty(a.PortsToMap);
        Assert.Equal([50682, 61602], a.PortsToRemove);
    }

    [Fact]
    public void Reconcile_ForeignMappings_NeverTouched()
    {
        // A mapping whose backend port differs from its HTTPS port was not created by the
        // provisioner (except 443, handled separately) - leave it alone.
        var json = StatusJson((443, "http://localhost:7878"), (8080, "http://localhost:3000"));

        var a = TailscaleServeProvisioner.ComputeReconcileActions(json, GatewayPort, []);

        Assert.Empty(a.PortsToRemove);
    }

    [Fact]
    public void Reconcile_EmptyTable_AssertsFrontDoorAndAllDesired()
    {
        var a = TailscaleServeProvisioner.ComputeReconcileActions("{}", GatewayPort, [7884, 7886]);

        Assert.True(a.AssertFrontDoor);
        Assert.Equal([7884, 7886], a.PortsToMap);
        Assert.Empty(a.PortsToRemove);
    }

    [Fact]
    public void Reconcile_BlankStatus_AssertsFrontDoor()
    {
        var a = TailscaleServeProvisioner.ComputeReconcileActions("", GatewayPort, []);

        Assert.True(a.AssertFrontDoor);
        Assert.Empty(a.PortsToMap);
        Assert.Empty(a.PortsToRemove);
    }
}
