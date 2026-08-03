using System.Text.Json;
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
/// The 31 July load-test baseline measured the old shape exactly: 1,032 reads for 30 roster polls plus 13
/// sweeps over 8 sessions, (30 + 13) x 8 x 3 with no remainder, and named the registry's process-wide
/// monitor as the resource that gave first - roughly five concurrent viewers over 800 sessions, 104 of 111
/// threads blocked on it at the wedge.
///
/// TWO LOOPS IS THE POINT OF THE ASSERTION. The three reads sat in TWO loops - <c>HoldStateFor</c> and
/// <c>IsExpired</c> in the first, <c>SnoozeUntilFor</c> in a second further down the same method - so a
/// batch that fixed only the first would remove two reads of three and the load test would report a
/// two-thirds improvement that reads like success. These assert the count is ONE for the whole fold, not
/// "fewer than before": over four sessions the old shape cost twelve, a first-loop-only fix would cost
/// five, and only a fix reaching both loops costs one.
///
/// WHY THIS FILE IS IN THE LOCKED SUITE AND ITS CORRECTNESS SIBLINGS ARE NOT. Every fact here asserts a
/// DELTA on <c>LoadTestMetrics.snoozeDbReads</c>, which is process-global static state. The fast
/// <c>CcDirector.Gateway.UnitTests</c> assembly runs four collections at once and holds a dozen fold tests
/// that increment that same counter, so an exact delta measured there would be another test's reads added
/// to mine - a flaky assertion on this work item's headline proof. This suite disables parallelism, which
/// is what makes the delta meaningful. It is the same rule the suite split applied to the classes that
/// mutate process-wide environment variables: process-global state stays behind the lock.
///
/// The cost is real and is stated rather than hidden: these run on the <c>-Parked</c> release gate, not on
/// every default gate. The correctness facts they depend on - that the batched read answers exactly what
/// the three per-session reads answered - DO run on every gate, in
/// <c>CcDirector.Gateway.UnitTests/RosterFoldBatchedSnoozeReadTests</c>. And the count itself has a second,
/// independent instrument: the load test's Stage 0, which measured 42 reads for 42 folds against the
/// baseline's 1,032.
///
/// The counter asserted here is the same one the load test reads at <c>GET /diag/loadmetrics</c>
/// (<c>counters.snoozeDbReads</c>), read through the same JSON the endpoint serves - so a counter that
/// stopped being exposed would fail here rather than quietly leaving the load test blind.
/// </summary>
public sealed class RosterFoldSnoozeReadCountTests : IDisposable
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
