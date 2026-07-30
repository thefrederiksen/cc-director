using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Stats;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE REGRESSION SUITE FOR THE 2026-07-30 OUTAGE.
///
/// What happened: the hosted Gateway kept its statistics in a SQLite database on a network share, and
/// GET /sessions WROTE to it on every read. A slot swap put two containers on that share, the indexes
/// corrupted, the write threw, and the exception left the roster handler unhandled - so the Gateway
/// answered HTTP 500 to every client for 32 minutes. An OPTIONAL analytics write failed the fleet's most
/// important READ.
///
/// The mechanism was never SQLite. It was an optional write deciding whether a mandatory one succeeds,
/// and these tests are about the MECHANISM, which is why they use a store that is broken rather than a
/// store that is SQLite.
///
/// THE ONE THAT MATTERS MOST IS THE BLOCKING TEST. A throwing store is the easy case; every throw-based
/// test passes happily while a lock is held. The failure that would actually have replaced the outage is
/// a STALL - a hung write to a network share, holding a lock that every push thread queues behind - and
/// it presents as slowness rather than as an error, so it is harder to find, not easier. Only a store
/// that blocks catches it.
/// </summary>
public sealed class StatisticsCannotFailTheFleetTests
{
    private static readonly TenantId Tenant = TenantId.Local;

    // A push must return in far less than this even on a slow build agent; the stalled write it is racing
    // is deliberately an order of magnitude longer, so a failure here means the ingress genuinely waited
    // on the store rather than that the machine was busy.
    private static readonly TimeSpan PushMustReturnWithin = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StoreStallsFor = TimeSpan.FromSeconds(30);

    // ---------------------------------------------------------------------------------------------
    // THE BLOCKING-STORE TEST. This is the convoy.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AStalledStatisticsWrite_DoesNotHoldTheCaller()
    {
        using var queue = new SyncQueue(operationBound: TimeSpan.FromMilliseconds(200));
        var releaseWrite = new ManualResetEventSlim(false);
        var writeStarted = new ManualResetEventSlim(false);

        // MEASURE THE OFFER OF THE STALLING WRITE ITSELF. That is the whole property: the caller hands over
        // work that will take thirty seconds against an unresponsive share, and must not wait for it.
        //
        // An earlier version of this test stalled one write and then timed a batch of FAST ones. It passed
        // against the pre-fix behaviour - because when the caller does the write itself, the stall lands in
        // the first offer, which that version never timed. It measured the wrong thing and would have shipped
        // as proof of a property it never checked. The fault injection caught it; that is what red-first is
        // for.
        var clock = Stopwatch.StartNew();
        queue.Offer(StatisticsObservationQueue.InputStatsObserver, _ =>
        {
            writeStarted.Set();
            releaseWrite.Wait(StoreStallsFor);
            return Task.CompletedTask;
        });
        clock.Stop();

        Assert.True(clock.Elapsed < PushMustReturnWithin,
            $"offering a statistics write took {clock.Elapsed.TotalSeconds:0.0}s - the caller is doing the "
            + "write itself and waiting on the store, which is the convoy this queue exists to prevent");

        // AND THE WORK MUST ACTUALLY HAVE BEEN TAKEN UP. Without this, the test passes for an implementation
        // that discards every observation: "returned immediately" is trivially true of a queue that does
        // nothing at all, so speed alone proves only half the claim and the useless half at that.
        Assert.True(writeStarted.Wait(TimeSpan.FromSeconds(5)),
            "the offered write never started - returning fast is worthless if the work is simply thrown away");

        releaseWrite.Set();
    }

    [Fact]
    public void AStalledWrite_IsNeverJoinedByASecondWrite_SoAWedgedShareCannotAccumulateThreads()
    {
        // The operation bound reports a stuck write; it CANNOT cancel one, because the work is a
        // synchronous file write and File.WriteAllText does not honour a token. So the consumer must WAIT
        // for the stalled write rather than moving on - otherwise each new observation would start another
        // write to the same hung share, which is both a second writer on one file (the original corruption)
        // and an unbounded thread leak.
        //
        // This asserts the property directly: while one write is stalled and well past its bound, no other
        // write is allowed to begin.
        using var queue = new SyncQueue(operationBound: TimeSpan.FromMilliseconds(100));
        var firstStarted = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        var concurrentWrites = 0;
        var peakConcurrentWrites = 0;

        queue.Offer(StatisticsObservationQueue.InputStatsObserver, _ =>
        {
            Track(ref concurrentWrites, ref peakConcurrentWrites);
            firstStarted.Set();
            release.Wait(StoreStallsFor);
            Interlocked.Decrement(ref concurrentWrites);
            return Task.CompletedTask;
        });
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

        for (var i = 0; i < 10; i++)
            queue.Offer(StatisticsObservationQueue.InputStatsObserver, _ =>
            {
                Track(ref concurrentWrites, ref peakConcurrentWrites);
                Interlocked.Decrement(ref concurrentWrites);
                return Task.CompletedTask;
            });

        // Far longer than the 100ms bound: if the consumer abandoned the stalled write, a second one would
        // have started by now.
        Thread.Sleep(1500);
        Assert.Equal(1, Volatile.Read(ref peakConcurrentWrites));

        release.Set();

        static void Track(ref int current, ref int peak)
        {
            var now = Interlocked.Increment(ref current);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)))
                Interlocked.CompareExchange(ref peak, now, seen);
        }
    }

    [Fact]
    public void AStalledWrite_IsReportedAsStuck_RatherThanLeavingSomebodyToInferItFromASilence()
    {
        using var queue = new SyncQueue(operationBound: TimeSpan.FromMilliseconds(150));
        var release = new ManualResetEventSlim(false);
        queue.Offer(StatisticsObservationQueue.InputStatsObserver, _ => { release.Wait(StoreStallsFor); return Task.CompletedTask; });

        // The bound is a HEALTH CHECK, not a cancellation: the write is still running, and the system's job
        // is to say so. "Stuck since" is a fact; a rising drop count that somebody has to interpret is not.
        var stuck = WaitFor(() => Health(queue, StatisticsObservationQueue.InputStatsObserver)?.StuckSinceUtc is not null,
            TimeSpan.FromSeconds(5));
        Assert.True(stuck, "a write past its bound was never reported as stuck");

        release.Set();
    }

    // ---------------------------------------------------------------------------------------------
    // A BROKEN STORE COSTS A COUNT, NOT THE CALLER.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AThrowingStatisticsWrite_IsCountedAndNamed_AndTheQueueKeepsWorking()
    {
        using var queue = new SyncQueue(operationBound: TimeSpan.FromSeconds(5));
        queue.Offer(StatisticsObservationQueue.InputStatsObserver,
            _ => throw new InvalidOperationException("database disk image is malformed"));

        var recorded = WaitFor(() => Health(queue, StatisticsObservationQueue.InputStatsObserver)?.FailureCount > 0,
            TimeSpan.FromSeconds(5));
        Assert.True(recorded, "a failing statistics write was not counted");

        var health = Health(queue, StatisticsObservationQueue.InputStatsObserver)!;
        Assert.Contains("malformed", health.LastError);
        Assert.True(queue.IsDegraded(), "a failed write must make the queue report itself degraded");

        // The next write still lands: one broken observation does not stop the consumer.
        var ran = new ManualResetEventSlim(false);
        queue.Offer(StatisticsObservationQueue.InputStatsObserver, _ => { ran.Set(); return Task.CompletedTask; });
        Assert.True(ran.Wait(TimeSpan.FromSeconds(5)), "the consumer stopped after one failure");
    }

    [Fact]
    public void AFullQueue_DropsAndCounts_RatherThanBlockingTheCaller()
    {
        using var queue = new SyncQueue(operationBound: TimeSpan.FromMilliseconds(200), capacity: 4);
        var release = new ManualResetEventSlim(false);
        var firstStarted = new ManualResetEventSlim(false);
        queue.Offer(StatisticsObservationQueue.InputStatsObserver,
            _ => { firstStarted.Set(); release.Wait(StoreStallsFor); return Task.CompletedTask; });

        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 500; i++)
            queue.Offer(StatisticsObservationQueue.InputStatsObserver, _ => Task.CompletedTask);
        clock.Stop();

        Assert.True(clock.Elapsed < PushMustReturnWithin,
            "offering into a full queue blocked the caller - drop-and-count is what keeps the ingress free");
        // The first write must actually have STARTED, or this passes for a queue that drops everything
        // always - which satisfies "never blocks" perfectly and is worthless.
        Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)),
            "the queue never began the first write, so this proves nothing about work being accepted");
        var dropped = WaitFor(() => Health(queue, StatisticsObservationQueue.InputStatsObserver)?.DropCount > 0,
            TimeSpan.FromSeconds(5));
        Assert.True(dropped, "a full queue dropped work without counting it, which is the silence we are removing");

        release.Set();
    }

    [Fact]
    public void ConcurrencySamples_CoalescePerTenant_SoASupersededSampleIsNeverWritten()
    {
        using var queue = new SyncQueue(operationBound: TimeSpan.FromSeconds(5));
        var block = new ManualResetEventSlim(false);
        // Occupy the consumer so the samples below queue up behind it.
        queue.Offer(StatisticsObservationQueue.InputStatsObserver, _ => { block.Wait(TimeSpan.FromSeconds(10)); return Task.CompletedTask; });

        // TWO TENANTS, AND VALUES THAT GO DOWN AS WELL AS UP. The first version of this test used one tenant
        // and samples 1..20 ascending, which could not fail: a single global slot passes a one-tenant test,
        // and "keep the latest" and "keep the largest" are indistinguishable when every sample is larger
        // than the last. It blessed the exact defect it was named for.
        var other = new TenantId("other-tenant");
        var written = new List<(string Tenant, int Sample)>();
        void Offer(TenantId t, int sample) =>
            queue.OfferConcurrency(t, sample, _ => { lock (written) written.Add((t.Value, sample)); return Task.CompletedTask; });

        // The PEAK arrives first and is then followed by smaller readings, which is the ordinary shape of a
        // fleet quietening down. Keeping the latest would silently delete the peak that really happened.
        Offer(Tenant, 12);
        Offer(Tenant, 8);
        Offer(Tenant, 3);
        Offer(other, 5);
        Offer(other, 9);
        block.Set();

        var drained = WaitFor(() => { lock (written) return written.Count >= 2; }, TimeSpan.FromSeconds(5));
        Assert.True(drained, "the coalesced concurrency samples were never written");
        lock (written)
        {
            // One write per tenant, each carrying that tenant's MAXIMUM - not its most recent.
            var mine = written.Where(w => w.Tenant == Tenant.Value).Select(w => w.Sample).ToList();
            var theirs = written.Where(w => w.Tenant == other.Value).Select(w => w.Sample).ToList();
            Assert.Equal(new[] { 12 }, mine);
            Assert.Equal(new[] { 9 }, theirs);
        }
    }

    [Fact]
    public async Task Shutdown_DoesNotKeepWritingToTheSharedStore()
    {
        // A consumer still writing while a slot swap has started the next container IS the two-writer
        // window that corrupted the database in the first place. Shutdown must not rebuild it by draining.
        var queue = new StatisticsObservationQueue(operationBound: TimeSpan.FromMilliseconds(200));
        var started = new ManualResetEventSlim(false);
        var wroteAfterShutdown = 0;
        queue.Offer(StatisticsObservationQueue.InputStatsObserver, _ => { started.Set(); Thread.Sleep(3000); return Task.CompletedTask; });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

        for (var i = 0; i < 20; i++)
            queue.Offer(StatisticsObservationQueue.InputStatsObserver,
                _ => { Interlocked.Increment(ref wroteAfterShutdown); return Task.CompletedTask; });

        var clock = Stopwatch.StartNew();
        await queue.DisposeAsync();
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10),
            "shutdown waited for the whole backlog - a bounded deadline is what stops the next container "
            + "starting while this one is still writing to the shared file");

        // THE ASSERTION THIS TEST WAS MISSING. It computed wroteAfterShutdown and never looked at it, so it
        // passed while the consumer drained all twenty writes after shutdown began - the exact behaviour the
        // name says it prevents. A counter nobody asserts on is not a test, it is a decoration, and this one
        // certified the opposite of its own title for as long as it existed.
        var drainedAfterShutdown = Volatile.Read(ref wroteAfterShutdown);
        Assert.True(drainedAfterShutdown == 0,
            $"{drainedAfterShutdown} queued write(s) ran after shutdown began - the point of the bounded "
            + "deadline is that this process stops writing to the shared file before its successor starts, "
            + "and draining the backlog on the way out rebuilds the two-writer window in the cleanup path");

        // Offers after shutdown are refused outright, not queued and not written.
        queue.Offer(StatisticsObservationQueue.InputStatsObserver, _ => { Interlocked.Increment(ref wroteAfterShutdown); return Task.CompletedTask; });
        Assert.Equal(drainedAfterShutdown, Volatile.Read(ref wroteAfterShutdown));
    }

    /// <summary>xUnit facts are synchronous here, and the queue is IAsyncDisposable by design (shutdown is
    /// a bounded wait, not a drain). This thin wrapper lets a test use `using` without turning every fact
    /// async for no reason - it disposes the real queue on the way out.</summary>
    private sealed class SyncQueue : IDisposable
    {
        private readonly StatisticsObservationQueue _inner;
        public SyncQueue(TimeSpan operationBound, int capacity = 512) =>
            _inner = new StatisticsObservationQueue(operationBound, capacity);
        public static implicit operator StatisticsObservationQueue(SyncQueue q) => q._inner;
        public void Offer(string observer, Func<CancellationToken, Task> work) => _inner.Offer(observer, work);
        public void OfferConcurrency(TenantId tenant, int liveSessions, Func<CancellationToken, Task> work) => _inner.OfferConcurrency(tenant, liveSessions, work);
        public IReadOnlyList<StatisticsObservationQueue.ObserverHealthReport> Health() => _inner.Health();
        public bool IsDegraded() => _inner.IsDegraded();
        public void Dispose() => _inner.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static StatisticsObservationQueue.ObserverHealthReport? Health(
        SyncQueue queue, string observer)
    {
        foreach (var r in queue.Health())
            if (string.Equals(r.Observer, observer, StringComparison.Ordinal))
                return r;
        return null;
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (condition()) return true;
            Thread.Sleep(25);
        }
        return condition();
    }
}
