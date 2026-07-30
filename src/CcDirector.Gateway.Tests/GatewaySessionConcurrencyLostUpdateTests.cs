using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Stats.Data;
using CcDirector.Gateway.Stats.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Sdk;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE LOST-UPDATE PROOF. Every maximum in the concurrency store - both all-time peaks and all five per-hour
/// figures - is written with an explicit <c>ON CONFLICT DO UPDATE ... GREATEST</c> rather than a
/// change-tracked read-then-save. This file is the evidence that the difference is real and that the
/// assertion which claims it can actually fail.
///
/// The race is the one the hosted Gateway is in every time it deploys: a slot swap runs TWO containers
/// against ONE store, both folding the same <c>/sessions</c> roster. Each has its own in-memory picture, so
/// each decides what to write from a view of the store taken before the other wrote. Single-writer SQLite on
/// one machine never exposed this; concurrent PostgreSQL does, and so does any store two processes share.
///
/// <see cref="RunTheRace"/> holds the interleaving and the assertion ONCE, and the two tests below run it
/// against the two implementations:
///
///  - against the real store, the higher maximum survives (the test the store has to pass);
///  - against a deliberately change-tracked read-modify-write writer, the SAME assertion fails - and that
///    failure is asserted here, in the committed suite, so the proof is not a claim about a red somebody
///    saw once on their own machine. If that writer were ever "fixed", this test turns red and says so.
/// </summary>
public sealed class GatewaySessionConcurrencyLostUpdateTests : IDisposable
{
    private readonly StatsConcurrencyTestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly DateTime T0 = new(2026, 7, 11, 20, 0, 0, DateTimeKind.Utc);
    private const string HourKey = "2026-07-11T20";
    private static readonly TenantId Tenant = TenantId.Local;

    /// <summary>One Gateway CONTAINER, in the two halves the race needs: it decides what to write from the
    /// picture it holds now, and it writes later.</summary>
    private interface IContainer
    {
        void Prepare(int live, DateTime at);
        void Commit();
    }

    /// <summary>The real store. Prepare only remembers the roster; the decision and the write happen together
    /// inside Observe, and the write is the upsert.</summary>
    private sealed class UpsertContainer : IContainer
    {
        private readonly GatewaySessionConcurrencyStore _store;
        private List<SessionDto> _roster = new();
        private DateTime _at;

        public UpsertContainer(IDbContextFactory<GatewayStatsDbContext> factory) =>
            _store = new GatewaySessionConcurrencyStore(factory);

        public void Prepare(int live, DateTime at)
        {
            _roster = Roster(live);
            _at = at;
        }

        public void Commit() => _store.Observe(_roster, _at, Tenant);
    }

    /// <summary>
    /// DELIBERATELY WRONG, and kept wrong on purpose. This is the naive port: read the row through the change
    /// tracker, work out the new maximum in memory, save. It is what the store would look like if the ruling
    /// on upserts had been ignored, and it exists so the assertion in <see cref="RunTheRace"/> has something
    /// to fail against. Nothing in the product references it.
    /// </summary>
    private sealed class ReadModifyWriteContainer : IContainer, IDisposable
    {
        private readonly IDbContextFactory<GatewayStatsDbContext> _factory;
        private GatewayStatsDbContext? _ctx;

        public ReadModifyWriteContainer(IDbContextFactory<GatewayStatsDbContext> factory) => _factory = factory;

        public void Prepare(int live, DateTime at)
        {
            _ctx = _factory.CreateDbContext();

            var peak = _ctx.ConcurrencyPeaks.FirstOrDefault(p => p.Tenant == Tenant.Value);
            if (peak is null)
            {
                _ctx.ConcurrencyPeaks.Add(new ConcurrencyPeakEntity
                {
                    Tenant = Tenant.Value,
                    LiveMax = live,
                    LiveMaxAtUtc = live > 0 ? at : null,
                });
            }
            else if (live > peak.LiveMax)
            {
                peak.LiveMax = live;
                peak.LiveMaxAtUtc = at;
            }

            var hour = _ctx.ConcurrencyHours.FirstOrDefault(h => h.Tenant == Tenant.Value && h.HourUtc == HourKey);
            if (hour is null)
            {
                _ctx.ConcurrencyHours.Add(new ConcurrencyHourEntity
                {
                    Tenant = Tenant.Value,
                    HourUtc = HourKey,
                    MaxLive = live,
                });
            }
            else if (live > hour.MaxLive)
            {
                hour.MaxLive = live;
            }
        }

        public void Commit()
        {
            if (_ctx is null) throw new InvalidOperationException("Commit without Prepare.");
            _ctx.SaveChanges();
            _ctx.Dispose();
            _ctx = null;
        }

        public void Dispose() => _ctx?.Dispose();
    }

    private static List<SessionDto> Roster(int live)
    {
        var list = new List<SessionDto>();
        for (var i = 0; i < live; i++)
            list.Add(new SessionDto { SessionId = $"s{i}", ActivityState = "Working", MachineName = "M1", RepoPath = "R1" });
        return list;
    }

    /// <summary>
    /// Two containers, one store, one hour bucket and one all-time peak.
    ///
    /// Both have already seen the fleet at five, so both hold a picture that says five. The fleet then grows,
    /// and they observe it at different moments: container A sees eight, container B sees seven. Their writes
    /// interleave - each decides what to write BEFORE the other has written - and B's write lands last.
    ///
    /// The maximum ever seen was eight, so eight is what the store must hold. A writer that computes the
    /// maximum in its own memory and then writes an absolute value overwrites A's eight with B's seven, and
    /// the owner's dashboard quietly loses the peak it is there to report.
    /// </summary>
    private void RunTheRace(Func<IDbContextFactory<GatewayStatsDbContext>, IContainer> containerFactory)
    {
        var a = containerFactory(_db.NewFactory());
        var b = containerFactory(_db.NewFactory());

        // Seed: sequentially, so both containers and the store agree the maximum is five.
        a.Prepare(5, T0); a.Commit();
        b.Prepare(5, T0.AddMinutes(1)); b.Commit();

        // The race.
        a.Prepare(8, T0.AddMinutes(2));
        b.Prepare(7, T0.AddMinutes(3));
        a.Commit();
        b.Commit();

        using var ctx = _db.NewFactory().CreateDbContext();
        var peak = ctx.ConcurrencyPeaks.AsNoTracking().Single(p => p.Tenant == Tenant.Value);
        var hour = ctx.ConcurrencyHours.AsNoTracking().Single(h => h.Tenant == Tenant.Value && h.HourUtc == HourKey);

        Assert.Equal(8, peak.LiveMax);
        Assert.Equal(8, hour.MaxLive);
        // The timestamp belongs to the write that actually set the maximum, so it must be A's instant and
        // not B's - a peak stamped with the moment of the write that did NOT set it is a wrong answer that
        // looks right.
        Assert.Equal(T0.AddMinutes(2), peak.LiveMaxAtUtc);

        (a as IDisposable)?.Dispose();
        (b as IDisposable)?.Dispose();
    }

    [Fact]
    public void Upsert_KeepsTheHigherMaximum_WhenTwoContainersRaceTheSameHourAndPeak()
    {
        RunTheRace(factory => new UpsertContainer(factory));
    }

    [Fact]
    public void ReadModifyWrite_LosesTheHigherMaximum_WhichIsWhyTheStoreDoesNotUseIt()
    {
        // The SAME race and the SAME assertion, run against the change-tracked read-then-save writer. It
        // fails - that is the point. This is the red that makes the green above mean something, kept in the
        // suite rather than reported as something a worker once watched happen.
        var failure = Assert.Throws<EqualException>(() => RunTheRace(factory => new ReadModifyWriteContainer(factory)));
        Assert.Contains("8", failure.Message, StringComparison.Ordinal);
        Assert.Contains("7", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManyContainersHammeringOneHour_AllEndAtTheTrueMaximum()
    {
        // The deterministic interleaving above is the proof; this is the same property under real threads,
        // which is how it will actually happen. Four containers, each folding rosters of a different size at
        // the same hour, all writing concurrently. The store must end at the largest roster any of them saw.
        const int containers = 4;
        const int observationsEach = 25;
        var stores = Enumerable.Range(0, containers)
            .Select(_ => new GatewaySessionConcurrencyStore(_db.NewFactory()))
            .ToList();

        var trueMax = 0;
        var work = new List<Action>();
        for (var c = 0; c < containers; c++)
        {
            var store = stores[c];
            var offset = c;
            work.Add(() =>
            {
                for (var i = 0; i < observationsEach; i++)
                {
                    var live = 1 + ((offset * observationsEach) + i);
                    store.Observe(Roster(live), T0.AddSeconds(i), Tenant);
                }
            });
            trueMax = Math.Max(trueMax, (offset * observationsEach) + observationsEach);
        }

        Parallel.ForEach(work, action => action());

        using var ctx = _db.NewFactory().CreateDbContext();
        var peak = ctx.ConcurrencyPeaks.AsNoTracking().Single(p => p.Tenant == Tenant.Value);
        var hour = ctx.ConcurrencyHours.AsNoTracking().Single(h => h.Tenant == Tenant.Value && h.HourUtc == HourKey);
        Assert.Equal(trueMax, peak.LiveMax);
        Assert.Equal(trueMax, hour.MaxLive);
    }
}
