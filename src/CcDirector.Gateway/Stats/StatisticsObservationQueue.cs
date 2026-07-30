using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// THE ONE PLACE a statistics write is allowed to be slow.
///
/// WHY THIS EXISTS. On 2026-07-30 the hosted Gateway answered HTTP 500 on GET /sessions for 32 minutes
/// because a corrupted statistics database threw from inside the roster handler: an optional write failed
/// a mandatory read. The first fix moved those writes to the push ingress and wrapped them in a try/catch.
/// That contains a THROW. It does nothing about a STALL - and the statistics stores live on an Azure Files
/// network share, where a write can hang rather than fail.
///
/// The stall is worse than the throw, for a reason that is not obvious until you look:
/// <see cref="GatewaySessionConcurrencyStats.Observe"/> takes its lock and HOLDS IT across a synchronous
/// File.WriteAllText plus File.Move to that share. Observed from every push, every hub thread would
/// contend on one lock held across a network write. Share latency would not stall one push - it would
/// convoy the entire ingress, the pushed store would stop updating, and the roster would go stale for
/// everybody. That is the same outage the owner saw, arrived at from the opposite direction, and it would
/// present as slowness rather than as an error, so it would be harder to diagnose rather than easier.
///
/// So the ingress does not write. It ENQUEUES, and a single background consumer does the writing. A
/// stalled share then starves this consumer and nothing else.
///
/// THE RULES THIS TYPE KEEPS, each one load-bearing:
///
/// 1. NO BACKPRESSURE REACHES THE PRODUCER. Bounded channel, TryWrite only, never Wait, never await a
///    full queue. If enqueuing could ever block, the convoy has simply been rebuilt inside its own fix.
///    A full queue DROPS and COUNTS.
/// 2. NOTHING IS COALESCED. A per-tenant pending slot used to collapse concurrency samples and produced
///    two defects in a row - see OfferConcurrency for both. The timer already bounds the sample rate and
///    the bounded channel already handles a slow consumer by dropping and counting, so collapsing bought
///    nothing that was not already paid for by a mechanism with tests behind it.
/// 3. THE CONSUMER BOUNDS EACH OPERATION, and the bound is a HEALTH CHECK, not a capacity knob.
///
///    BE PRECISE ABOUT WHICH HALF THE BOUND PROTECTS, because reading it as more than it is would be
///    exactly the kind of unproven claim this incident was made of. The bound decides WHEN WE REPORT a
///    write as stuck. It does NOT stop the write: the work is a synchronous file write wrapped in a task,
///    File.WriteAllText does not honour a cancellation token, and nothing here can make it.
///
///    So when the bound fires we mark the observer STUCK, with the time, and then WAIT for the write
///    anyway (see RunOneAsync - the await after the report is deliberate and load-bearing). Abandoning it
///    to start the next one would put a second writer on the same file over the same network share, which
///    is the corruption this whole mission exists to remove; it would also accumulate a hung thread per
///    observation on a wedged mount. Waiting means at most ONE write is ever in flight.
///
///    THE LIMITATION THAT REMAINS, stated rather than left to be discovered: a permanently wedged share
///    parks this consumer forever. One thread stays blocked, every later observation fills the queue and
///    is dropped-and-counted, and NO statistic is written again until the process restarts. That is the
///    deliberate trade - statistics stop, the fleet does not - and "stuck since" plus a climbing drop
///    count is what says so out loud. Step 2 removes these file writes entirely, which retires it.
/// 4. SHUTDOWN STOPS ACCEPTING WORK AND ABANDONS ITS BACKLOG. It does NOT drain into the share, because a
///    consumer still writing while a slot swap has started the next container is the two-writer window
///    rebuilt by the cleanup path. What is past the bounded deadline is counted as lost and let go.
///
///    THIS NARROWS THE WINDOW AND DOES NOT CLOSE IT, and saying otherwise was one of several false
///    completeness claims on this change. An in-flight write cannot be cancelled - it is synchronous and
///    File.WriteAllText ignores a token - so it may still be running when this returns. The successor
///    container is also already booted before this one is asked to stop, which is the warmed swap working
///    as designed, so exclusive ownership is not something this end can grant itself. The window closes
///    when the store leaves the shared file system, which is Step 2; until then process exit is the real
///    boundary and this code does not provide it.
/// 5. EVERY FAILURE IS COUNTED AND NAMED. Silence is what this whole incident was made of.
///
/// WHAT DOES NOT BELONG HERE: anything that does not touch a store. Session-number adoption, for one, is
/// a ConcurrentDictionary under a per-partition lock with no context and no file - it cannot hang on the
/// share and cannot throw a storage error. Queuing it would trade a real regression (a dropped adoption
/// lets the allocator re-issue a live number - duplicate session numbers, untraceable to this change) for
/// protection against a failure mode that does not exist. Queue only what writes to a store.
/// </summary>
public sealed class StatisticsObservationQueue : IAsyncDisposable
{
    /// <summary>Named observers, so health is reported per writer rather than as one opaque total.</summary>
    public const string InputStatsObserver = "inputStats";
    public const string ConcurrencyObserver = "concurrency";
    /// <summary>The snooze prune. Not a statistic, but it is a deferred STORE WRITE off a hot path, which is
    /// what this queue is for - and dropping one is safe because the next snapshot prunes the same rows.</summary>
    public const string SnoozePruneObserver = "snoozePrune";

    private const int DefaultCapacity = 512;

    private readonly Channel<WorkItem> _channel;
    private readonly Task _consumer;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TimeSpan _operationBound;
    private readonly ConcurrentDictionary<string, ObserverHealth> _health = new(StringComparer.Ordinal);



    /// <param name="operationBound">How long one statistics write may take before it is reported as stuck.
    /// A health threshold, not a cancellation - see rule 3.</param>
    /// <param name="capacity">Queue depth before observations are dropped and counted.</param>
    public StatisticsObservationQueue(TimeSpan? operationBound = null, int capacity = DefaultCapacity)
    {
        _operationBound = operationBound ?? TimeSpan.FromSeconds(30);
        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity)
        {
            // Wait, combined with TryWrite-only on the producer side, is what turns "full" into an
            // immediate false rather than a blocked caller. Never call WriteAsync on this channel.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>
    /// Offer one statistics write. Returns immediately, always. Never throws, never blocks, and never
    /// reports failure to the caller - the caller is a push handler and has nothing useful to do with it.
    /// A rejected offer is counted as a drop against <paramref name="observer"/>.
    /// </summary>
    public void Offer(string observer, Func<CancellationToken, Task> work)
    {
        if (work is null) return;
        var health = HealthFor(observer);
        if (_stopping.IsCancellationRequested)
        {
            health.RecordDrop();
            return;
        }
        if (!_channel.Writer.TryWrite(new WorkItem(observer, work)))
        {
            // The consumer is behind - almost always because the share is slow or stuck. Dropping is the
            // correct answer: statistics are optional and the alternative is blocking the ingress.
            health.RecordDrop();
        }
    }

    /// <summary>
    /// COALESCING IS GONE, AND ITS ABSENCE IS THE FIX RATHER THAN A SIMPLIFICATION.
    ///
    /// There used to be a per-tenant pending slot here that collapsed concurrency samples. It produced two
    /// separate defects: first "latest wins", which deleted real peaks, and then - after that was repaired -
    /// "largest live count wins", which was still wrong because the concurrency store records five distinct
    /// facts (live, working, current, per-hour buckets, and distinct session/machine/repository sets) and a
    /// single scalar cannot choose between samples on behalf of all of them. A roster of twelve idle
    /// sessions is not interchangeable with a roster of eight working ones, and "current" wants the LATEST
    /// sample while a peak wants the LARGEST. One slot cannot serve both.
    ///
    /// It also bought almost nothing once concurrency moved to a TIMER. Coalescing existed to stop a
    /// per-push flood; the timer already bounds the rate, and the bounded channel with drop-and-count
    /// already handles a consumer that falls behind - a mechanism that is tested, unlike the slot.
    ///
    /// So every sample is simply offered. Two defects deleted rather than a third attempt at the same idea.
    /// </summary>
    public void OfferConcurrency(TenantId tenant, Func<CancellationToken, Task> work) =>
        Offer(ConcurrencyObserver, work);

    /// <summary>The health of every observer that has been used, for /stats/data and the 503 body.</summary>
    public IReadOnlyList<ObserverHealthReport> Health() =>
        _health.Select(kv => kv.Value.Snapshot(kv.Key)).OrderBy(r => r.Observer, StringComparer.Ordinal).ToList();

    /// <summary>True when any observer is failing, stuck, or dropping - the one flag a caller needs.</summary>
    public bool IsDegraded() => _health.Values.Any(h => h.IsDegraded());

    private ObserverHealth HealthFor(string observer) =>
        _health.GetOrAdd(observer ?? "unknown", _ => new ObserverHealth());

    private async Task ConsumeAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var item))
                {
                    // ONCE SHUTDOWN HAS BEGUN, STOP WRITING. Do not drain the backlog.
                    //
                    // This is the whole point of rule 4 and it was NOT implemented - the loop simply kept
                    // running until the channel was empty, so shutdown wrote every queued observation to the
                    // shared file on the way out. A slot swap starts the successor container while this one
                    // is tearing down, so draining here is precisely the two-writer window that corrupted
                    // the database on 2026-07-30, rebuilt inside the code meant to prevent it. The class
                    // comment claimed this behaviour and the test computed the evidence and threw it away,
                    // so nothing contradicted the claim until the assertion was added.
                    //
                    // What is abandoned is COUNTED, not silently forgotten: losing statistics at shutdown is
                    // acceptable, losing them without saying so is not.
                    if (_stopping.IsCancellationRequested)
                    {
                        HealthFor(item.Observer).RecordDrop();
                        continue;
                    }
                    await RunOneAsync(item).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            // The consumer loop itself must never die quietly - that would silently stop every statistic
            // with nothing to show for it, which is the failure mode this whole class exists to remove.
            // RECORD IT AS HEALTH, not only as a log line. A dead consumer writes nothing ever again, and on
            // a quiet Gateway the queue never fills, so no drop counter climbs either - the failure would be
            // completely invisible to anyone reading /stats/data. It is recorded against every observer this
            // queue knows about, because none of them will be written again.
            foreach (var health in _health.Values)
                health.RecordFailure(new InvalidOperationException(
                    $"the statistics writer stopped: {ex.GetType().Name}: {ex.Message}"));
            FileLog.Write($"[StatisticsObservationQueue] CONSUMER STOPPED: {ex.GetType().Name}: {ex.Message} "
                          + "- statistics are no longer being written; the Gateway is otherwise unaffected.");
        }
    }

    private async Task RunOneAsync(WorkItem item)
    {
        var health = HealthFor(item.Observer);
        var work = item.Work;
        if (work is null) return;

        var started = DateTime.UtcNow;
        var run = Task.Run(() => work(_stopping.Token));
        var finished = await Task.WhenAny(run, Task.Delay(_operationBound, CancellationToken.None))
            .ConfigureAwait(false) == run;

        if (!finished)
        {
            // Rule 3. The write is over its bound. We do NOT abandon it and start another - two concurrent
            // writers to one file over a network share is the corruption we are here to prevent. Mark it
            // stuck, say so once, and wait for it. Whether it ever returns is now a reportable fact.
            health.RecordStuck(started);
            FileLog.Write($"[StatisticsObservationQueue] {item.Observer} write has exceeded {_operationBound.TotalSeconds:0}s "
                          + "and is still running - the statistics store is not responding. Statistics are stalled; "
                          + "the roster, the tunnels and every other surface are unaffected.");
        }

        try
        {
            await run.ConfigureAwait(false);
            health.RecordSuccess();
        }
        catch (Exception ex)
        {
            health.RecordFailure(ex);
            FileLog.Write($"[StatisticsObservationQueue] {item.Observer} write FAILED: {ex.GetType().Name}: {ex.Message} "
                          + $"(failures={health.Snapshot(item.Observer).FailureCount}) - the statistic is lost; "
                          + "nothing else is affected.");
        }
    }

    /// <summary>
    /// Stop accepting work and give whatever is in flight a BOUNDED chance to finish. Rule 4: this is
    /// deliberately not a full drain. A background consumer still writing to the shared file while a slot
    /// swap has already started the next container is precisely the two-writer window that corrupted the
    /// statistics database in the first place, and a cleanup path is a silly place to rebuild it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _channel.Writer.TryComplete();
        var deadline = Task.Delay(TimeSpan.FromSeconds(5));
        if (await Task.WhenAny(_consumer, deadline).ConfigureAwait(false) != _consumer)
        {
            var lost = _channel.Reader.CanCount ? _channel.Reader.Count : -1;
            FileLog.Write($"[StatisticsObservationQueue] shutdown deadline reached with {lost} observation(s) "
                          + "unwritten - abandoning them on purpose rather than writing to a shared file while "
                          + "the next container may already be starting.");
        }
        _stopping.Dispose();
    }

    private sealed record WorkItem(string Observer, Func<CancellationToken, Task>? Work);

    /// <summary>What a reader is told about one observer. Four facts, because a bare failure count cannot
    /// distinguish "broken an hour ago and recovered" from "has not written anything since".</summary>
    public sealed record ObserverHealthReport(
        string Observer,
        long FailureCount,
        long DropCount,
        string? LastError,
        DateTime? LastSuccessfulWriteUtc,
        DateTime? StuckSinceUtc);

    private sealed class ObserverHealth
    {
        private long _failures;
        private long _drops;
        private string? _lastError;
        private DateTime? _lastSuccess;
        private DateTime? _stuckSince;

        // The PRESENT-TENSE verdict, cleared by the next success. The counters are lifetime totals and stay.
        private volatile bool _failingNow;

        public void RecordDrop()
        {
            Interlocked.Increment(ref _drops);
            _failingNow = true;
        }

        public void RecordFailure(Exception ex)
        {
            Interlocked.Increment(ref _failures);
            _lastError = $"{ex.GetType().Name}: {ex.Message}";
            _stuckSince = null;
            _failingNow = true;
        }

        public void RecordStuck(DateTime startedUtc) => _stuckSince = startedUtc;

        public void RecordSuccess()
        {
            _lastSuccess = DateTime.UtcNow;
            _stuckSince = null;
            // A write got through, so whatever was wrong is not wrong NOW. The lifetime counters stay - "this
            // has failed nine times today" is worth keeping - but the CURRENT verdict has to be able to
            // return to healthy, which is what this flag is for.
            _failingNow = false;
        }

        /// <summary>
        /// Is this observer failing RIGHT NOW - as distinct from having ever failed?
        ///
        /// This used to read "any failure or drop, ever". One transient blip or a single queue-full moment
        /// then marked the writer degraded for the entire life of the process, through thousands of
        /// subsequent successful writes. A health flag that can only ever latch true is not health: after the
        /// first bad minute it says the same thing forever, so nobody can use it to tell a live problem from
        /// an old one, and it becomes something people learn to ignore - which is worse than not having it,
        /// because the day it means something it will look identical to every other day.
        ///
        /// The lifetime counts are still reported, because "failed nine times today, last succeeded two
        /// hours ago" is exactly the shape a reader needs. This flag answers only the present tense.
        /// </summary>
        public bool IsDegraded() => _failingNow || _stuckSince is not null;

        public ObserverHealthReport Snapshot(string observer) => new(
            observer,
            Interlocked.Read(ref _failures),
            Interlocked.Read(ref _drops),
            _lastError,
            _lastSuccess,
            _stuckSince);
    }
}
