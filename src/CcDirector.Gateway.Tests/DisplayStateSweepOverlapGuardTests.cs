using System.Text.Json;
using CcDirector.Gateway;
using CcDirector.Gateway.Diagnostics;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE DISPLAY-STATE SWEEP RUNS ONE PASS AT A TIME (issue #2323, read-model epic #1159).
///
/// The sweep is a five-second <see cref="System.Threading.Timer"/> that re-folds every session of every
/// tenant as a backstop for the Gateway-only overlays that arrive on no Director push. A timer fires whether
/// or not the last callback finished, so a pass slower than five seconds simply gets another one on top of
/// it. The 31 July load-test baseline measured what that cost: 91 of 98 ticks overlapped a prior tick, with
/// up to 36 sweeps in flight at once, all folding behind the snooze registry's process-wide monitor.
///
/// SKIPPING is the correct behaviour, not queueing: the sweep is a backstop re-fold, so a tick arriving while
/// one is still running has nothing to add that the running pass will not already carry, and the next tick is
/// five seconds away.
///
/// AND A SKIPPED TICK STAYS COUNTABLE, which is the half that is easy to get wrong. <c>sweepOverlaps</c> is
/// the instrument that measured this defect. A guard that simply made the overlap stop happening would leave
/// that counter reading zero for two indistinguishable reasons - the guard works, or nothing is being
/// observed. So the skip is counted as its own fact, and the test below that proves the guard holds is paired
/// with a control proving the overlap counter can still report a non-zero.
///
/// TIMING NOTE, so nobody has to wonder whether these are racy: xUnit builds a fresh instance of this class
/// (and therefore a fresh Gateway) for every test, and the sweep timer's first fire is five seconds after
/// start. Each test here finishes in milliseconds, so the only ticks in the window are the ones the test
/// makes itself.
/// </summary>
public sealed class DisplayStateSweepOverlapGuardTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token",
            authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"));
        await _gateway.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); }
        catch { }
    }

    /// <summary>Read one counter out of the load-test snapshot exactly as <c>/diag/loadmetrics</c> serves it.</summary>
    private static long Counter(string name)
    {
        var json = JsonSerializer.Serialize(LoadTestMetrics.Snapshot(reset: false));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("counters").GetProperty(name).GetInt64();
    }

    [Fact]
    public void ATickThatArrivesWhileAPassIsRunning_IsSkipped_AndIsCountedAsSkipped()
    {
        var passStarted = new ManualResetEventSlim(false);
        var releasePass = new ManualResetEventSlim(false);
        var secondPassRuns = 0;

        var skippedBefore = Counter("sweepSkipped");
        var overlapsBefore = Counter("sweepOverlaps");
        var ticksBefore = Counter("sweepTicks");

        // A pass held open, on its own thread, standing in for a sweep that is slower than the interval.
        var holder = new Thread(() => _gateway.SweepDisplayStateTick(() =>
        {
            passStarted.Set();
            releasePass.Wait(TimeSpan.FromSeconds(30));
        }));
        holder.Start();
        Assert.True(passStarted.Wait(TimeSpan.FromSeconds(30)), "the first pass never started");

        // The tick the five-second timer would deliver on top of it.
        _gateway.SweepDisplayStateTick(() => Interlocked.Increment(ref secondPassRuns));

        // It did not run - which is the whole fix - and it said so.
        Assert.Equal(0, Volatile.Read(ref secondPassRuns));
        Assert.Equal(1L, Counter("sweepSkipped") - skippedBefore);
        Assert.Equal(0L, Counter("sweepOverlaps") - overlapsBefore);
        // Both ticks are still counted, so `sweepTicks` keeps the meaning the 31 July baseline gave it -
        // how many times the timer fired - and ticks-minus-skipped is how many ran.
        Assert.Equal(2L, Counter("sweepTicks") - ticksBefore);

        releasePass.Set();
        Assert.True(holder.Join(TimeSpan.FromSeconds(30)), "the held pass never finished");

        // And the guard let go: the next tick runs.
        var thirdPassRuns = 0;
        _gateway.SweepDisplayStateTick(() => Interlocked.Increment(ref thirdPassRuns));
        Assert.Equal(1, Volatile.Read(ref thirdPassRuns));
    }

    [Fact]
    public void TheOverlapCounter_StillReportsAnOverlap_SoTheZeroAboveIsEarned()
    {
        // VALIDATE THE INSTRUMENT WHERE IT IS POINTED. The test above asserts an ABSENCE - zero overlaps -
        // and an absence is only evidence when the same counter, in the same place, can be shown returning
        // something else. This drives the metric directly, without the guard in front of it, exactly as the
        // unguarded sweep did on 31 July.
        var overlapsBefore = Counter("sweepOverlaps");

        var first = LoadTestMetrics.SweepStarting();
        var second = LoadTestMetrics.SweepStarting();   // a second pass while the first is still in flight
        LoadTestMetrics.SweepFinished(second);
        LoadTestMetrics.SweepFinished(first);

        Assert.Equal(1L, Counter("sweepOverlaps") - overlapsBefore);
    }

    [Fact]
    public void TheGuardIsReleased_EvenWhenThePassThrows()
    {
        // The pass is wrapped in a try/catch so a sweep failure never kills the timer thread. If the release
        // were not in the finally, one throwing pass would wedge the guard closed and the backstop sweep
        // would stop for the life of the process - silently, since the ticks would all read as skipped.
        var skippedBefore = Counter("sweepSkipped");

        _gateway.SweepDisplayStateTick(() => throw new InvalidOperationException("a tenant pass blew up"));

        var ranAfterwards = 0;
        _gateway.SweepDisplayStateTick(() => Interlocked.Increment(ref ranAfterwards));

        Assert.Equal(1, Volatile.Read(ref ranAfterwards));
        Assert.Equal(0L, Counter("sweepSkipped") - skippedBefore);
    }

    [Fact]
    public void TheRealPerTenantPass_RunsUnderTheGuard_AndReleasesIt()
    {
        // The tests above substitute the pass so the guard can be held open deterministically. This one runs
        // the REAL thing the timer runs - SweepDisplayState, the per-tenant fold - twice, so the guard is
        // exercised against the actual work rather than only against a stand-in.
        var skippedBefore = Counter("sweepSkipped");
        var overlapsBefore = Counter("sweepOverlaps");
        var ticksBefore = Counter("sweepTicks");

        _gateway.SweepDisplayState();
        _gateway.SweepDisplayState();

        Assert.Equal(2L, Counter("sweepTicks") - ticksBefore);
        Assert.Equal(0L, Counter("sweepSkipped") - skippedBefore);   // sequential, so neither was skipped
        Assert.Equal(0L, Counter("sweepOverlaps") - overlapsBefore);
    }
}
