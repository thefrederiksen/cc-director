using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Screens;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Screens;

/// <summary>
/// ROWS 5 and 6 of the Terminal Rules phase 0 proofs
/// (<c>docs/missions/terminal-rules-2026-09-02/phase-0-proofs.md</c>): the three-fact freshness rule
/// ruling 1 settled, driven directly.
///
/// The rule is that a STORED screen may answer the live-truth question only when all three of these are
/// positively established - the byte mark equals the currently pushed count, the owning Director's tunnel
/// is CONNECTED at this instant, and that pushed snapshot is younger than
/// <see cref="GatewayScreenReader.LiveSnapshotBudget"/>. Byte equality alone is a check whose pass
/// condition is an ABSENCE: when the push stream freezes, the mark and the current value are equal
/// because nothing is ARRIVING, not because nothing changed.
///
/// EVERY TEST HERE ASSERTS WHAT THE READER DID RETURN, never only what it did not. "It did not serve the
/// stale screen" is satisfied by a reader that returns nothing at all, and by a fixture that was never
/// driven; so each case names the source it got and the reason string that decided it.
///
/// Time is moved by INJECTING THE CLOCK - both <see cref="GatewayScreenReader"/> and
/// <see cref="PushedSessionStore"/> take a <c>Func&lt;DateTime&gt;</c> seam. No sleeping, and the budget
/// constant is never edited: a test that moves the rule to make itself pass has stopped testing the rule.
///
/// <b>Proven against the mapped model, not the migrated schema, and proven from the store inwards with
/// the push path unexercised.</b> See <see cref="ScreenStoreTestDb"/>.
/// </summary>
[Collection(ScreenPullCounterCollection.Name)]
public class GatewayScreenReaderFreshnessTests
{
    private const string Tenant = "local";
    private const string DirectorId = "director-1";
    private const string SessionId = "11111111-1111-1111-1111-111111111111";
    private const string ConnectionId = "conn-1";

    private static readonly string[] StoredRows = { "STORED SCREEN LINE ONE", "stored line two" };
    private static readonly string[] LiveRows = { "LIVE TUNNEL LINE ONE", "live line two" };

    /// <summary>The clock both the reader and the pushed store read, moved by the test.</summary>
    private sealed class TestClock
    {
        public DateTime Now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Read() => Now;
    }

    /// <summary>A tunnel that answers <c>screen-grid</c> with a KNOWN-DIFFERENT screen, and counts how
    /// many times it was asked. Different rows on purpose: it is the only way to tell a store answer from
    /// a tunnel answer by content rather than by taking the reader's own label on trust.</summary>
    private sealed class FakeTunnel
    {
        public int Calls;
        public bool Answers = true;

        public DirectorCommandRouter.SendDirectorCommandAsync Send => (directorId, command, ct) =>
        {
            if (command.Verb == "screen-grid") Calls++;
            if (!Answers) return Task.FromResult<DirectorCommandResult?>(null);
            var body = new ScreenGridResponse
            {
                SessionId = command.SessionId,
                Rows = LiveRows.ToList(),
                CursorRow = 1,
                CursorCol = 2,
                CursorVisible = true,
                IsAlternateScreen = false,
                HasGrid = true,
            };
            return Task.FromResult<DirectorCommandResult?>(
                DirectorCommandResult.Success(SessionCommandSerialize(body)));
        };

        private static string SessionCommandSerialize(ScreenGridResponse body)
            => System.Text.Json.JsonSerializer.Serialize(body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    }

    private static ScreenPush StoredPush(long bufferBytes, DateTime capturedAt) => new()
    {
        SessionId = SessionId,
        CapturedAtUtc = capturedAt,
        Rows = StoredRows.ToList(),
        CursorRow = 3,
        CursorCol = 4,
        CursorVisible = true,
        IsAlternateScreen = false,
        HasGrid = true,
        BufferBytes = bufferBytes,
        ActivityState = "WaitingForInput",
        Agent = "ClaudeCode",
    };

    private static SessionDto Session(long totalBufferBytes) => new()
    {
        SessionId = SessionId,
        DirectorId = DirectorId,
        ActivityState = "WaitingForInput",
        TotalBufferBytes = totalBufferBytes,
    };

    private static SessionVerbClient Route(FakeTunnel tunnel)
        => new(new DirectorDto { DirectorId = DirectorId }, tunnel.Send);

    /// <summary>
    /// ROW 5. The three states in ONE run, so that the refusal cannot be a broken fixture: connected and
    /// fresh serves the STORE; the tunnel then dropping flips the live answer to UNREADABLE with a reason
    /// naming the connection; and the HISTORY answer is unaffected throughout.
    /// </summary>
    [Fact]
    public async Task A_dropped_tunnel_stops_the_store_answering_the_live_question_but_not_the_history_one()
    {
        using var db = new ScreenStoreTestDb();
        var store = db.StoreFor(Tenant);
        var clock = new TestClock();
        var pushed = new PushedSessionStore(clock.Read);
        var reader = new GatewayScreenReader(store, pushed, clock.Read);
        var tunnel = new FakeTunnel { Answers = false };   // the machine is gone: no tunnel answer either

        store.Append(DirectorId, StoredPush(bufferBytes: 500, capturedAt: clock.Now), clock.Now);
        pushed.RegisterConnection(new TenantId(Tenant), DirectorId, ConnectionId);
        pushed.ApplySnapshot(new TenantId(Tenant), DirectorId, ConnectionId, 1, new[] { Session(500) });

        // 1. POSITIVE: connected, fresh, byte mark equal - the store answers, with the STORED rows.
        var connected = await reader.ReadLiveAsync(new TenantId(Tenant), Route(tunnel), SessionId);
        Assert.Equal(ScreenSource.Store, connected.Source);
        Assert.Equal(StoredRows, connected.Grid!.Rows);
        Assert.Equal(0, tunnel.Calls);

        // 2. The tunnel drops. NOTHING else changes - no bytes written, no time skipped. The pushed byte
        //    count therefore stays at 500, which is exactly the state a byte-equality-only check passes on.
        pushed.UnregisterConnection(new TenantId(Tenant), DirectorId, ConnectionId);

        // 3. The live answer is now UNREADABLE, and it names the deciding fact rather than failing vaguely.
        var offline = await reader.ReadLiveAsync(new TenantId(Tenant), Route(tunnel), SessionId);
        Assert.Equal(ScreenSource.Unreadable, offline.Source);
        Assert.Null(offline.Grid);
        Assert.Contains("not connected", offline.Why);

        // 4. And the HISTORY answer is untouched by any of it - which is the whole point of the store.
        var history = reader.ReadStored(SessionId);
        Assert.NotNull(history);
        Assert.Equal(StoredRows, history!.Grid.Rows);
    }

    /// <summary>
    /// ROW 6, the negative control for the whole slice. A FROZEN push stream leaves the byte count equal
    /// because nothing is arriving; the reader must not read that as "the screen has not changed".
    /// The budget boundary is asserted from BOTH sides in the same test, or the row would pass on a rule
    /// that always refuses.
    /// </summary>
    [Fact]
    public async Task A_frozen_push_stream_does_not_certify_the_stale_screen_and_the_budget_holds_on_both_sides()
    {
        using var db = new ScreenStoreTestDb();
        var store = db.StoreFor(Tenant);
        var clock = new TestClock();
        var pushed = new PushedSessionStore(clock.Read);
        var reader = new GatewayScreenReader(store, pushed, clock.Read);
        var tunnel = new FakeTunnel();

        store.Append(DirectorId, StoredPush(bufferBytes: 500, capturedAt: clock.Now), clock.Now);
        pushed.RegisterConnection(new TenantId(Tenant), DirectorId, ConnectionId);
        pushed.ApplySnapshot(new TenantId(Tenant), DirectorId, ConnectionId, 1, new[] { Session(500) });

        // JUST INSIDE the budget: still certified from the store. This is the half that stops the test
        // passing on a rule that refuses everything.
        clock.Now += GatewayScreenReader.LiveSnapshotBudget - TimeSpan.FromSeconds(1);
        var inside = await reader.ReadLiveAsync(new TenantId(Tenant), Route(tunnel), SessionId);
        Assert.Equal(ScreenSource.Store, inside.Source);
        Assert.Equal(StoredRows, inside.Grid!.Rows);
        Assert.Equal(0, tunnel.Calls);

        // JUST OUTSIDE it - the stream stayed frozen, so the pushed count is STILL 500 and equal to the
        // mark. A byte-equality-only check passes here. This one must not.
        clock.Now += TimeSpan.FromSeconds(2);
        var outside = await reader.ReadLiveAsync(new TenantId(Tenant), Route(tunnel), SessionId);
        Assert.Equal(ScreenSource.Tunnel, outside.Source);
        Assert.Equal(LiveRows, outside.Grid!.Rows);          // the CURRENT screen, named by its content
        Assert.Contains("budget", outside.Why);
        Assert.Equal(1, tunnel.Calls);
    }

    /// <summary>
    /// ROW 6, second half: with the snapshot FRESH and the tunnel connected, a terminal that has moved by
    /// even one byte falls to the tunnel. Strict equality, not the dictation guard's threshold.
    /// </summary>
    [Fact]
    public async Task One_byte_of_movement_sends_the_live_read_to_the_tunnel()
    {
        using var db = new ScreenStoreTestDb();
        var store = db.StoreFor(Tenant);
        var clock = new TestClock();
        var pushed = new PushedSessionStore(clock.Read);
        var reader = new GatewayScreenReader(store, pushed, clock.Read);
        var tunnel = new FakeTunnel();

        store.Append(DirectorId, StoredPush(bufferBytes: 500, capturedAt: clock.Now), clock.Now);
        pushed.RegisterConnection(new TenantId(Tenant), DirectorId, ConnectionId);
        pushed.ApplySnapshot(new TenantId(Tenant), DirectorId, ConnectionId, 1, new[] { Session(500) });

        // The control first: unchanged at 500, the store answers.
        var unchanged = await reader.ReadLiveAsync(new TenantId(Tenant), Route(tunnel), SessionId);
        Assert.Equal(ScreenSource.Store, unchanged.Source);

        // ONE byte. Not the dictation guard's tolerance - one.
        pushed.ApplyDelta(new TenantId(Tenant), DirectorId, ConnectionId, 2, Session(501));
        var moved = await reader.ReadLiveAsync(new TenantId(Tenant), Route(tunnel), SessionId);
        Assert.Equal(ScreenSource.Tunnel, moved.Source);
        Assert.Equal(LiveRows, moved.Grid!.Rows);
        Assert.Contains("terminal has moved", moved.Why);
        Assert.Contains("500", moved.Why);
        Assert.Contains("501", moved.Why);
    }

    /// <summary>
    /// A session the Director no longer reports is not one anything current is known about. Without this
    /// the reader would certify a stored screen on the strength of a snapshot that does not mention the
    /// session at all - the byte comparison would simply never run.
    /// </summary>
    [Fact]
    public async Task A_session_absent_from_the_current_snapshot_is_not_certified()
    {
        using var db = new ScreenStoreTestDb();
        var store = db.StoreFor(Tenant);
        var clock = new TestClock();
        var pushed = new PushedSessionStore(clock.Read);
        var reader = new GatewayScreenReader(store, pushed, clock.Read);
        var tunnel = new FakeTunnel();

        store.Append(DirectorId, StoredPush(bufferBytes: 500, capturedAt: clock.Now), clock.Now);
        pushed.RegisterConnection(new TenantId(Tenant), DirectorId, ConnectionId);
        // A snapshot that names a DIFFERENT session - the Director is connected and talking, it simply is
        // not reporting this one.
        pushed.ApplySnapshot(new TenantId(Tenant), DirectorId, ConnectionId, 1, new[]
        {
            new SessionDto { SessionId = "22222222-2222-2222-2222-222222222222", DirectorId = DirectorId, TotalBufferBytes = 500 },
        });

        var read = await reader.ReadLiveAsync(new TenantId(Tenant), Route(tunnel), SessionId);
        Assert.Equal(ScreenSource.Tunnel, read.Source);
        Assert.Contains("not in the Director's current snapshot", read.Why);
    }

    /// <summary>
    /// With nothing stored, the live read is a plain tunnel pull - the behaviour every caller had before
    /// this store existed. This is also the known-bad control for the pull counter: it shows the tunnel
    /// CAN be reached from here, so a zero in the certified cases means something.
    /// </summary>
    [Fact]
    public async Task With_no_stored_screen_the_live_read_is_an_ordinary_tunnel_pull()
    {
        using var db = new ScreenStoreTestDb();
        var store = db.StoreFor(Tenant);
        var clock = new TestClock();
        var pushed = new PushedSessionStore(clock.Read);
        var reader = new GatewayScreenReader(store, pushed, clock.Read);
        var tunnel = new FakeTunnel();

        pushed.RegisterConnection(new TenantId(Tenant), DirectorId, ConnectionId);
        pushed.ApplySnapshot(new TenantId(Tenant), DirectorId, ConnectionId, 1, new[] { Session(500) });

        var before = SessionVerbClient.ScreenGridPulls;
        var read = await reader.ReadLiveAsync(new TenantId(Tenant), Route(tunnel), SessionId);
        var after = SessionVerbClient.ScreenGridPulls;

        Assert.Equal(ScreenSource.Tunnel, read.Source);
        Assert.Equal(LiveRows, read.Grid!.Rows);
        Assert.Contains("no screen stored", read.Why);
        // The counter MOVES. Without this the zero asserted elsewhere would be a number that cannot rise.
        Assert.Equal(1, after - before);
    }

    /// <summary>
    /// Nothing stored AND no tunnel answer is UNREADABLE, returned as unreadable with a null grid - never
    /// as a screen. Every caller's fail-closed branch reads that null exactly as it always did.
    /// </summary>
    [Fact]
    public async Task Nothing_stored_and_no_tunnel_answer_is_unreadable()
    {
        using var db = new ScreenStoreTestDb();
        var clock = new TestClock();
        var reader = new GatewayScreenReader(db.StoreFor(Tenant), new PushedSessionStore(clock.Read), clock.Read);

        var read = await reader.ReadLiveAsync(new TenantId(Tenant), Route(new FakeTunnel { Answers = false }), SessionId);

        Assert.Equal(ScreenSource.Unreadable, read.Source);
        Assert.Null(read.Grid);
        Assert.Contains("no tunnel answer", read.Why);
    }
}
