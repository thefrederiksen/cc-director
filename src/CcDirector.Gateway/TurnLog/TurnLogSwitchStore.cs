using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.TurnLog;

/// <summary>
/// Whether turn-end capture is on, for whom, and who said so.
///
/// THE DEFAULT IS OFF, AND OFF MEANS NOTHING HAPPENS. With no row for a scope the recorder does not run, no
/// screen is read, no file is written, and a turn ends exactly as it would if this code did not exist. That
/// is the property the whole instrument rests on: an observer that changes what it observes is not measuring
/// the product, it is measuring itself.
///
/// IT READS THROUGH AN UNSCOPED CONTEXT, AND THAT IS NOT A DETAIL. This table is deliberately global: an
/// administrator writes it for an account that is not their own, and the recorder reads it at a turn-end
/// boundary where no tenant is in scope yet. A tenant-scoped context THROWS when the ambient tenant is
/// missing, which on the hosted Gateway is every administrator request and every turn end - and because the
/// read below cannot be allowed to fail open, that throw would have been swallowed into "capture is off".
/// The feature would have looked switched on and recorded nothing, on the one deployment that matters. Use
/// <see cref="GatewayDatabase.CreateUnscopedContext"/> here, always.
///
/// AN "OFF" DECISION ANYWHERE WINS. Rather than ranking scopes against each other - is a rule about one
/// machine more specific than a rule about one account? - the resolution is simply that any matching
/// decision saying off beats every decision saying on. For a switch that starts copying somebody's terminal,
/// the direction of any ambiguity has to be "stop", and a person who switches a scope off must never have to
/// work out which other row silently outranks theirs.
///
/// A FALSE ROW IS A DECISION AND IS STORED AS ONE. Turning a scope off writes a row saying off rather than
/// deleting the row that said on, so "we decided not to capture this" survives as a fact instead of decaying
/// into "nobody has ever considered it".
/// </summary>
public sealed class TurnLogSwitchStore : IDisposable
{
    /// <summary>
    /// How often the cached decisions are re-read in the BACKGROUND. The recorder asks this question on
    /// every turn end - a few hundred times a day - and it must never wait on a database to do it, so the
    /// answer is always served from memory and refreshed on a timer. Half a minute bounds how long capture
    /// keeps running after somebody switches it off, which is the direction that matters.
    /// </summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(30);

    private readonly GatewayDatabase _db;
    private readonly Func<DateTime> _nowUtc;
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _disposed;

    /// <summary>
    /// The decisions, as last read. Starts EMPTY, which means off - so a Gateway that has not yet managed
    /// its first read captures nothing. That is the safe direction: the cost of being late is a few missing
    /// records, and the cost of being wrong the other way is recording somebody who was switched off.
    /// </summary>
    private volatile IReadOnlyList<TurnLogSwitchEntity> _cached = Array.Empty<TurnLogSwitchEntity>();

    public TurnLogSwitchStore(GatewayDatabase db, Func<DateTime>? nowUtc = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Read the decisions now, and keep them fresh in the background. Safe to call more than once; a
    /// failure to prime is logged and leaves capture off until a later refresh succeeds.
    /// </summary>
    public void Start()
    {
        if (_disposed) return;
        Refresh();
        lock (_gate)
        {
            _timer ??= new Timer(_ => Refresh(), null, RefreshEvery, RefreshEvery);
        }
        FileLog.Write($"[TurnLogSwitchStore] Start: {_cached.Count} decision(s) on record, refreshing every {RefreshEvery.TotalSeconds:F0}s");
    }

    /// <summary>
    /// Is capture on for this account and machine?
    ///
    /// PURE MEMORY, NO INPUT OR OUTPUT, NO LOCK THE TURN-END PATH CAN QUEUE ON. This is called from the
    /// turn-end callback before anything is spawned, so it has to be free. A database read here - even a
    /// cached one that occasionally misses - would put blocking work on the path the supervisor, the rules
    /// engine and the voice refresh all run on, which is precisely the harm this instrument promised not to
    /// do.
    /// </summary>
    public bool IsEnabled(string account, string machine)
    {
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(machine)) return false;

        var rows = _cached;
        if (rows.Count == 0) return false;

        var any = TurnLogSwitchEntity.Any;
        var on = false;
        foreach (var row in rows)
        {
            var accountMatches = string.Equals(row.Account, account, StringComparison.Ordinal)
                                 || string.Equals(row.Account, any, StringComparison.Ordinal);
            var machineMatches = string.Equals(row.Machine, machine, StringComparison.Ordinal)
                                 || string.Equals(row.Machine, any, StringComparison.Ordinal);
            if (!accountMatches || !machineMatches) continue;

            // Off wins outright, so there is no need to finish the scan for a stronger "on".
            if (!row.Enabled) return false;
            on = true;
        }
        return on;
    }

    /// <summary>Every decision on record, for an administrator screen to show. Reads the database rather
    /// than the cache: a screen showing a stale answer about whose terminal is being recorded is worse than
    /// a screen that takes a moment.</summary>
    public IReadOnlyList<TurnLogSwitchEntity> All()
    {
        lock (_gate)
        {
            using var ctx = _db.CreateUnscopedContext();
            return ctx.TurnLogSwitches.AsNoTracking()
                .OrderBy(r => r.Account).ThenBy(r => r.Machine)
                .ToList();
        }
    }

    /// <summary>
    /// Record a decision for one scope, replacing whatever that scope said before. Throws on a blank actor
    /// or reason rather than storing an anonymous one: for an account that is not ours, the reason is where
    /// the permission is written down, and a ledger of blank reasons answers no question anybody will
    /// actually ask.
    /// </summary>
    public void Set(string account, string machine, bool enabled, string actor, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(machine);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_gate)
        {
            using var ctx = _db.CreateUnscopedContext();
            var existing = ctx.TurnLogSwitches
                .FirstOrDefault(r => r.Account == account && r.Machine == machine);
            if (existing is null)
            {
                ctx.TurnLogSwitches.Add(new TurnLogSwitchEntity
                {
                    Account = account,
                    Machine = machine,
                    Enabled = enabled,
                    Actor = actor,
                    Reason = reason,
                    RecordedUtc = _nowUtc(),
                });
            }
            else
            {
                existing.Enabled = enabled;
                existing.Actor = actor;
                existing.Reason = reason;
                existing.RecordedUtc = _nowUtc();
            }
            ctx.SaveChanges();
        }

        // Take effect NOW rather than at the next tick. Someone who switches capture off is entitled to
        // have it stop, not to wait out a refresh interval they cannot see.
        Refresh();
        FileLog.Write($"[TurnLogSwitchStore] capture {(enabled ? "ON" : "OFF")} for machine={machine} (scope recorded)");
    }

    /// <summary>
    /// Re-read the decisions. A failure leaves the previous answer standing and is logged loudly - it must
    /// not silently switch capture off, which would leave a corpus with a hole nobody decided on, nor
    /// silently switch it on.
    /// </summary>
    public void Refresh()
    {
        if (_disposed) return;
        try
        {
            lock (_gate)
            {
                using var ctx = _db.CreateUnscopedContext();
                _cached = ctx.TurnLogSwitches.AsNoTracking().ToList();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnLogSwitchStore] Refresh FAILED - the previous decisions still stand: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            try { _timer?.Dispose(); } catch (ObjectDisposedException) { }
            _timer = null;
        }
    }
}
