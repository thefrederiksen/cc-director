using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Diagnostics;
using CcDirector.Gateway.Snooze;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE ROSTER FOLD TAKES ONE SNOOZE DATABASE READ, NOT THREE PER SESSION (issue #2323, read-model epic
/// #1159).
///
/// The fold used to ask the snooze registry three separate questions per session - <c>HoldStateFor</c> and
/// <c>IsExpired</c> in its first loop, <c>SnoozeUntilFor</c> in a SECOND loop further down the same method -
/// and each one took the registry's process-wide monitor, rented its own pooled context and ran its own
/// query. The 31 July load-test baseline measured it exactly: 1,032 reads for 30 roster polls plus 13 sweeps
/// over 8 sessions, (30 + 13) x 8 x 3 with no remainder, and named that monitor as the resource that gave
/// first - roughly five concurrent viewers over 800 sessions, 104 of 111 threads blocked on it at the wedge.
///
/// TWO LOOPS IS THE POINT OF THE READ-COUNT ASSERTION. A batch that fixed only the first loop would remove
/// two reads of three and the load test would report a two-thirds improvement that reads like success. So
/// these tests assert the read count is ONE for the whole fold, not "fewer than before": over four sessions
/// the old shape cost twelve, a first-loop-only fix would cost five, and only a fix that reaches both loops
/// costs one.
///
/// The counter asserted here is the same one the load test reads at <c>GET /diag/loadmetrics</c>
/// (<c>counters.snoozeDbReads</c>), read through the same JSON the endpoint serves - so a counter that
/// stopped being exposed would fail here rather than quietly leaving the load test blind.
/// </summary>
public sealed class RosterFoldBatchedSnoozeReadTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase? _db;
    private GatewayDatabase Db => _db ??= _h.Open();

    private SnoozeRegistry NewReg() => new(Db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));

    public void Dispose() => _h.Dispose();

    /// <summary>Read one counter out of the load-test snapshot exactly as <c>/diag/loadmetrics</c> serves it.</summary>
    private static long Counter(string name)
    {
        var json = JsonSerializer.Serialize(LoadTestMetrics.Snapshot(reset: false));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("counters").GetProperty(name).GetInt64();
    }

    private static SessionDto Session(string sid) => new()
    {
        SessionId = sid,
        Agent = "ClaudeCode",
        RepoPath = "repo",
        ActivityState = "WaitingForInput",
        Status = "Running",
        StatusColor = "red",
        CreatedAt = DateTime.UtcNow,
        LastActivityAt = DateTime.UtcNow,
    };

    /// <summary>Run the shared fold over a set of sessions, as the roster and the display sweep both do.</summary>
    private static void Fold(SnoozeRegistry? reg, List<SessionDto> sessions)
        => GatewayEndpoints.StampFleetRolesAndFold(sessions, sessions, needsYouStampFor: null, snoozeRegistry: reg);

    [Fact]
    public void TheWholeFold_TakesExactlyOneSnoozeRead_HoweverManySessionsItStamps()
    {
        var reg = NewReg();
        var now = DateTime.UtcNow;
        reg.Snooze("s1", now.AddHours(2), "dir-1");     // armed, running
        reg.Snooze("s2", now.AddMinutes(-1), "dir-1");  // armed, elapsed
        reg.SnoozeDeferred("s3", 720, "dir-1");         // deferred, no clock yet
        // s4 has no entry at all.
        var sessions = new List<SessionDto> { Session("s1"), Session("s2"), Session("s3"), Session("s4") };

        var before = Counter("snoozeDbReads");
        Fold(reg, sessions);
        var reads = Counter("snoozeDbReads") - before;

        // ONE. Four sessions x three reads = twelve under the old shape; five if only the first loop had
        // been batched. Anything above one means a per-session read is back on the fold path.
        Assert.Equal(1L, reads);
    }

    [Fact]
    public void TheReadCounter_StillCountsEveryRead_SoTheOneAboveIsMeasuredRatherThanBroken()
    {
        // VALIDATE THE INSTRUMENT WHERE IT IS POINTED. "One read" is only evidence if this counter, at this
        // call site, can report something other than one - a counter that had stopped incrementing would
        // produce a very convincing zero and a nearly convincing one.
        var reg = NewReg();
        var sessions = new List<SessionDto> { Session("s1"), Session("s2") };

        var before = Counter("snoozeDbReads");
        Fold(reg, sessions);
        Fold(reg, sessions);
        Fold(reg, sessions);
        Assert.Equal(3L, Counter("snoozeDbReads") - before);   // three folds, three reads

        // And the per-session readers - the three the fold no longer calls - still count one read each, so
        // the counter has not stopped seeing the shape it was built to see.
        before = Counter("snoozeDbReads");
        reg.HoldStateFor("s1", DateTime.UtcNow);
        reg.IsExpired("s1", DateTime.UtcNow);
        reg.SnoozeUntilFor("s1");
        Assert.Equal(3L, Counter("snoozeDbReads") - before);
    }

    [Fact]
    public void AFoldOverNothing_ReadsNothing()
    {
        var reg = NewReg();
        var before = Counter("snoozeDbReads");

        Fold(reg, new List<SessionDto>());                              // no sessions at all
        Fold(reg, new List<SessionDto> { Session(""), Session("  ") }); // nothing that could match a row

        Assert.Equal(0L, Counter("snoozeDbReads") - before);
    }

    [Fact]
    public void EveryShapeOfRow_FoldsToTheSameAnswerItDidBefore()
    {
        // The four shapes a session can be in, and the three stamped facts for each. This is the behaviour
        // the batched read has to preserve exactly; the read count above is worthless if it buys a wrong
        // answer.
        var reg = NewReg();
        var now = DateTime.UtcNow;
        var running = now.AddHours(2);
        var elapsed = now.AddMinutes(-1);
        reg.Snooze("armed-running", running, "dir-1");
        reg.Snooze("armed-elapsed", elapsed, "dir-1");
        reg.SnoozeDeferred("deferred", 720, "dir-1");

        var sessions = new List<SessionDto>
        {
            Session("armed-running"), Session("armed-elapsed"), Session("deferred"), Session("absent"),
        };
        Fold(reg, sessions);
        var bySid = sessions.ToDictionary(s => s.SessionId, StringComparer.Ordinal);

        // Armed and running: held, not expired, deadline shown.
        Assert.Equal(HoldStates.Held, bySid["armed-running"].HoldState);
        Assert.False(bySid["armed-running"].SnoozeExpired);
        Assert.Equal(running, bySid["armed-running"].SnoozeUntil);

        // Armed and elapsed: the owner got their quiet, so the hold is over - and the badge says WHY it came
        // back. The deadline is still real and still shown.
        Assert.Equal(HoldStates.None, bySid["armed-elapsed"].HoldState);
        Assert.True(bySid["armed-elapsed"].SnoozeExpired);
        Assert.Equal(elapsed, bySid["armed-elapsed"].SnoozeUntil);

        // Deferred: asked for while the agent was working, so there is no clock yet and nothing to expire.
        Assert.Equal(HoldStates.DeferredHold, bySid["deferred"].HoldState);
        Assert.False(bySid["deferred"].SnoozeExpired);
        Assert.Null(bySid["deferred"].SnoozeUntil);

        // No row at all.
        Assert.Equal(HoldStates.None, bySid["absent"].HoldState);
        Assert.False(bySid["absent"].SnoozeExpired);
        Assert.Null(bySid["absent"].SnoozeUntil);
    }

    [Fact]
    public void ADeferredHold_IsNotTheSameAsNoHold_WhichIsTheDistinctionABatchCanLose()
    {
        // The trap in batching these reads is the map's type. A present row with a NULL deadline (a deferred
        // hold - the clock starts when the work ends) and an ABSENT row both carry "no deadline", so any
        // shape that cannot express present-with-null - a non-nullable value map, or a "zero means none"
        // convention - silently merges them, and a deferred hold reads on the phone as no hold at all.
        var reg = NewReg();
        reg.SnoozeDeferred("deferred", 720, "dir-1");
        var snapshot = reg.HoldSnapshotFor(new[] { "deferred", "absent" });

        Assert.Equal(1, snapshot.Count);                                             // only the row that exists
        Assert.Equal(HoldStates.DeferredHold, snapshot.HoldStateFor("deferred", DateTime.UtcNow));
        Assert.Equal(HoldStates.None, snapshot.HoldStateFor("absent", DateTime.UtcNow));
        Assert.Null(snapshot.SnoozeUntilFor("deferred"));
        Assert.Null(snapshot.SnoozeUntilFor("absent"));
    }

    [Fact]
    public void TheSnapshotAndThePerSessionReaders_CannotDisagree()
    {
        // Two ways of answering the same question is two authorities, and they drift. Both paths run the
        // deciders in SnoozeHoldSnapshot, and this walks every shape to say so out loud - including the
        // boundary instant, where "at or past the deadline" is the rule.
        var reg = NewReg();
        var now = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        reg.Snooze("armed-running", now.AddHours(2), "dir-1");
        reg.Snooze("armed-elapsed", now.AddMinutes(-1), "dir-1");
        reg.Snooze("armed-exactly-due", now, "dir-1");
        reg.SnoozeDeferred("deferred", 720, "dir-1");

        var ids = new[] { "armed-running", "armed-elapsed", "armed-exactly-due", "deferred", "absent" };
        var snapshot = reg.HoldSnapshotFor(ids);

        foreach (var sid in ids)
        {
            Assert.Equal(reg.HoldStateFor(sid, now), snapshot.HoldStateFor(sid, now));
            Assert.Equal(reg.IsExpired(sid, now), snapshot.IsExpired(sid, now));
            Assert.Equal(reg.SnoozeUntilFor(sid), snapshot.SnoozeUntilFor(sid));
        }

        // Named, so a reader can see the walk above covered a real spread rather than five nulls.
        Assert.Equal(HoldStates.Held, snapshot.HoldStateFor("armed-running", now));
        Assert.True(snapshot.IsExpired("armed-exactly-due", now));
        Assert.False(snapshot.IsExpired("deferred", now));
    }

    [Fact]
    public void TheReadAsksOnlyAboutTheSessionsBeingFolded()
    {
        // Scoped to the fold's own ids rather than reading the whole table. The table is not bounded - a
        // permanently retired Director's rows are never dropped - so a whole-table read on the hot path
        // would trade a measured ceiling for an unmeasured one.
        var reg = NewReg();
        var now = DateTime.UtcNow;
        reg.Snooze("mine", now.AddHours(1), "dir-1");
        reg.Snooze("someone-elses", now.AddHours(1), "dir-1");

        var snapshot = reg.HoldSnapshotFor(new[] { "mine" });

        Assert.Equal(1, snapshot.Count);
        Assert.Equal(HoldStates.Held, snapshot.HoldStateFor("mine", now));
        // Not asked about, so not answered for - and the caller gets the honest "no row here" rather than a
        // stale one.
        Assert.Equal(HoldStates.None, snapshot.HoldStateFor("someone-elses", now));
    }

    [Fact]
    public void TheSetBasedRead_IsScopedToItsTenant()
    {
        // A set-based read is exactly the shape that leaks across tenants if it escapes the scope: one query
        // returning rows for whoever happens to be in the table, instead of one query per session under the
        // caller's own context. It resolves the ambient tenant through GatewayDatabase.CreateContext and the
        // global query filter, the same as the three per-session reads it replaces - and this says so with
        // two tenants over ONE database file rather than trusting the mechanism.
        var alice = new TenantId(Guid.NewGuid().ToString("N"));
        var bob = new TenantId(Guid.NewGuid().ToString("N"));
        var aliceReg = new SnoozeRegistry(_h.Open(new FixedTenantContext(alice)),
            _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
        var bobReg = new SnoozeRegistry(_h.Open(new FixedTenantContext(bob)),
            _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));

        var now = DateTime.UtcNow;
        aliceReg.Snooze("shared-session-id", now.AddHours(3), "dir-1");

        // Bob asks about the very same session id and gets no row - not Alice's deadline, and not a hold.
        var bobsView = bobReg.HoldSnapshotFor(new[] { "shared-session-id" });
        Assert.Equal(0, bobsView.Count);
        Assert.Equal(HoldStates.None, bobsView.HoldStateFor("shared-session-id", now));
        Assert.Null(bobsView.SnoozeUntilFor("shared-session-id"));

        // And the row really is there for its owner, so the empty answer above is a partition rather than an
        // empty table.
        var alicesView = aliceReg.HoldSnapshotFor(new[] { "shared-session-id" });
        Assert.Equal(1, alicesView.Count);
        Assert.Equal(HoldStates.Held, alicesView.HoldStateFor("shared-session-id", now));
    }

    [Fact]
    public void AFoldWithNoRegistryAtAll_StampsNoHold_AndReadsNothing()
    {
        // The dev and diagnostic callers pass no registry. They used to get null out of `snoozeRegistry?.`;
        // they now get the empty snapshot, which must answer the same way.
        var sessions = new List<SessionDto> { Session("s1") };
        var before = Counter("snoozeDbReads");

        Fold(null, sessions);

        Assert.Equal(0L, Counter("snoozeDbReads") - before);
        Assert.Null(sessions[0].SnoozeUntil);
    }
}
