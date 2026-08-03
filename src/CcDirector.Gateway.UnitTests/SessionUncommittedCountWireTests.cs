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
/// The wire for a session's uncommitted file count: the Director measures it, the mapper puts it on
/// <see cref="SessionDto.UncommittedCount"/>, and it survives the Gateway's pushed-session cache intact so
/// the Cockpit roster can render the same "N chg" badge the desktop rail shows.
///
/// WHY THIS EXISTS AT ALL. The count used to be computed in the desktop window and written onto a view
/// model, so it never touched the DTO: the Gateway was never told, and the Cockpit roster had nothing to
/// show. These tests pin the two joints that made that true - the mapper, and the cache copy - because a
/// field silently dropped at either one looks exactly like the bug we just fixed.
///
/// The other property under test is that UNKNOWN survives as unknown. Null means the git probe has not
/// succeeded; it must never arrive at a client as 0, which every reader renders as a verified-clean tree.
/// </summary>
public sealed class SessionUncommittedCountWireTests
{
    [Fact]
    public void Map_CarriesTheCountOntoTheDto()
    {
        using var session = NewSession();
        session.UncommittedCount = 12;

        var dto = ControlEndpoints.Map(session, "dir-A");

        Assert.Equal(12, dto.UncommittedCount);
    }

    [Fact]
    public void Map_UnprobedSessionReportsNull_NotZero()
    {
        using var session = NewSession();

        var dto = ControlEndpoints.Map(session, "dir-A");

        // Null is "we have not been able to tell". A zero here would claim a clean tree nobody measured.
        Assert.Null(dto.UncommittedCount);
    }

    [Fact]
    public void Map_VerifiedCleanTreeReportsZero()
    {
        using var session = NewSession();
        session.UncommittedCount = 0;

        var dto = ControlEndpoints.Map(session, "dir-A");

        // A successful probe that found nothing IS zero, and is distinguishable from the null above.
        Assert.Equal(0, dto.UncommittedCount);
    }

    [Fact]
    public void PushedSessionStore_ServesTheCountBackUnchanged()
    {
        // The store hands out Clone()d copies so one request cannot contaminate the cache for the next. A
        // hand-written copy that forgot this field would drop the badge with no other symptom.
        var now = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
        var store = new PushedSessionStore(() => now);
        store.RegisterConnection(TenantId.Local, "dir-A", "conn-1");

        store.ApplySnapshot(TenantId.Local, "dir-A", "conn-1", 0, new[]
        {
            new SessionDto { SessionId = "s-dirty", ActivityState = "Working", UncommittedCount = 12 },
            new SessionDto { SessionId = "s-clean", ActivityState = "Working", UncommittedCount = 0 },
            new SessionDto { SessionId = "s-unknown", ActivityState = "Working", UncommittedCount = null },
        });

        var fresh = store.TryGetFresh(TenantId.Local, "dir-A", TimeSpan.FromSeconds(20));

        Assert.NotNull(fresh);
        Assert.Equal(12, Assert.Single(fresh, s => s.SessionId == "s-dirty").UncommittedCount);
        Assert.Equal(0, Assert.Single(fresh, s => s.SessionId == "s-clean").UncommittedCount);
        Assert.Null(Assert.Single(fresh, s => s.SessionId == "s-unknown").UncommittedCount);
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
