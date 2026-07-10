using System;
using System.Collections.Generic;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the pure fleet-broadcast scope policy (issue #1229): who may reach whom, and when a
/// broadcast beyond the sender's team is refused. Pure and machine-independent - no host spun up.
/// </summary>
public sealed class FleetBroadcastPolicyTests
{
    private static BroadcastScope Mission(string missionId, string repo = "D:\\Repo", string machine = "M1")
        => new(missionId, GroupId: null, repo, machine);

    private static BroadcastScope Group(string groupId, string repo = "D:\\Repo", string machine = "M1")
        => new(MissionId: null, groupId, repo, machine);

    private static BroadcastScope Solo(string repo, string machine)
        => new(MissionId: null, GroupId: null, repo, machine);

    private static List<(string, BroadcastScope)> Targets(params (string Id, BroadcastScope Scope)[] t)
    {
        var list = new List<(string, BroadcastScope)>();
        foreach (var (id, scope) in t) list.Add((id, scope));
        return list;
    }

    // ===== In-scope: the free lane =====

    [Fact]
    public void SameMission_isAllowedInScope_withoutGrant()
    {
        var sender = Mission("mission-x");
        var targets = Targets(("a", Mission("mission-x")), ("b", Mission("mission-x")));

        var d = FleetBroadcastPolicy.Evaluate(sender, targets, hasValidGrant: false, reason: null);

        Assert.True(d.Allowed);
        Assert.Equal(BroadcastOutcome.AllowedInScope, d.Outcome);
        Assert.Equal(2, d.InScopeTargetIds.Count);
        Assert.Empty(d.OutOfScopeTargetIds);
    }

    [Fact]
    public void SameGroup_isAllowedInScope()
    {
        var sender = Group("group-1");
        var targets = Targets(("a", Group("group-1")));

        var d = FleetBroadcastPolicy.Evaluate(sender, targets, hasValidGrant: false, reason: null);

        Assert.True(d.Allowed);
        Assert.Equal(BroadcastOutcome.AllowedInScope, d.Outcome);
    }

    [Fact]
    public void SoloSameRepoAndMachine_isAllowedInScope_caseAndSeparatorInsensitive()
    {
        var sender = Solo("D:\\ReposFred\\devthrottle", "SOREN_NORTH");
        var targets = Targets(("a", Solo("d:/reposfred/devthrottle/", "soren_north")));

        var d = FleetBroadcastPolicy.Evaluate(sender, targets, hasValidGrant: false, reason: null);

        Assert.True(d.Allowed);
        Assert.Equal(BroadcastOutcome.AllowedInScope, d.Outcome);
    }

    [Fact]
    public void EmptyTargets_isAllowed()
    {
        var d = FleetBroadcastPolicy.Evaluate(Mission("x"), Targets(), hasValidGrant: false, reason: null);
        Assert.True(d.Allowed);
        Assert.Equal(BroadcastOutcome.AllowedInScope, d.Outcome);
    }

    // ===== Out of scope: the wall =====

    [Fact]
    public void DifferentMission_isDenied_withoutGrant()
    {
        var sender = Mission("mission-x");
        var targets = Targets(("a", Mission("mission-x")), ("b", Mission("mission-y")));

        var d = FleetBroadcastPolicy.Evaluate(sender, targets, hasValidGrant: false, reason: null);

        Assert.False(d.Allowed);
        Assert.Equal(BroadcastOutcome.DeniedOutOfScope, d.Outcome);
        Assert.Equal(new[] { "a" }, d.InScopeTargetIds);
        Assert.Equal(new[] { "b" }, d.OutOfScopeTargetIds);
        Assert.Contains("#1229", d.DeniedReason);
    }

    [Fact]
    public void SoloDifferentRepo_isDenied_theIncidentCase()
    {
        // The real incident: a session in one repo broadcasting to sessions in other repos.
        var sender = Solo("D:\\ReposMindzie\\mindzieDocs", "SOREN_NORTH");
        var targets = Targets(
            ("a", Solo("D:\\ReposFred\\cc-consult", "SOREN_NORTH")),
            ("b", Solo("D:\\ReposFred\\AgentEyes", "SOREN_NORTH")));

        var d = FleetBroadcastPolicy.Evaluate(sender, targets, hasValidGrant: false, reason: null);

        Assert.False(d.Allowed);
        Assert.Equal(BroadcastOutcome.DeniedOutOfScope, d.Outcome);
        Assert.Equal(2, d.OutOfScopeTargetIds.Count);
    }

    [Fact]
    public void SoloDifferentMachine_isDenied_evenSameRepoPath()
    {
        var sender = Solo("D:\\Repo", "MACHINE_A");
        var targets = Targets(("a", Solo("D:\\Repo", "MACHINE_B")));

        var d = FleetBroadcastPolicy.Evaluate(sender, targets, hasValidGrant: false, reason: null);

        Assert.False(d.Allowed);
        Assert.Equal(BroadcastOutcome.DeniedOutOfScope, d.Outcome);
    }

    // ===== Grant path =====

    [Fact]
    public void OutOfScope_withValidGrantAndReason_isAllowedByGrant()
    {
        var sender = Mission("mission-x");
        var targets = Targets(("a", Mission("mission-y")));

        var d = FleetBroadcastPolicy.Evaluate(sender, targets, hasValidGrant: true, reason: "fleet-wide maintenance notice");

        Assert.True(d.Allowed);
        Assert.Equal(BroadcastOutcome.AllowedByGrant, d.Outcome);
    }

    [Fact]
    public void OutOfScope_withGrantButNoReason_isDeniedMissingReason()
    {
        var sender = Mission("mission-x");
        var targets = Targets(("a", Mission("mission-y")));

        var d = FleetBroadcastPolicy.Evaluate(sender, targets, hasValidGrant: true, reason: "   ");

        Assert.False(d.Allowed);
        Assert.Equal(BroadcastOutcome.DeniedMissingReason, d.Outcome);
    }

    // ===== Unknown sender =====

    [Fact]
    public void UnknownSender_withTargets_isDenied_withoutGrant()
    {
        var targets = Targets(("a", Mission("mission-y")));

        var d = FleetBroadcastPolicy.Evaluate(sender: null, targets, hasValidGrant: false, reason: null);

        Assert.False(d.Allowed);
        Assert.Equal(BroadcastOutcome.DeniedUnknownSender, d.Outcome);
        Assert.Equal(new[] { "a" }, d.OutOfScopeTargetIds);
    }

    [Fact]
    public void UnknownSender_withValidGrantAndReason_isAllowedByGrant()
    {
        var targets = Targets(("a", Mission("mission-y")), ("b", Solo("D:\\X", "M")));

        var d = FleetBroadcastPolicy.Evaluate(sender: null, targets, hasValidGrant: true, reason: "operator broadcast");

        Assert.True(d.Allowed);
        Assert.Equal(BroadcastOutcome.AllowedByGrant, d.Outcome);
    }

    [Fact]
    public void UnknownSender_withNoTargets_isAllowed()
    {
        var d = FleetBroadcastPolicy.Evaluate(sender: null, Targets(), hasValidGrant: false, reason: null);
        Assert.True(d.Allowed);
    }

    [Fact]
    public void MissionPrecedence_missionWins_overRepo()
    {
        // Same repo/machine but DIFFERENT mission => out of scope. Mission is the team boundary.
        var sender = new BroadcastScope("mission-x", GroupId: null, "D:\\Repo", "M1");
        var targets = Targets(("a", new BroadcastScope("mission-y", GroupId: null, "D:\\Repo", "M1")));

        var d = FleetBroadcastPolicy.Evaluate(sender, targets, hasValidGrant: false, reason: null);

        Assert.False(d.Allowed);
        Assert.Equal(BroadcastOutcome.DeniedOutOfScope, d.Outcome);
    }
}
