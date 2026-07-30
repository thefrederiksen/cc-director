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
/// 2. CONCURRENCY IS COALESCED PER TENANT, latest sample wins. It is a high-water measure, so ten pending
///    identical samples cost memory and buy nothing a maximum can detect.
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
/// 4. SHUTDOWN DOES NOT DRAIN INTO THE SHARE. A consumer still writing while a slot swap has started the
///    next container IS the two-writer window, rebuilt by the cleanup path. Flush with a bounded deadline;
///    whatever is past it is counted as lost and let go.
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

    // Coalescing slots for concurrency: at most one pending observation per tenant. The Func is the work
    // to run; replacing it means the latest sample wins, which is exactly the semantics of a high-water mark.
    private readonly ConcurrentDictionary<TenantId, PendingSample> _pendingConcurrency = new();

    /// <summary>One tenant's pending concurrency observation. <paramref name="LiveSessions"/> is what makes
    /// coalescing by MAXIMUM possible - without a magnitude the queue can only keep the latest, which
    /// silently discards real peaks.</summary>
    private sealed record PendingSample(int LiveSessions, Func<CancellationToken, Task> Work);

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
    /// Offer a concurrency sample for one tenant, replacing any sample for that tenant that has not yet
    /// been written. Rule 2: latest wins, because a maximum cannot tell the difference and the queue
    /// should not fill with samples that are already superseded.
    /// </summary>
    public void OfferConcurrency(TenantId tenant, int liveSessions, Func<CancellationToken, Task> work)
    {
        if (work is null) return;
        var health = HealthFor(ConcurrencyObserver);
        if (_stopping.IsCancellationRequested)
        {
            health.RecordDrop();
            return;
        }

        // KEEP THE LARGER SAMPLE, atomically. AddOrUpdate is what makes this safe against two producers:
        // an earlier read-then-write pair could lose a peak to a racing writer, which is the same defect as
        // "latest wins" arriving by a different route.
        _pendingConcurrency.AddOrUpdate(
            tenant,
            _ => new PendingSample(liveSessions, work),
            (_, existing) => existing.LiveSessions >= liveSessions ? existing : new PendingSample(liveSessions, work));

        // SCHEDULE A MARKER UNCONDITIONALLY, and let a redundant one be harmless.
        //
        // The first version checked whether a marker already existed and skipped writing one if so. That
        // check and the assignment above were separate operations, so a consumer that removed the pending
        // sample in between left a producer believing a marker was still coming when none was - and that
        // tenant's samples were then orphaned FOREVER, with no drop and no failure recorded. Silence again.
        //
        // An extra marker costs one slot and finds nothing to do (see RunOneAsync, which returns when the
        // slot is already empty). That is a far better trade than a lost-update window, and it needs no
        // state machine to reason about.
        if (!_channel.Writer.TryWrite(new WorkItem(ConcurrencyObserver, null, tenant)))
        {
            // Deliberately DO NOT remove the pending sample here - it may belong to another producer, and
            // the timer offers again shortly, so the next marker collects it. Dropping the marker delays a
            // sample; removing someone else's pending work would lose it.
            health.RecordDrop();
        }
    }

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
                    await RunOneAsync(item).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // The consumer loop itself must never die quietly - that would silently stop every statistic
            // with nothing to show for it, which is the failure mode this whole class exists to remove.
            FileLog.Write($"[StatisticsObservationQueue] CONSUMER STOPPED: {ex.GetType().Name}: {ex.Message} "
                          + "- statistics are no longer being written; the Gateway is otherwise unaffected.");
        }
    }

    private async Task RunOneAsync(WorkItem item)
    {
        var health = HealthFor(item.Observer);
        var work = item.Work;
        if (work is null && item.Tenant is TenantId t)
        {
            // A coalescing marker: take this tenant's pending sample - the LARGEST offered since the last
            // drain, not the most recent. A redundant marker finds nothing and returns; see OfferConcurrency
            // for why markers are written unconditionally rather than guarded by a check that could race.
            if (!_pendingConcurrency.TryRemove(t, out var pending)) return;
            work = pending.Work;
        }
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

    private sealed record WorkItem(string Observer, Func<CancellationToken, Task>? Work, TenantId? Tenant = null);

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

        public void RecordDrop() => Interlocked.Increment(ref _drops);

        public void RecordFailure(Exception ex)
        {
            Interlocked.Increment(ref _failures);
            _lastError = $"{ex.GetType().Name}: {ex.Message}";
            _stuckSince = null;
        }

        public void RecordStuck(DateTime startedUtc) => _stuckSince = startedUtc;

        public void RecordSuccess()
        {
            _lastSuccess = DateTime.UtcNow;
            _stuckSince = null;
        }

        public bool IsDegraded() =>
            Interlocked.Read(ref _failures) > 0 || Interlocked.Read(ref _drops) > 0 || _stuckSince is not null;

        public ObserverHealthReport Snapshot(string observer) => new(
            observer,
            Interlocked.Read(ref _failures),
            Interlocked.Read(ref _drops),
            _lastError,
            _lastSuccess,
            _stuckSince);
    }
}
