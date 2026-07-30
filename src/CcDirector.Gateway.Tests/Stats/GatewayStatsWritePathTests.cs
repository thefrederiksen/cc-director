using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Stats;

/// <summary>
/// The write path's batch semantics, provider-independent, plus the end-to-end replay property on the
/// self-host SQLite store.
///
/// These are the facts the port had to preserve when the statements moved to Entity Framework: one batch is
/// one tenant, an IDLE poll writes nothing at all, and replaying one snapshot any number of times leaves the
/// same numbers as replaying it once. The real-PostgreSQL half - no lost update between interleaved writers -
/// is <see cref="GatewayStatsWritePathPostgresTests"/>, because a lost update cannot be demonstrated on a
/// single-writer SQLite file at all.
/// </summary>
public sealed class GatewayStatsWritePathTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public GatewayStatsWritePathTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-stats-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "gateway-stats.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    /// <summary>
    /// A context factory that refuses to hand out a context. An empty batch must not reach it: the writer has
    /// to decide there is nothing to do BEFORE it creates a context or opens a transaction, and this is the
    /// only way to prove it did - counting statements afterwards would not distinguish "wrote nothing" from
    /// "opened a transaction and committed nothing", which is a round trip a poll every few seconds cannot
    /// afford.
    /// </summary>
    private sealed class RefusingContextFactory : IDbContextFactory<GatewayStatsDbContext>
    {
        public GatewayStatsDbContext CreateDbContext() =>
            throw new InvalidOperationException("The writer created a context for a batch with nothing in it.");
    }

    [Fact]
    public void EmptyBatch_WritesNothing_AndNeverOpensAContext()
    {
        var writer = new GatewayStatsWriter(new RefusingContextFactory());
        var batch = new StatsWriteBatch(TenantId.Local, DateTime.UtcNow, "2026-07-30T12");

        Assert.True(batch.IsEmpty);
        var minted = writer.Commit(batch, (_, _) => throw new InvalidOperationException("nothing to resolve"));

        Assert.Empty(minted);
        Assert.Equal(0, writer.StatementsExecuted);
    }

    [Fact]
    public void IdlePoll_OfAnUnchangedRoster_ExecutesNoStatements()
    {
        using var agg = new GatewayInputStatsAggregator(_path);
        var session = Session("s1", ("typed", "phone", 4, 90));

        agg.ObserveSnapshot(new[] { session });
        var afterFirstFold = agg.StatementsExecuted;
        Assert.True(afterFirstFold > 0, "the first fold must write something");

        // The same roster again, and again: nothing has changed, so the mirror answers "already recorded"
        // and the store is not touched at all.
        agg.ObserveSnapshot(new[] { session });
        agg.ObserveSnapshot(new[] { session });

        Assert.Equal(afterFirstFold, agg.StatementsExecuted);
    }

    [Fact]
    public void ReplayingOneSnapshotTenTimes_EqualsReplayingItOnce()
    {
        var snapshot = new[]
        {
            Session("s1", ("typed", "phone", 4, 90), ("voice", "phone", 2, 30)),
            Session("s2", ("typed", "desktop", 7, 210)),
        };

        long onceTurns, onceChars, onceWingman;
        var oncePath = Path.Combine(_dir, "once.db");
        using (var once = new GatewayInputStatsAggregator(oncePath))
        {
            once.ObserveSnapshot(snapshot);
            (onceTurns, onceChars) = Totals(once);
            onceWingman = once.WingmanUsage().Sessions;
        }

        using var ten = new GatewayInputStatsAggregator(_path);
        for (var i = 0; i < 10; i++) ten.ObserveSnapshot(snapshot);
        var (tenTurns, tenChars) = Totals(ten);

        Assert.Equal(onceTurns, tenTurns);
        Assert.Equal(onceChars, tenChars);
        Assert.Equal(onceWingman, ten.WingmanUsage().Sessions);
        Assert.Equal(13, tenTurns);
    }

    /// <summary>
    /// The reset rule the high-water comparison must NOT have changed: a reported count LOWER than the last
    /// one seen means a Director restarted that session id and is counting fresh from zero, so the whole
    /// current count is new activity. It lives in the fold, against the in-memory mirror - not in the stored
    /// row, which is a floor - and this is the test that says so.
    /// </summary>
    [Fact]
    public void ADroppedCount_StillFoldsAsFreshActivity_AfterThePortToUpserts()
    {
        using var agg = new GatewayInputStatsAggregator(_path);

        agg.Observe(Session("s1", ("typed", "phone", 10, 300)));
        agg.Observe(Session("s1", ("typed", "phone", 3, 40)));  // the Director restarted this session id

        var (turns, chars) = Totals(agg);
        Assert.Equal(13, turns);
        Assert.Equal(340, chars);
    }

    /// <summary>A model a Director never named is stored as SQL NULL, not as an identity spelled "". The port
    /// moved that through Entity Framework's nullable mapping instead of a DBNull parameter, so it is worth
    /// one test that the null bucket is still a real, separate bucket.</summary>
    [Fact]
    public void AnUnnamedModel_StaysNull_AndNeverBecomesAnIdentity()
    {
        using var agg = new GatewayInputStatsAggregator(_path);

        var unnamed = Session("s1", ("typed", "phone", 2, 20));
        var named = Session("s2", ("typed", "phone", 3, 30));
        named.CurrentModel = "claude-opus-5";
        agg.ObserveSnapshot(new[] { unnamed, named });

        var models = agg.ModelTotals();
        Assert.Equal(2, models.Count);
        Assert.Contains(models, m => m.Model is null && m.Turns == 2);
        Assert.Contains(models, m => m.Model == "claude-opus-5" && m.Turns == 3);
    }

    private static (long Turns, long Chars) Totals(GatewayInputStatsAggregator agg)
    {
        var dto = agg.CurrentTotals();
        return (dto.Buckets.Sum(b => b.Turns), dto.Buckets.Sum(b => b.Characters));
    }

    private static SessionDto Session(string id, params (string modality, string surface, long turns, long chars)[] buckets)
    {
        var dto = new SessionDto { SessionId = id, InputStats = new InputStatsDto() };
        foreach (var b in buckets)
            dto.InputStats!.Buckets.Add(new InputStatBucketDto
            {
                Modality = b.modality,
                Surface = b.surface,
                Turns = b.turns,
                Characters = b.chars,
            });
        return dto;
    }
}
