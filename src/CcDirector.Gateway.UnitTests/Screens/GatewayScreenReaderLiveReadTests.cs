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
/// (<c>docs/missions/terminal-rules-2026-09-02/phase-0-proofs.md</c>), REWRITTEN by the fix round for
/// inspection 01, finding 1 (<c>rulings/r12-the-fix-round.md</c>).
///
/// WHAT CHANGED AND WHY, because the old version of this file is the reason nobody noticed. It asserted
/// that a stored screen IS served as the live answer while three freshness facts hold, and one of those
/// facts - the pushed byte count - is not refreshed when the terminal moves. So the shipped negative
/// control encoded the bug: it froze the pushed count at 500, moved the clock to nineteen seconds, and
/// required the reader to hand back the OLD rows while a ready tunnel held different ones.
///
/// THE PRINCIPLE THAT SETTLED IT: a certification may only rest on a signal that is refreshed by the
/// event it claims to detect. The byte count claims "the terminal has not moved since capture" and is
/// refreshed by a ten-second snapshot timer and some activity transitions, never by a terminal write. So
/// it cannot establish that, and no amount of connection state or snapshot age repairs it - those answer
/// different questions.
///
/// THE RESOLUTION: the store no longer answers the live question at all.
/// <see cref="GatewayScreenReader.ReadLiveAsync"/> always goes to the tunnel;
/// <see cref="GatewayScreenReader.ReadStored"/> keeps serving history, which is the half the mission was
/// for and which is untouched. The live half only ever ran while the owning Director's tunnel was
/// CONNECTED - which is exactly when the tunnel could have answered - so it never bought availability,
/// only latency on a connection that was already up. An optimisation that cannot be made sound is
/// dropped, not weakened.
///
/// EVERY TEST HERE ASSERTS WHAT THE READER DID RETURN, never only what it did not. "It did not serve the
/// stale screen" is satisfied by a reader that returns nothing at all, and by a fixture that was never
/// driven; so each case names the source it got, the rows it got BY CONTENT, and the tunnel call count.
///
/// <b>Proven against the mapped model, not the migrated schema, and proven from the store inwards with
/// the push path unexercised.</b> See <see cref="ScreenStoreTestDb"/>.
/// </summary>
[Collection(ScreenPullCounterCollection.Name)]
public class GatewayScreenReaderLiveReadTests
{
    private const string Tenant = "local";
    private const string DirectorId = "director-1";
    private const string OtherDirectorId = "director-2";
    private const string SessionId = "11111111-1111-1111-1111-111111111111";
    private const string ConnectionId = "conn-1";

    private static readonly string[] StoredRows = { "STORED SCREEN LINE ONE", "stored line two" };
    private static readonly string[] LiveRows = { "LIVE TUNNEL LINE ONE", "live line two" };

    /// <summary>The clock the pushed store reads, moved by the test.</summary>
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
                DirectorCommandResult.Success(Serialize(body)));
        };

        private static string Serialize(ScreenGridResponse body)
            => System.Text.Json.JsonSerializer.Serialize(body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    }

    private static ScreenPush StoredPush(long bufferBytes, DateTime capturedAt, IReadOnlyList<string>? rows = null) => new()
    {
        SessionId = SessionId,
        CapturedAtUtc = capturedAt,
        Rows = (rows ?? StoredRows).ToList(),
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
    /// THE NEGATIVE CONTROL FOR THE WHOLE SLICE, rewritten to FORBID the stale serve rather than assert
    /// it. This is the strongest possible temptation: a screen is stored, the owning Director's tunnel is
    /// connected, its snapshot was pushed one second ago, and the pushed byte count is EXACTLY the mark
    /// taken at capture. Every one of the three facts the old rule certified on is satisfied.
    ///
    /// The reader must STILL pull the live screen down the tunnel, and the assertion is on CONTENT - the
    /// tunnel's rows differ from the stored rows, so this cannot pass on a reader that happened to return
    /// the right label with the wrong screen. The tunnel call count is asserted too, so a reader that
    /// answered from the store and merely relabelled its answer is caught.
    /// </summary>
    [Fact]
    public async Task The_store_never_answers_the_live_question_even_when_every_freshness_fact_holds()
    {
        using var db = new ScreenStoreTestDb();
        var store = db.StoreFor(Tenant);
        var clock = new TestClock();
        var pushed = new PushedSessionStore(clock.Read);
        var reader = new GatewayScreenReader(store);
        var tunnel = new FakeTunnel();

        store.Append(DirectorId, StoredPush(bufferBytes: 500, capturedAt: clock.Now), clock.Now);
        pushed.RegisterConnection(new TenantId(Tenant), DirectorId, ConnectionId);
        pushed.ApplySnapshot(new TenantId(Tenant), DirectorId, ConnectionId, 1, new[] { Session(500) });
        clock.Now += TimeSpan.FromSeconds(1);     // a snapshot one second old: as fresh as it ever gets

        var live = await reader.ReadLiveAsync(Route(tunnel), SessionId);

        Assert.Equal(ScreenSource.Tunnel, live.Source);
        Assert.Equal(LiveRows, live.Grid!.Rows);          // the CURRENT screen, named by its content
        Assert.NotEqual(StoredRows, live.Grid!.Rows);     // and demonstrably not the stored one
        Assert.Equal(1, tunnel.Calls);

        // The HISTORY answer is untouched by any of it, in the same run - so this test cannot be passed by
        // a reader that has simply stopped using the store.
        var history = reader.ReadStored(SessionId);
        Assert.NotNull(history);
        Assert.Equal(StoredRows, history!.Grid.Rows);
    }

    /// <summary>
    /// Inspection 01's cross-Director repro, installed permanently. A row captured by director-2 must
    /// never come back to a live read routed to director-1. It cannot now, because no live read is
    /// answered from the store at all - but the case is the one that was demonstrated against the shipped
    /// code, so it is asserted here rather than left to be re-derived from the reader's structure.
    /// </summary>
    [Fact]
    public async Task A_row_captured_by_another_Director_is_never_returned_to_a_live_read_routed_elsewhere()
    {
        using var db = new ScreenStoreTestDb();
        var store = db.StoreFor(Tenant);
        var clock = new TestClock();
        var pushed = new PushedSessionStore(clock.Read);
        var reader = new GatewayScreenReader(store);
        var tunnel = new FakeTunnel();

        // The stored row belongs to a DIFFERENT Director, with the same session id and the same byte total.
        store.Append(OtherDirectorId, StoredPush(bufferBytes: 500, capturedAt: clock.Now), clock.Now);
        pushed.RegisterConnection(new TenantId(Tenant), DirectorId, ConnectionId);
        pushed.ApplySnapshot(new TenantId(Tenant), DirectorId, ConnectionId, 1, new[] { Session(500) });

        // The history read finds it and NAMES its owner - the positive half, so a fixture that stored
        // nothing cannot pass this test.
        var history = reader.ReadStored(SessionId);
        Assert.NotNull(history);
        Assert.Equal(OtherDirectorId, history!.DirectorId);

        // The live read, routed to director-1, gets the tunnel - not director-2's rows.
        var live = await reader.ReadLiveAsync(Route(tunnel), SessionId);
        Assert.Equal(ScreenSource.Tunnel, live.Source);
        Assert.Equal(LiveRows, live.Grid!.Rows);
        Assert.Equal(1, tunnel.Calls);
    }

    /// <summary>
    /// ROW 5. The machine goes away: the live question becomes UNREADABLE with a reason, and the history
    /// question is unaffected - which is the entire point of storing the screen. Both halves in one run,
    /// with the positive first, so the refusal cannot be a broken fixture.
    /// </summary>
    [Fact]
    public async Task A_dropped_tunnel_makes_the_live_question_unreadable_but_not_the_history_one()
    {
        using var db = new ScreenStoreTestDb();
        var store = db.StoreFor(Tenant);
        var clock = new TestClock();
        var reader = new GatewayScreenReader(store);

        store.Append(DirectorId, StoredPush(bufferBytes: 500, capturedAt: clock.Now), clock.Now);

        // 1. POSITIVE: with the tunnel answering, the live read produces the live screen.
        var up = new FakeTunnel();
        var served = await reader.ReadLiveAsync(Route(up), SessionId);
        Assert.Equal(ScreenSource.Tunnel, served.Source);
        Assert.Equal(LiveRows, served.Grid!.Rows);

        // 2. The machine is gone: the tunnel does not answer. UNREADABLE, with a null grid and a reason -
        //    never a stored screen dressed up as a live one.
        var down = new FakeTunnel { Answers = false };
        var offline = await reader.ReadLiveAsync(Route(down), SessionId);
        Assert.Equal(ScreenSource.Unreadable, offline.Source);
        Assert.Null(offline.Grid);
        Assert.Contains("no tunnel answer", offline.Why);
        Assert.Equal(1, down.Calls);

        // 3. And the HISTORY answer still works with the machine gone.
        var history = reader.ReadStored(SessionId);
        Assert.NotNull(history);
        Assert.Equal(StoredRows, history!.Grid.Rows);
    }

    /// <summary>
    /// The process-wide tunnel counter MOVES for a live read. Without this, a zero asserted anywhere else
    /// would be a number that cannot rise - which is a broken instrument, not a clean result.
    /// </summary>
    [Fact]
    public async Task A_live_read_moves_the_process_wide_tunnel_pull_counter()
    {
        using var db = new ScreenStoreTestDb();
        var reader = new GatewayScreenReader(db.StoreFor(Tenant));
        var tunnel = new FakeTunnel();

        var before = SessionVerbClient.ScreenGridPulls;
        var read = await reader.ReadLiveAsync(Route(tunnel), SessionId);
        var after = SessionVerbClient.ScreenGridPulls;

        Assert.Equal(ScreenSource.Tunnel, read.Source);
        Assert.Equal(LiveRows, read.Grid!.Rows);
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
        var reader = new GatewayScreenReader(db.StoreFor(Tenant));

        var read = await reader.ReadLiveAsync(Route(new FakeTunnel { Answers = false }), SessionId);

        Assert.Equal(ScreenSource.Unreadable, read.Source);
        Assert.Null(read.Grid);
        Assert.Contains("no tunnel answer", read.Why);
    }
}
