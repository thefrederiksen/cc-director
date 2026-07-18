using CcDirector.Setup.Engine;
using CcDirectorSetup.Models;
using CcDirectorSetup.Services;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// The cc-director installer's FRESH-install path (a Workstation install) shows NOTHING about a gateway.
/// The fresh install always lays down the Director set (issue #1807) and never mentions a gateway; the
/// Gateway wording that remains in the wizard is gated behind the update-of-a-Gateway-host path (the
/// Tailscale prerequisite row, the Install-step Gateway and Cockpit card, the read-only installed-role
/// line), which a fresh Workstation install never reaches.
///
/// This pins the one place a gateway string leaked onto the fresh path: the Prerequisites checklist copy
/// the person reads on the Prerequisites step. The ".NET 10 Runtime" description used to read "(runs the
/// Director, Gateway, and Cockpit)" and rendered on every fresh install.
///
/// Revert-proof (real production line): restoring "Gateway" to any Workstation-role checklist item's
/// user-visible copy in <see cref="PrerequisiteChecker.CreateChecklist"/> reds
/// <see cref="WorkstationPrerequisiteChecklist_CopyIsGatewayFree"/>.
/// </summary>
public sealed class InstallerNoGatewaySurfaceTests
{
    [Fact]
    public void WorkstationPrerequisiteChecklist_CopyIsGatewayFree()
    {
        var checklist = PrerequisiteChecker.CreateChecklist(InstallRole.Workstation);

        Assert.NotEmpty(checklist);
        foreach (var item in checklist)
        {
            // Name and Description are the user-visible copy rendered on the Prerequisites step; neither
            // may mention a gateway on a fresh Workstation install.
            Assert.DoesNotContain("gateway", item.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gateway", item.Description, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void WorkstationPrerequisiteChecklist_HasNoGatewayOnlyTailscaleRow()
    {
        // The Tailscale row is added ONLY for the Gateway role; a fresh Workstation install must not
        // surface it (its copy is deliberately gateway-centric), so the fresh path stays gateway-free.
        var checklist = PrerequisiteChecker.CreateChecklist(InstallRole.Workstation);

        Assert.DoesNotContain(checklist, (PrerequisiteInfo i) => i.Name == "Tailscale");
    }
}
