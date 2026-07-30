using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    // THE CEILING ON A STALL, NOT ITS DURATION. Every stalled write in this file is released by a gate the
    // test sets, so the normal path costs milliseconds; this is only the backstop for a gate that never
    // arrives. It is deliberately SHORT because a test that hangs on a build agent burns to the job timeout
    // and produces NO RESULT - neither a pass nor a failure - which is worse than red, and it lands on the
    // path that gates the deploy. A test that cannot finish cannot fail either.
    private static readonly TimeSpan StoreStallsFor = TimeSpan.FromSeconds(2);

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

        // Comfortably past the 100ms bound: if the consumer abandoned the stalled write, a second would have
        // started. Kept short deliberately - the property is observable within a few multiples of the bound,
        // and seconds of real sleep in a test buy nothing but build time.
        Thread.Sleep(400);
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

    /// <summary>
    /// COALESCING IS GONE, SO ITS TEST IS GONE WITH IT - and the replacement asserts the property that
    /// actually matters now: every sample offered gets written, none is silently collapsed away.
    ///
    /// The deleted test is worth remembering rather than quietly dropping. It used one tenant and samples
    /// ascending one to twenty, which could not fail: a single global slot passes a one-tenant test, and
    /// "keep the latest" and "keep the largest" are indistinguishable when every sample is bigger than the
    /// last. It was written to protect coalescing and it blessed the exact defect coalescing had.
    /// </summary>
    [Fact]
    public void EverySampleIsWritten_BecauseNothingIsCoalescedAwayAnyMore()
    {
        using var queue = new SyncQueue(operationBound: TimeSpan.FromSeconds(5));
        var other = new TenantId("other-tenant");
        var written = new List<(string Tenant, int Sample)>();
        void Offer(TenantId t, int sample) =>
            queue.OfferConcurrency(t, _ => { lock (written) written.Add((t.Value, sample)); return Task.CompletedTask; });

        // A peak followed by smaller readings - a fleet quietening down - plus a second tenant. Under the
        // old coalescing every one of these but two would have vanished.
        Offer(Tenant, 12);
        Offer(Tenant, 8);
        Offer(Tenant, 0);
        Offer(other, 5);
        Offer(other, 9);

        var all = WaitFor(() => { lock (written) return written.Count == 5; }, TimeSpan.FromSeconds(5));
        Assert.True(all, "samples were lost - nothing may be collapsed away now that coalescing is gone");
        lock (written)
        {
            Assert.Equal(new[] { 12, 8, 0 }, written.Where(w => w.Tenant == Tenant.Value).Select(w => w.Sample));
            Assert.Equal(new[] { 5, 9 }, written.Where(w => w.Tenant == other.Value).Select(w => w.Sample));
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
        // A GATE, NOT A SLEEP. The old version slept three seconds unconditionally, which cost real time on
        // every run and told us nothing a gate does not - the point is that shutdown does not drain the
        // backlog, and that is true whether the in-flight write takes three seconds or three milliseconds.
        var releaseInFlight = new ManualResetEventSlim(false);
        queue.Offer(StatisticsObservationQueue.InputStatsObserver,
            _ => { started.Set(); releaseInFlight.Wait(StoreStallsFor); return Task.CompletedTask; });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

        for (var i = 0; i < 20; i++)
            queue.Offer(StatisticsObservationQueue.InputStatsObserver,
                _ => { Interlocked.Increment(ref wroteAfterShutdown); return Task.CompletedTask; });

        var clock = Stopwatch.StartNew();
        var dispose = queue.DisposeAsync().AsTask();
        releaseInFlight.Set();   // let the in-flight write finish instead of waiting out the whole deadline
        await dispose;
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
        public void OfferConcurrency(TenantId tenant, Func<CancellationToken, Task> work) => _inner.OfferConcurrency(tenant, work);
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
