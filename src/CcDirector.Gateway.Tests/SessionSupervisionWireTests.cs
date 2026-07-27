using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The wire for the session supervision facts (internal#625 Phase 1): the Director counts turns
/// and keeps the waiting clock at the activity flip, the mapper puts all three on
/// <see cref="SessionDto.TurnCount"/> / <see cref="SessionDto.WaitingSince"/> /
/// <see cref="SessionDto.CumulativeIdleSeconds"/>, and they survive the Gateway's pushed-session
/// cache intact so every card renders the numbers the Director measured.
///
/// These pin the same two joints SessionUncommittedCountWireTests pins - the mapper and the cache
/// copy - because a field silently dropped at either one renders as "this session has no history",
/// which is exactly the defect this feature removes. The other pinned property is that UNKNOWN
/// survives as unknown: null is what an older Director reports, and it must never arrive at a
/// client as zero turns or zero idle.
/// </summary>
public sealed class SessionSupervisionWireTests
{
    [Fact]
    public void Map_CarriesTurnsAnchorAndIdleOntoTheDto()
    {
        using var session = NewSession();
        var t0 = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
        var now = t0;
        session.SupervisionClock = () => now;

        // One full turn (30 seconds of waiting), then a second turn ends and waits.
        session.ApplyTerminalActivityState(ActivityState.Working);
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);
        now = t0.AddSeconds(30);
        session.ApplyTerminalActivityState(ActivityState.Working);
        now = t0.AddSeconds(90);
        session.ApplyTerminalActivityState(ActivityState.WaitingForInput);

        var dto = ControlEndpoints.Map(session, "dir-A");

        Assert.Equal(2, dto.TurnCount);
        Assert.Equal(t0.AddSeconds(90), dto.WaitingSince);
        Assert.Equal(30, dto.CumulativeIdleSeconds);
    }

    [Fact]
    public void Map_AFreshSessionReportsZeros_NotNulls()
    {
        using var session = NewSession();

        var dto = ControlEndpoints.Map(session, "dir-A");

        // A live Director always knows: zero turns and zero idle are measured answers here.
        // Null is reserved for Directors that predate the fields.
        Assert.Equal(0, dto.TurnCount);
        Assert.Null(dto.WaitingSince);
        Assert.Equal(0, dto.CumulativeIdleSeconds);
    }

    [Fact]
    public void PushedSessionStore_ServesTheFactsBackUnchanged()
    {
        // The store hands out Clone()d copies; a copy that dropped one of these fields would
        // render as a session with no history, with no other symptom.
        var now = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
        var anchor = now.AddMinutes(-5);
        var store = new PushedSessionStore(() => now);
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-1");

        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-1", 0, new[]
        {
            new SessionDto
            {
                SessionId = "s-measured", ActivityState = "WaitingForInput",
                TurnCount = 14, WaitingSince = anchor, CumulativeIdleSeconds = 2520,
            },
            // An older Director that has never heard of the fields: unknown must survive as
            // unknown, never harden into "zero turns, zero idle".
            new SessionDto
            {
                SessionId = "s-old-director", ActivityState = "Working",
                TurnCount = null, WaitingSince = null, CumulativeIdleSeconds = null,
            },
        });

        var fresh = store.TryGetFresh(TenantId.Local, "dir-A", TimeSpan.FromSeconds(20));

        Assert.NotNull(fresh);
        var measured = Assert.Single(fresh, s => s.SessionId == "s-measured");
        Assert.Equal(14, measured.TurnCount);
        Assert.Equal(anchor, measured.WaitingSince);
        Assert.Equal(2520, measured.CumulativeIdleSeconds);

        var old = Assert.Single(fresh, s => s.SessionId == "s-old-director");
        Assert.Null(old.TurnCount);
        Assert.Null(old.WaitingSince);
        Assert.Null(old.CumulativeIdleSeconds);
    }

    private static Session NewSession()
        => new(Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null, new NullBackend(), SessionBackendType.ConPty);

    private sealed class NullBackend : ISessionBackend
    {
        public CircularTerminalBuffer? Buffer => null;
        public int ProcessId => 1;
        public string Status => "Null";
        public bool IsRunning => true;
        public bool HasExited => false;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Kill() { }
        public void Dispose() { }
    }
}
