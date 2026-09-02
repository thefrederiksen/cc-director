using CcDirector.Gateway.Stats.Data;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A STATISTICS STORE THAT COULD NOT BE REACHED COMES BACK ON ITS OWN.
///
/// THE INCIDENT THIS FILE IS THE PROOF FOR (devthrottle_internal, 2 September 2026). A deploy ran
/// production and staging together for four minutes. Each container opens its own connection pool, the
/// pooler refused the incoming container's statistics connection, and the open failed. Nothing retried it.
/// Your Throttle answered 503 to every request for the next two hours and every turn the owner drove went
/// unrecorded - against a database that was healthy the whole time, serving the roster from the other pool
/// in the same process, and which answered the very next connection anybody made to it.
///
/// The store already had a promise for a SLOW database: the open outlives the startup deadline and
/// publishes when it finishes, so a slow store costs the first seconds of one boot instead of everything
/// after it. It had no promise at all for an UNREACHABLE one. That asymmetry is the whole defect, and
/// nothing in the suite could see it, because every fixture here either opens successfully or fails
/// against a fault that never clears - and a store that never retries and a store that retries into a
/// permanent fault are indistinguishable when the fault is permanent.
///
/// SO THE FAULT HERE CLEARS. That is the one thing this file does that no other test in the suite does.
/// The store is pointed at a SQLite path that CANNOT be opened - a directory sits where the database file
/// belongs, which is a genuine "unable to open database file" from the provider and not a fabricated
/// failure. The store is asserted unavailable. Then the obstruction is REMOVED and nothing else happens:
/// no restart, no second construction, no call of any kind. The store has to notice by itself.
///
/// A NEGATIVE CONTROL RIDES ALONG, because "it became available" would also pass against a store that was
/// never really broken: <see cref="AnObstructedStore_IsUnavailableToBeginWith"/> holds the obstruction in
/// place and asserts the store stays unavailable and names UNREACHABLE. Delete the reopen and the first
/// test fails while the control still passes - which is the shape that tells you the first test is
/// measuring the reopen and not the weather.
/// </summary>
public sealed class StatsStoreReopensAfterAnUnreachableStoreTests : IDisposable
{
    /// <summary>
    /// How long a test will wait for the reopen. Comfortably past the FIRST backoff step and no further:
    /// the claim under test is that the store retries promptly, and a generous window would let a fix that
    /// only retried once a minute pass a test whose comment says "promptly".
    /// </summary>
    private static readonly TimeSpan Patience = GatewayStatsStore.ReopenBackoff[0] + TimeSpan.FromSeconds(20);

    private readonly ITestOutputHelper _out;
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "cc-stats-reopen-" + Guid.NewGuid().ToString("N"));

    public StatsStoreReopensAfterAnUnreachableStoreTests(ITestOutputHelper output)
    {
        _out = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private string SqlitePath => Path.Combine(_dir, "gateway-stats.db");

    /// <summary>
    /// Put a DIRECTORY where the database file belongs. SQLite cannot open it, and the failure is the real
    /// provider error an operator would see rather than a stub throwing on our behalf.
    /// </summary>
    private void Obstruct() => Directory.CreateDirectory(SqlitePath);

    private void ClearTheObstruction() => Directory.Delete(SqlitePath, recursive: true);

    private StatsConnectionChoice SelfHostChoice() =>
        StatsConnectionSelection.Resolve(
            statsOverride: null, gatewayConnection: null, hosted: false, sqlitePath: SqlitePath);

    // ============================================================ the negative control, first

    /// <summary>
    /// THE CONTROL. While the obstruction is in place the store is unavailable, and it says UNREACHABLE -
    /// a database problem, not a missing setting and not our own bug. Without this arm, the reopen test
    /// below could pass against a store that opened fine on the first attempt and never retried anything.
    /// </summary>
    [Fact]
    public void AnObstructedStore_IsUnavailableToBeginWith()
    {
        Obstruct();

        using var store = new GatewayStatsStore(SelfHostChoice());

        Assert.False(store.Availability.IsAvailable);
        Assert.Null(store.Factory);
        Assert.Equal(StatsStoreUnavailableReason.Unreachable, store.Availability.Reason);
        _out.WriteLine($"obstructed: {store.Availability.ReasonCode}: {store.Availability.Detail}");

        // AND IT STAYS THAT WAY WHILE THE FAULT STAYS. The retry must not invent a store out of a fault
        // that has not cleared, which is the one way a "it comes back" fix could be worse than no fix.
        Thread.Sleep(Patience);
        Assert.False(store.Availability.IsAvailable);
        Assert.Null(store.Factory);
    }

    // ============================================================ the claim

    /// <summary>
    /// THE CLAIM. The obstruction is removed and NOTHING ELSE HAPPENS - no restart, no reconstruction, not
    /// one call into the store. It has to come back by itself, which is exactly what production could not
    /// do on 2 September 2026.
    /// </summary>
    [Fact]
    public void WhenTheFaultClears_TheStoreReopensItself_WithNoRestartAndNoCall()
    {
        Obstruct();

        using var store = new GatewayStatsStore(SelfHostChoice());
        Assert.False(store.Availability.IsAvailable);   // the precondition, not the claim

        ClearTheObstruction();

        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline && !store.Availability.IsAvailable)
            Thread.Sleep(250);

        Assert.True(
            store.Availability.IsAvailable,
            "the statistics store did not reopen itself after the fault cleared - this is the 2 September " +
            $"2026 defect: {store.Availability.ReasonCode}: {store.Availability.Detail}");
        Assert.NotNull(store.Factory);
        Assert.Equal(StatsStoreUnavailableReason.None, store.Availability.Reason);

        // AND IT IS A USABLE STORE, not merely a flag that flipped. A reopen that published an availability
        // without a working context would read as fixed on every surface and record nothing, which is the
        // half-state this whole area keeps producing.
        using var context = store.CreateContext();
        Assert.True(context.Database.CanConnect());
        _out.WriteLine("reopened and served a working context");
    }

    // ============================================================ what must NOT be retried

    /// <summary>
    /// A store with NOTHING CONFIGURED is not retried, because there is nothing to retry: no setting names
    /// a database, and asking again produces no connection string. It must stay unavailable, keep saying so
    /// with its own distinct reason, and never quietly become available.
    ///
    /// This matters beyond tidiness. NOT CONFIGURED and UNREACHABLE are the two states an operator acts on
    /// differently - one is a setting to fix, the other is a database to wait for - and a retry that
    /// treated them alike would put the wrong advice on the surface for the one that needs a human.
    /// </summary>
    [Fact]
    public void AStoreWithNothingConfigured_IsNotRetried()
    {
        var blank = StatsConnectionSelection.Resolve(
            statsOverride: "", gatewayConnection: null, hosted: true, sqlitePath: SqlitePath);

        using var store = new GatewayStatsStore(blank);

        Assert.False(store.Availability.IsAvailable);
        Assert.Equal(StatsStoreUnavailableReason.NotConfigured, store.Availability.Reason);

        Thread.Sleep(Patience);

        Assert.False(store.Availability.IsAvailable);
        Assert.Equal(StatsStoreUnavailableReason.NotConfigured, store.Availability.Reason);
        _out.WriteLine($"not configured, and left alone: {store.Availability.ReasonCode}");
    }

    // ============================================================ the schedule itself

    /// <summary>
    /// THE BACKOFF NEVER STOPS. The last entry repeats for the life of the process, so this asserts the
    /// SHAPE that makes that safe rather than the numbers, which will move: it starts quickly, it never
    /// goes backwards, and it settles somewhere a permanent poll is affordable.
    ///
    /// The claim being defended is that the loop has no give-up. A fix that retried five times and stopped
    /// would satisfy every other test in this file - the fault always clears within the first attempt or
    /// two here - and would reintroduce the incident for any outage longer than the schedule.
    /// </summary>
    [Fact]
    public void TheBackoff_StartsQuickly_NeverGoesBackwards_AndSettlesSomewhereAffordable()
    {
        var schedule = GatewayStatsStore.ReopenBackoff;

        Assert.NotEmpty(schedule);
        Assert.True(
            schedule[0] <= TimeSpan.FromSeconds(10),
            $"the first retry is {schedule[0]}, too slow for a pooler that refused one connection");

        for (var i = 1; i < schedule.Count; i++)
            Assert.True(
                schedule[i] >= schedule[i - 1],
                $"the backoff goes backwards at step {i}: {schedule[i - 1]} then {schedule[i]}");

        // The steady state, which repeats forever. Slow enough to be free, fast enough that a database
        // coming back is noticed in about a minute rather than an hour.
        var steady = schedule[^1];
        Assert.InRange(steady, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
    }
}
