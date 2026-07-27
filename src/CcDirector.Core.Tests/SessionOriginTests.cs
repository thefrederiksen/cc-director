using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The session-origin vocabulary and its composer (devthrottle_internal issue #982).
///
/// Everything here defends one property: an origin we did not measure must never come out looking like
/// one we did. The field exists to answer "what share of sessions do agents start", and every way that
/// number can be quietly wrong runs through this composer - a typo landing in a real bucket, a parent
/// id surviving on a human origin, a blank normalizing to something plausible.
/// </summary>
public class SessionOriginTests
{
    [Theory]
    [InlineData("human", SessionOriginKinds.Human)]
    [InlineData("AGENT", SessionOriginKinds.Agent)]
    [InlineData("  Schedule  ", SessionOriginKinds.Schedule)]
    [InlineData("unknown", SessionOriginKinds.Unknown)]
    public void Kind_normalizes_case_and_whitespace(string input, string expected)
        => Assert.Equal(expected, SessionOriginKinds.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("humans")]
    [InlineData("bot")]
    public void Kind_refuses_anything_it_does_not_know(string? input)
    {
        // Null, not "unknown". The API boundary needs to tell "the caller said nothing" from "the
        // caller said something we do not recognize" so it can reject the second - a mistyped origin
        // silently becoming "unknown" is indistinguishable from an honest older caller.
        Assert.Null(SessionOriginKinds.Normalize(input));
        Assert.False(SessionOriginKinds.IsValid(input));
    }

    [Theory]
    [InlineData("desktop", SessionOriginSurfaces.Desktop)]
    [InlineData("Cockpit", SessionOriginSurfaces.Cockpit)]
    [InlineData("CLI", SessionOriginSurfaces.Cli)]
    [InlineData("cron", SessionOriginSurfaces.Cron)]
    [InlineData("workflow", SessionOriginSurfaces.Workflow)]
    [InlineData("api", SessionOriginSurfaces.Api)]
    public void Surface_normalizes_case(string input, string expected)
        => Assert.Equal(expected, SessionOriginSurfaces.Normalize(input));

    [Theory]
    [InlineData("phone", SessionOriginSurfaces.Phone)]
    [InlineData("PHONE", SessionOriginSurfaces.Phone)]
    [InlineData("browser", SessionOriginSurfaces.Cockpit)]
    [InlineData("workstation", SessionOriginSurfaces.Unknown)]
    [InlineData("gateway", SessionOriginSurfaces.Unknown)]
    [InlineData(null, SessionOriginSurfaces.Unknown)]
    public void Device_type_maps_only_the_two_surfaces_a_person_holds(string? deviceType, string expected)
    {
        // The Gateway spawn relay overwrites the origin ONLY when this returns a real surface, so
        // "workstation" and "gateway" landing on unknown is what keeps a Director-relayed agent spawn
        // from being rewritten as a human one. Getting this wrong would erase agent lineage on exactly
        // the cross-machine spawns that make lineage worth having.
        Assert.Equal(expected, SessionOriginSurfaces.FromDeviceType(deviceType));
    }

    [Fact]
    public void Compose_keeps_a_parent_only_on_an_agent_origin()
    {
        var parent = Guid.NewGuid();

        var agent = SessionOrigin.Compose("agent", "cli", parent);
        Assert.Equal(SessionOriginKinds.Agent, agent.Kind);
        Assert.Equal(parent, agent.ParentSessionId);

        // A human origin that also names a parent is self-contradictory. The stated kind is what the
        // caller meant, so the parent goes - storing both would leave a lineage edge hanging off a
        // session nobody claims started it.
        Assert.Null(SessionOrigin.Compose("human", "cockpit", parent).ParentSessionId);
        Assert.Null(SessionOrigin.Compose("schedule", "cron", parent).ParentSessionId);
        Assert.Null(SessionOrigin.Compose(null, null, parent).ParentSessionId);
    }

    [Fact]
    public void Compose_lands_unknown_tokens_on_unknown_not_on_something_plausible()
    {
        var composed = SessionOrigin.Compose("robot", "smoke-signal", null);
        Assert.Equal(SessionOriginKinds.Unknown, composed.Kind);
        Assert.Equal(SessionOriginSurfaces.Unknown, composed.Surface);
    }

    [Fact]
    public void Unknown_is_the_default_shape()
    {
        var origin = SessionOrigin.Unknown;
        Assert.Equal(SessionOriginKinds.Unknown, origin.Kind);
        Assert.Equal(SessionOriginSurfaces.Unknown, origin.Surface);
        Assert.Null(origin.ParentSessionId);
    }

    [Fact]
    public void The_named_origins_say_what_they_mean()
    {
        Assert.Equal((SessionOriginKinds.Human, SessionOriginSurfaces.Desktop),
            (SessionOrigin.DesktopHuman.Kind, SessionOrigin.DesktopHuman.Surface));
        Assert.Equal((SessionOriginKinds.Schedule, SessionOriginSurfaces.Cron),
            (SessionOrigin.Cron.Kind, SessionOrigin.Cron.Surface));
        Assert.Equal((SessionOriginKinds.Schedule, SessionOriginSurfaces.Workflow),
            (SessionOrigin.Workflow.Kind, SessionOrigin.Workflow.Surface));

        // A schedule is neither of the two the issue named, and that is the point: a fleet with hourly
        // jobs would otherwise fold every one of them into "human" or "agent" and move the share.
        Assert.NotEqual(SessionOriginKinds.Human, SessionOrigin.Cron.Kind);
        Assert.NotEqual(SessionOriginKinds.Agent, SessionOrigin.Cron.Kind);
    }

    [Fact]
    public void Agent_and_human_factories_carry_the_surface_through()
    {
        var parent = Guid.NewGuid();
        var spawned = SessionOrigin.AgentFrom(parent, SessionOriginSurfaces.Cli);
        Assert.Equal(SessionOriginKinds.Agent, spawned.Kind);
        Assert.Equal(SessionOriginSurfaces.Cli, spawned.Surface);
        Assert.Equal(parent, spawned.ParentSessionId);

        var typed = SessionOrigin.HumanFrom(SessionOriginSurfaces.Phone);
        Assert.Equal(SessionOriginKinds.Human, typed.Kind);
        Assert.Equal(SessionOriginSurfaces.Phone, typed.Surface);
        Assert.Null(typed.ParentSessionId);
    }
}
