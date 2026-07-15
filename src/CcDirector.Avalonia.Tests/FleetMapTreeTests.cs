using CcDirector.Avalonia.Fleet;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Issue #1627: the desktop fleet map's spawn tree and lane grouping.
///
/// These are the SAME rules the Cockpit's map follows (issue #1626,
/// apps/cockpit/src/fleet/fleetMapFormat.ts). They are restated here rather than shared because that copy
/// is TypeScript in a browser and there is no code path that could carry one implementation to both. The
/// thing that MUST not drift - the ROLE - is not restated anywhere: it arrives on the wire already decided
/// by the Gateway's FleetRoleResolver, and both views read it.
/// </summary>
public sealed class FleetMapTreeTests
{
    private static SessionDto S(string id, string? controller = null, string activityState = "Working",
                               string? repo = null, string? agent = null, int? number = null)
        => new()
        {
            SessionId = id,
            ActivityState = activityState,
            IsControlled = controller is not null,
            ControllerSessionId = controller,
            RepoPath = repo ?? @"D:\ReposFred\devthrottle",
            Agent = agent ?? "ClaudeCode",
            Number = number,
            Name = id,
        };

    // Order by id so the assertions are about the TREE, not about sorting.
    private static int ById(SessionDto a, SessionDto b)
        => string.CompareOrdinal(a.SessionId, b.SessionId);

    private static string[] Shape(params SessionDto[] fleet)
        => FleetMapTree.Build(fleet, ById).Select(n => $"{n.Session.SessionId}@{n.Depth}").ToArray();

    [Fact]
    public void Build_ControlledSession_NestsUnderItsController()
    {
        Assert.Equal(new[] { "a@0", "b@1" }, Shape(S("b", controller: "a"), S("a")));
    }

    [Fact]
    public void Build_DeepChain_NestsBeyondTwoLevels()
    {
        // Nesting is real and the depth is not capped; only the visual indent is.
        var shape = Shape(S("a"), S("b", "a"), S("c", "b"), S("d", "c"), S("e", "d"));
        Assert.Equal(new[] { "a@0", "b@1", "c@2", "d@3", "e@4" }, shape);
    }

    [Fact]
    public void Build_Siblings_StayWithTheirParentDepthFirst()
    {
        var shape = Shape(S("arch"), S("m1", "arch"), S("m2", "arch"), S("w1", "m1"), S("w2", "m2"));
        Assert.Equal(new[] { "arch@0", "m1@1", "w1@2", "m2@1", "w2@2" }, shape);
    }

    [Fact]
    public void Build_ControllerNotInThisLane_RendersAtTopLevel()
    {
        // The pivots slice the fleet, so a Worker's Manager can be filtered out of the lane entirely.
        Assert.Equal(new[] { "b@0" }, Shape(S("b", controller: "elsewhere")));
    }

    [Fact]
    public void Build_ExitedController_DoesNotAdoptItsChild()
    {
        // The Gateway already demotes a session whose controller exited; indenting under the corpse
        // would say the opposite of what the roster says.
        Assert.Equal(new[] { "a@0", "b@0" }, Shape(S("a", activityState: "Exited"), S("b", "a")));
    }

    [Fact]
    public void Build_SelfReference_IsItsOwnRoot()
    {
        Assert.Equal(new[] { "a@0" }, Shape(S("a", controller: "a")));
    }

    [Fact]
    public void Build_Cycle_DoesNotHangAndLosesNothing()
    {
        // Neither member of a cycle can reach a root, so both are promoted to roots and render flat.
        // A cycle must never hang the view or swallow a card.
        var shape = Shape(S("a", "b"), S("b", "a"), S("c"));
        Assert.Equal(3, shape.Length);
        Assert.Equal(new[] { "a@0", "b@0", "c@0" }, shape.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Build_EverySession_RendersExactlyOnce()
    {
        // A lost card is a worse bug than a badly indented one.
        var fleet = new[]
        {
            S("a"), S("b", "a"), S("c", "b"),
            S("d", "gone"),
            S("e", activityState: "Exited"), S("f", "e"),
        };
        var nodes = FleetMapTree.Build(fleet, ById);
        Assert.Equal(6, nodes.Count);
        Assert.Equal(6, nodes.Select(n => n.Session.SessionId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Build_ControllerIdWithoutIsControlled_IsNotAnEdge()
    {
        // IsControlled is the fact; a stale controller id without it is not an edge.
        var orphan = S("b");
        orphan.ControllerSessionId = "a";
        orphan.IsControlled = false;
        Assert.Equal(new[] { "a@0", "b@0" }, Shape(S("a"), orphan));
    }

    [Fact]
    public void Build_EmptyLane_ReturnsNothing()
    {
        Assert.Empty(FleetMapTree.Build(Array.Empty<SessionDto>(), ById));
    }
}

/// <summary>Issue #1627: the desktop fleet map's pivots.</summary>
public sealed class FleetMapLanesTests
{
    private static SessionDto S(string id, string repo, string agent)
        => new() { SessionId = id, Name = id, RepoPath = repo, Agent = agent, ActivityState = "Working" };

    [Fact]
    public void Build_RepositoryPivot_GroupsByRepoBasename()
    {
        var lanes = FleetMapLanes.Build(
            new[]
            {
                S("a", @"D:\ReposFred\devthrottle", "ClaudeCode"),
                S("b", @"D:\ReposFred\mindzieWeb", "Codex"),
                S("c", @"D:\ReposFred\devthrottle", "Codex"),
            },
            FleetPivot.Repository, FleetMapLanes.DefaultSort);

        Assert.Equal(new[] { "devthrottle", "mindzieWeb" }, lanes.Select(l => l.Title).ToArray());
        Assert.Equal(2, lanes[0].Count);
        Assert.Equal(1, lanes[1].Count);
    }

    [Fact]
    public void Build_AgentPivot_GroupsByAgent()
    {
        var lanes = FleetMapLanes.Build(
            new[]
            {
                S("a", @"D:\ReposFred\devthrottle", "ClaudeCode"),
                S("b", @"D:\ReposFred\mindzieWeb", "Codex"),
                S("c", @"D:\ReposFred\devthrottle", "Codex"),
            },
            FleetPivot.Agent, FleetMapLanes.DefaultSort);

        Assert.Equal(new[] { "ClaudeCode", "Codex" }, lanes.Select(l => l.Title).ToArray());
        Assert.Equal(2, lanes[1].Count);
    }

    [Theory]
    [InlineData(@"D:\ReposFred\devthrottle", "devthrottle")]
    [InlineData(@"D:\ReposFred\devthrottle\", "devthrottle")]
    [InlineData("/home/soren/devthrottle", "devthrottle")]
    [InlineData("devthrottle", "devthrottle")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void RepoBasename_TakesTheLastSegment(string? path, string expected)
    {
        Assert.Equal(expected, FleetMapLanes.RepoBasename(path));
    }

    [Fact]
    public void LaneKey_BlankValues_GetANamedBucketRatherThanVanishing()
    {
        // A session with no repository is a fact worth seeing, not a card to drop.
        var noRepo = new SessionDto { SessionId = "a", RepoPath = "", Agent = "" };
        Assert.Equal("(no repository)", FleetMapLanes.LaneKey(noRepo, FleetPivot.Repository));
        Assert.Equal("(unknown agent)", FleetMapLanes.LaneKey(noRepo, FleetPivot.Agent));
    }

    [Fact]
    public void Filter_ByDefault_KeepsOnlyThisDirectorsSessions()
    {
        // The default is this cc-director's map: the sessions a click here can actually open.
        var fleet = new[] { S("mine", "r", "a"), S("theirs", "r", "a") };
        var local = new HashSet<string>(new[] { "mine" }, StringComparer.OrdinalIgnoreCase);

        var kept = FleetMapLanes.Filter(fleet, local, showWholeFleet: false);

        Assert.Equal(new[] { "mine" }, kept.Select(s => s.SessionId).ToArray());
    }

    [Fact]
    public void Filter_WhenShowingTheWholeFleet_KeepsEverything()
    {
        var fleet = new[] { S("mine", "r", "a"), S("theirs", "r", "a") };
        var local = new HashSet<string>(new[] { "mine" }, StringComparer.OrdinalIgnoreCase);

        var kept = FleetMapLanes.Filter(fleet, local, showWholeFleet: true);

        Assert.Equal(2, kept.Count);
    }

    [Fact]
    public void Filter_IsByDirector_NotByMachine()
    {
        // Two Directors on ONE machine. The other Director's session must NOT survive the default filter:
        // this Director cannot open it, so "visible" and "clickable" would stop meaning the same thing.
        var mine = S("mine", "r", "a");
        mine.MachineName = "SOREN_NORTH";
        mine.DirectorId = "director-1";
        var sameBoxOtherDirector = S("theirs", "r", "a");
        sameBoxOtherDirector.MachineName = "SOREN_NORTH";
        sameBoxOtherDirector.DirectorId = "director-2";

        var local = new HashSet<string>(new[] { "mine" }, StringComparer.OrdinalIgnoreCase);
        var kept = FleetMapLanes.Filter(new[] { mine, sameBoxOtherDirector }, local, showWholeFleet: false);

        Assert.Equal(new[] { "mine" }, kept.Select(s => s.SessionId).ToArray());
    }

    [Fact]
    public void Filter_WithNoLocalSessions_KeepsNothing()
    {
        // An empty map, which the view explains rather than leaving blank.
        var kept = FleetMapLanes.Filter(new[] { S("theirs", "r", "a") },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), showWholeFleet: false);
        Assert.Empty(kept);
    }

    [Fact]
    public void Build_LanesAreNameOrdered_SoTheMapDoesNotReshuffleBetweenPolls()
    {
        var lanes = FleetMapLanes.Build(
            new[]
            {
                S("a", @"D:\z-last", "ClaudeCode"),
                S("b", @"D:\a-first", "ClaudeCode"),
                S("c", @"D:\m-middle", "ClaudeCode"),
            },
            FleetPivot.Repository, FleetMapLanes.DefaultSort);

        Assert.Equal(new[] { "a-first", "m-middle", "z-last" }, lanes.Select(l => l.Title).ToArray());
    }
}
