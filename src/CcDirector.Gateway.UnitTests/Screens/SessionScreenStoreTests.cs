using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Screens;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Screens;

/// <summary>
/// Phase 0 of the Terminal Rules mission (<c>docs/missions/terminal-rules-2026-09-02/brief.md</c>): the
/// store every Gateway screen read goes through. These pin the properties the reader and the later phases
/// lean on - a stored screen that comes back whole, an idempotent append, the newest capture, the
/// per-session cap that trims at write time, refusal of a push that disagrees with itself, seven-day
/// retention that leaves live rows alone, and a capture time that survives the round trip.
///
/// WHAT THESE RESULTS DO AND DO NOT SAY, and it travels with every one of them. The store's own rows run
/// on <see cref="ScreenStoreTestDb"/>, whose tables are built from the mapped MODEL rather than by the
/// migration. That was once a real gap; it is now a narrow one, because the <c>session_screens</c>
/// migration exists on this branch and the model and the migration are known to AGREE - a disagreement is
/// exactly what EF's pending-model-changes check reports, and every database-backed test in this suite
/// opens a migrated <see cref="CcDirector.Gateway.Data.GatewayDatabase"/> through that check and passes.
/// The sweep row below opens one directly.
///
/// The limit that does remain: these seed the store BY HAND. Not one of them drives the Director capture
/// through the sink and the hub, so they say the store behaves correctly WHEN HANDED a screen, and nothing
/// about who hands it one.
/// </summary>
public sealed class SessionScreenStoreTests : IDisposable
{
    private readonly ScreenStoreTestDb _db = new();
    private static readonly DateTime Now = new(2026, 9, 2, 20, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _db.Dispose();

    private SessionScreenStore Store(string tenant = "local") => _db.StoreFor(tenant);

    /// <summary>A well-formed push. Every malformed case below starts from one of these and breaks exactly
    /// one thing, so the fault under test is the only difference from a push that is known to be accepted.</summary>
    private static ScreenPush Push(DateTime capturedAt, params string[] rows) => new()
    {
        SessionId = "s1",
        CapturedAtUtc = capturedAt,
        Rows = rows.Length == 0 ? new List<string> { "$ " } : rows.ToList(),
        CursorRow = 2,
        CursorCol = 7,
        CursorVisible = true,
        IsAlternateScreen = true,
        HasGrid = true,
        BufferBytes = 4096,
        ActivityState = "WaitingForInput",
        Agent = "ClaudeCode",
    };

    /// <summary>How many screens the session currently holds. The cap bounds a session at
    /// <see cref="SessionScreenStore.MaxScreensPerSession"/>, which is also the most one read returns, so
    /// this counts everything a capped session can hold.</summary>
    private static int Count(SessionScreenStore store, string sessionId)
        => store.ReadRecent(sessionId, SessionScreenStore.MaxScreensPerSession).Count;

    [Fact]
    public void Append_AWellFormedPush_IsReadBackWithEveryFieldIntact()
    {
        var store = Store();
        var capturedAt = Now.AddMinutes(-3);

        var added = store.Append("d1", Push(capturedAt, "NOTICE: you have reached your limit", "> "), Now);

        Assert.True(added);
        var stored = store.ReadLatest("s1");
        Assert.NotNull(stored);
        Assert.Equal("s1", stored.SessionId);
        Assert.Equal(capturedAt, stored.CapturedAtUtc);
        Assert.Equal(DateTimeKind.Utc, stored.CapturedAtUtc.Kind);
        Assert.Equal("d1", stored.DirectorId);
        Assert.Equal(4096, stored.BufferBytes);
        Assert.Equal("WaitingForInput", stored.ActivityState);
        Assert.Equal("ClaudeCode", stored.Agent);
        Assert.Equal(new[] { "NOTICE: you have reached your limit", "> " }, stored.Grid.Rows);
        Assert.Equal("s1", stored.Grid.SessionId);
        Assert.Equal(2, stored.Grid.CursorRow);
        Assert.Equal(7, stored.Grid.CursorCol);
        Assert.True(stored.Grid.CursorVisible);
        Assert.True(stored.Grid.IsAlternateScreen);
        Assert.True(stored.Grid.HasGrid);
    }

    [Fact]
    public void Append_TheSameCaptureTwice_StoresNothingTheSecondTime_AndLeavesOneRow()
    {
        // A Director re-sends after a reconnect. The (tenant, session, captured-at) key makes that one row,
        // and the second call says so rather than throwing.
        var store = Store();
        var capturedAt = Now.AddMinutes(-1);

        Assert.True(store.Append("d1", Push(capturedAt, "first"), Now));
        var again = store.Append("d1", Push(capturedAt, "first"), Now.AddSeconds(30));

        Assert.False(again);
        Assert.Equal(1, Count(store, "s1"));
        Assert.Equal(new[] { "first" }, store.ReadLatest("s1")!.Grid.Rows);
    }

    [Fact]
    public void Append_TwoDirectorsCapturingOneSessionAtTheSameInstant_KeepsBothRows()
    {
        // Inspection 01, finding 3. The key used to be (tenant, session, captured-at), which carries no
        // Director - so two Directors that captured the same session id in the same millisecond collided,
        // and the second row was answered "already stored" and silently lost. That is a same-tenant
        // ownership defect, not a cross-account one: the tenant filter was never in question.
        //
        // Both halves are asserted POSITIVELY. Two rows exist, each naming its own Director and carrying
        // its own rows - a store that simply stopped de-duplicating would pass that half alone, so the
        // idempotency control below is part of the same test.
        var store = Store();
        var capturedAt = Now.AddMinutes(-1);

        Assert.True(store.Append("director-1", Push(capturedAt, "DIRECTOR ONE SCREEN"), Now));
        Assert.True(store.Append("director-2", Push(capturedAt, "DIRECTOR TWO SCREEN"), Now));

        var held = store.ReadRecent("s1", 10);
        Assert.Equal(2, held.Count);
        var one = Assert.Single(held, s => s.DirectorId == "director-1");
        var two = Assert.Single(held, s => s.DirectorId == "director-2");
        Assert.Equal(new[] { "DIRECTOR ONE SCREEN" }, one.Grid.Rows);
        Assert.Equal(new[] { "DIRECTOR TWO SCREEN" }, two.Grid.Rows);

        // THE CONTROL. Idempotency is what the capture time is in the key for, and it must still hold: the
        // SAME Director re-sending the SAME capture after a reconnect is still one row, not a third.
        Assert.False(store.Append("director-1", Push(capturedAt, "DIRECTOR ONE SCREEN"), Now.AddSeconds(30)));
        Assert.Equal(2, Count(store, "s1"));
    }

    [Fact]
    public void ReadLatest_ReturnsTheNewestCapture_AndReadRecent_ReturnsThemNewestFirst()
    {
        var store = Store();
        // Deliberately appended out of order, so the read is doing the ordering and not the insert order.
        store.Append("d1", Push(Now.AddMinutes(-2), "middle"), Now);
        store.Append("d1", Push(Now.AddMinutes(-3), "oldest"), Now);
        store.Append("d1", Push(Now.AddMinutes(-1), "newest"), Now);

        Assert.Equal(new[] { "newest" }, store.ReadLatest("s1")!.Grid.Rows);
        var recent = store.ReadRecent("s1", 10);
        Assert.Equal(new[] { "newest", "middle", "oldest" }, recent.Select(s => s.Grid.Rows[0]));
        Assert.Equal(
            new[] { Now.AddMinutes(-1), Now.AddMinutes(-2), Now.AddMinutes(-3) },
            recent.Select(s => s.CapturedAtUtc));
    }

    [Fact]
    public void Append_PastThePerSessionCap_TrimsTheOldest_AndKeepsTheNewest()
    {
        // Retention alone is not a bound: a session that ends a hundred turns an hour would hold seven days
        // of them. The cap is applied at WRITE time, inside the push transaction.
        var store = Store();
        const int over = 3;
        var captures = Enumerable.Range(0, SessionScreenStore.MaxScreensPerSession + over)
            .Select(i => Now.AddMinutes(-(SessionScreenStore.MaxScreensPerSession + over) + i))
            .ToList();
        foreach (var capturedAt in captures)
            Assert.True(store.Append("d1", Push(capturedAt, "row " + capturedAt.Ticks), Now));

        var held = store.ReadRecent("s1", SessionScreenStore.MaxScreensPerSession);

        Assert.Equal(SessionScreenStore.MaxScreensPerSession, held.Count);
        // The three oldest are gone...
        var times = held.Select(s => s.CapturedAtUtc).ToHashSet();
        foreach (var trimmed in captures.Take(over))
            Assert.DoesNotContain(trimmed, times);
        // ...the oldest SURVIVOR is the one immediately after them, so the cut was at the boundary and not
        // deeper, and the newest is still there and is still what ReadLatest answers with.
        Assert.Equal(captures[over], held[^1].CapturedAtUtc);
        Assert.Equal(captures[^1], held[0].CapturedAtUtc);
        Assert.Equal(captures[^1], store.ReadLatest("s1")!.CapturedAtUtc);
    }

    [Theory]
    [InlineData("empty-session-id")]
    [InlineData("empty-director-id")]
    [InlineData("no-capture-time")]
    [InlineData("negative-buffer-bytes")]
    [InlineData("null-rows")]
    [InlineData("null-row")]
    [InlineData("too-many-rows")]
    [InlineData("row-too-long")]
    [InlineData("grid-claimed-with-no-rows")]
    public void Append_APushThatDisagreesWithItself_IsRefused_WithAMessageSayingWhatWasWrong(string fault)
    {
        // The known-BAD inputs. The positive control is Append_AWellFormedPush_IsReadBackWithEveryFieldIntact
        // above: the same push shape, unbroken, is accepted and read back - so a refusal here is this fault
        // being caught and not the store refusing everything.
        var store = Store();
        var push = Push(Now.AddMinutes(-1), "hello");
        string expected;
        switch (fault)
        {
            case "empty-session-id":
                push.SessionId = "";
                expected = "push.SessionId";
                break;
            case "empty-director-id":
                expected = "directorId";
                break;
            case "no-capture-time":
                push.CapturedAtUtc = default;
                expected = "CapturedAtUtc is not set";
                break;
            case "negative-buffer-bytes":
                push.BufferBytes = -1;
                expected = "BufferBytes is negative";
                break;
            case "null-rows":
                push.Rows = null!;
                expected = "Rows is null";
                break;
            case "null-row":
                push.Rows = new List<string> { "ok", null! };
                expected = "row 1 is null";
                break;
            case "too-many-rows":
                push.Rows = Enumerable.Repeat("x", SessionScreenStore.MaxRows + 1).ToList();
                expected = $"at most {SessionScreenStore.MaxRows} rows; this one carries {SessionScreenStore.MaxRows + 1}";
                break;
            case "row-too-long":
                push.Rows = new List<string> { new('x', SessionScreenStore.MaxRowLength + 1) };
                expected = $"row 0 is {SessionScreenStore.MaxRowLength + 1} characters; the limit is {SessionScreenStore.MaxRowLength}";
                break;
            case "grid-claimed-with-no-rows":
                push.Rows = new List<string>();
                push.HasGrid = true;
                expected = "HasGrid is true but no rows were sent";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault));
        }
        var directorId = fault == "empty-director-id" ? "" : "d1";

        var thrown = Assert.Throws<ArgumentException>(() => store.Append(directorId, push, Now));

        Assert.Contains(expected, thrown.Message, StringComparison.Ordinal);
        Assert.Null(store.ReadLatest("s1"));   // and nothing was written
    }

    [Fact]
    public void PurgeOlderThan_RemovesRowsReceivedBeforeTheCutoff_AndLeavesLaterOnes()
    {
        // The survivor is the control. A purge that deleted everything - the easiest way for this to be
        // wrong - fails on the second half, not on the first.
        var store = Store();
        store.Append("d1", Push(Now.AddDays(-9), "expired"), Now.AddDays(-9));
        store.Append("d1", Push(Now.AddDays(-1), "live"), Now.AddDays(-1));

        var removed = store.PurgeOlderThan(Now.AddDays(-7));

        Assert.Equal(1, removed);
        Assert.Equal(1, Count(store, "s1"));
        var survivor = store.ReadLatest("s1");
        Assert.NotNull(survivor);
        Assert.Equal(Now.AddDays(-1), survivor.CapturedAtUtc);
        Assert.Equal(new[] { "live" }, survivor.Grid.Rows);
        // And it is not a method that always answers 1: with nothing left to expire it answers 0.
        Assert.Equal(0, store.PurgeOlderThan(Now.AddDays(-7)));
    }

    [Fact]
    public async Task SweepAsync_RemovesScreensPastTheSevenDayRetention_AndReturnsHowManyItRemoved()
    {
        // PurgeOlderThan above proves the store's cut. This proves the thing that actually RUNS it in the
        // Gateway, and that it reports a NUMBER rather than reporting that a method was called - the number
        // is what a proof run quotes. The two rows sit either side of the seven-day retention: eight days
        // goes, six days is the control and stays.
        //
        // Self-host boundary, so the body runs exactly once under the local tenant and the tenant census is
        // never enumerated; the registry is here because the base requires one, not because it is read.
        using var registryDb = new GatewayDbTestHarness();
        var store = Store();
        store.Append("d1", Push(Now.AddDays(-8), "expired"), Now.AddDays(-8));
        store.Append("d1", Push(Now.AddDays(-6), "live"), Now.AddDays(-6));
        var sweep = new SessionScreenSweep(
            new HostedTenantBoundary(new SingleTenantContext(), new DeviceRegistry()),
            new TenantRegistry(registryDb.Open()),
            store,
            () => Now);

        var removed = await sweep.SweepAsync();

        Assert.Equal(1, removed);
        Assert.Equal(1, Count(store, "s1"));
        var survivor = store.ReadLatest("s1");
        Assert.NotNull(survivor);
        Assert.Equal(Now.AddDays(-6), survivor.CapturedAtUtc);
        Assert.Equal(new[] { "live" }, survivor.Grid.Rows);
        // And the return is a count and not a constant: a second pass with nothing left to expire answers 0,
        // and the six-day row is still there afterwards.
        Assert.Equal(0, await sweep.SweepAsync());
        Assert.Equal(new[] { "live" }, store.ReadLatest("s1")!.Grid.Rows);
    }

    [Fact]
    public void Append_ACaptureTimeWithSubMillisecondTicks_IsStored_AndFoundAgainByReadLatest()
    {
        // Postgres keeps microseconds and .NET keeps hundred-nanosecond ticks. A time written at full
        // precision could not be found again by the exact-match duplicate check on one provider and could on
        // the other, so both sides are pinned to whole milliseconds. The ROUND TRIP is the assertion.
        var store = Store();
        var ragged = Now.AddMinutes(-4).AddTicks(7777);

        Assert.True(store.Append("d1", Push(ragged, "ragged"), Now));

        var stored = store.ReadLatest("s1");
        Assert.NotNull(stored);
        Assert.Equal(SessionScreenStore.CapturePrecision(ragged), stored.CapturedAtUtc);
        Assert.Equal(0, stored.CapturedAtUtc.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(new[] { "ragged" }, stored.Grid.Rows);
        // And the same ragged time re-sent is recognised as the row already held, rather than stored twice
        // at a precision the lookup cannot match.
        Assert.False(store.Append("d1", Push(ragged, "ragged"), Now));
        Assert.Equal(1, Count(store, "s1"));
    }

    [Fact]
    public void ReadLatest_FromAnotherTenant_AnswersItsOwnRow_AndNeverTheFirstTenants()
    {
        // The partition proof, and the positive line comes FIRST: tenant A's store ANSWERS, so the null B
        // gets is the filter refusing and not a fixture that stored nothing.
        var a = Store("tenant-a");
        var b = Store("tenant-b");
        a.Append("d1", Push(Now.AddMinutes(-1), "tenant a screen"), Now);

        Assert.Equal(new[] { "tenant a screen" }, a.ReadLatest("s1")!.Grid.Rows);
        Assert.Null(b.ReadLatest("s1"));
        Assert.Empty(b.ReadRecent("s1", 10));

        // B storing its own row under the SAME session id gets its own row back, and A still gets A's.
        b.Append("d1", Push(Now.AddMinutes(-1), "tenant b screen"), Now);
        Assert.Equal(new[] { "tenant b screen" }, b.ReadLatest("s1")!.Grid.Rows);
        Assert.Equal(new[] { "tenant a screen" }, a.ReadLatest("s1")!.Grid.Rows);
    }
}
