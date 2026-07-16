using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1177 (Phase 2, increment 2.1): the Director's shared mapper <see cref="ControlEndpoints.Map"/>
/// emits the RAW LOCAL FACTS the Gateway color fold needs - <c>IsBrandNew</c>, <c>IsControlled</c>,
/// <c>ControllerSessionId</c>, <c>IsBackgroundRunning</c> - straight from the <see cref="Session"/>, with
/// no folding here (StatusColor is still stamped exactly as before). Pure additive: this proves the facts
/// reach the wire; the existing SessionOrdering / SessionsAggregation suites prove nothing else changed.
/// </summary>
[Collection("DirectorRoot")]
public sealed class SessionDtoRawFactsTests
{
    private static (SessionManager sm, Session session) NewSession()
    {
        var sm = new SessionManager(new Core.Configuration.AgentOptions());
        var session = sm.CreateEmbeddedSession(Path.GetTempPath(), null, new ExecuteActionTestBackend());
        return (sm, session);
    }

    [Fact]
    public void Map_BrandNewSession_EmitsIsBrandNewTrue()
    {
        var (sm, session) = NewSession();
        try
        {
            // A freshly created session has taken no turn yet, so IsBrandNew is true.
            var dto = ControlEndpoints.Map(session, "dir-A");

            Assert.True(dto.IsBrandNew);
            Assert.False(dto.IsControlled);
            Assert.Null(dto.ControllerSessionId);
            Assert.False(dto.IsBackgroundRunning);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public void Map_EchoesTheGatewayStampedDisplayStateBackOntoTheDto()
    {
        var (sm, session) = NewSession();
        try
        {
            // The Gateway folded this session and stamped its answer down onto the Director. Map must echo
            // every field back onto the DTO the desktop rail reads, or the rail has no fold to render.
            var since = new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc);
            var until = since.AddHours(4);
            session.ApplyGatewayDisplayState("grey", "Snoozed", "onHold", since, until, snoozeExpired: true);

            var dto = ControlEndpoints.Map(session, "dir-A");

            Assert.Equal("grey", dto.EffectiveColor);
            Assert.Equal("Snoozed", dto.StateLabel);
            Assert.Equal("onHold", dto.TriageBucket);
            Assert.Equal(since, dto.NeedsYouSince);
            Assert.Equal(until, dto.SnoozeUntil);
            Assert.True(dto.SnoozeExpired);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public void Map_WithNoGatewayStamp_LeavesTheDisplayStateNull()
    {
        var (sm, session) = NewSession();
        try
        {
            // No Gateway has stamped a fold - the "no Gateway, no fold" floor. Map must not invent one.
            var dto = ControlEndpoints.Map(session, "dir-A");

            Assert.Null(dto.EffectiveColor);
            Assert.Null(dto.StateLabel);
            Assert.Null(dto.TriageBucket);
            Assert.Null(dto.SnoozeUntil);
            Assert.False(dto.SnoozeExpired);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public void Map_ControlledSubAgent_EmitsIsControlledAndControllerId()
    {
        var (sm, session) = NewSession();
        try
        {
            var controllerId = Guid.NewGuid();
            session.ControllerSessionId = controllerId; // a controlled "Supporting" sub-agent

            var dto = ControlEndpoints.Map(session, "dir-A");

            Assert.True(dto.IsControlled);
            Assert.Equal(controllerId.ToString(), dto.ControllerSessionId);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public void Map_BackgroundRunningSession_EmitsIsBackgroundRunningTrue()
    {
        var (sm, session) = NewSession();
        try
        {
            session.SetBackgroundRunning(true, "running a build");

            var dto = ControlEndpoints.Map(session, "dir-A");

            Assert.True(dto.IsBackgroundRunning);
        }
        finally { sm.Dispose(); }
    }

    [Fact]
    public void Map_UncontrolledForegroundSession_EmitsRawFactsFalse()
    {
        var (sm, session) = NewSession();
        try
        {
            // Simulate a session that has taken a turn (no longer brand-new) and is a normal foreground,
            // uncontrolled, non-background session.
            session.IsBrandNew = false;

            var dto = ControlEndpoints.Map(session, "dir-A");

            Assert.False(dto.IsBrandNew);
            Assert.False(dto.IsControlled);
            Assert.Null(dto.ControllerSessionId);
            Assert.False(dto.IsBackgroundRunning);
        }
        finally { sm.Dispose(); }
    }
}
