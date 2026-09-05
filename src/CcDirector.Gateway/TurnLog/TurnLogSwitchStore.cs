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

    /// <summary>
    /// How stale the decisions may get before capture STOPS.
    ///
    /// The cache exists so the turn-end path never waits on a database, and that is worth keeping. But a
    /// cache that is trusted forever is a way for a withdrawal never to take effect: if the database is
    /// unreachable, every instance would go on capturing from the last answer it happened to hold, for as
    /// long as the outage lasted, while an administrator who switched capture off had been told it stopped.
    ///
    /// So the answer expires. Past this, IsEnabled says NO for everything until a read succeeds again. That
    /// direction is deliberate and it is not symmetric: losing some records during a database outage is a
    /// gap in a corpus, and capturing somebody who withdrew is a broken promise.
    /// </summary>
    public static readonly TimeSpan MaxTrustedStaleness = TimeSpan.FromMinutes(5);

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

    /// <summary>When the decisions were last read successfully. MinValue means never - which reads as off.</summary>
    private DateTime _lastGoodReadUtc = DateTime.MinValue;

    /// <summary>
    /// Test seam: where <see cref="Refresh"/> gets its rows. Exists so a test can make the re-read FAIL
    /// while the write succeeds, which is the exact shape of the defect this class had - a committed OFF
    /// that a failed refresh quietly discarded, leaving capture on and the caller told it had stopped.
    /// Production never sets it.
    /// </summary>
    internal Func<IReadOnlyList<TurnLogSwitchEntity>>? ReaderForTest { get; set; }

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

        // AN ANSWER WE CANNOT REFRESH IS NOT AN ANSWER. See MaxTrustedStaleness: past the window this
        // stops capturing rather than trusting whatever it last held.
        DateTime lastGood;
        lock (_gate) lastGood = _lastGoodReadUtc;
        if (lastGood == DateTime.MinValue || _nowUtc() - lastGood > MaxTrustedStaleness) return false;

        var rows = _cached;
        if (rows.Count == 0) return false;

        var any = TurnLogSwitchEntity.Any;
        var on = false;
        foreach (var row in rows)
        {
            // CASE-INSENSITIVE, to match how a Director id is keyed where it actually lives: the pushed
            // roster stores Directors in a case-insensitive map (PushedSessionStore). Comparing ordinally
            // here meant a switch row whose identifier differed only in case matched nothing - so a
            // deliberate OFF for one machine could sit in the table looking recorded while a wider ON went
            // on capturing it. For a privacy switch the comparison has to be the LOOSER one, so that an OFF
            // catches every spelling of the thing it is trying to protect.
            var accountMatches = string.Equals(row.Account, account, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(row.Account, any, StringComparison.Ordinal);
            var machineMatches = string.Equals(row.Machine, machine, StringComparison.OrdinalIgnoreCase)
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

            // APPLY IT TO THE CACHE HERE, FROM THE VALUE WE JUST COMMITTED - not by re-reading. Set used to
            // call Refresh() and rely on that; Refresh swallows a failed read and keeps whatever it already
            // had, so a committed OFF could be discarded by a database blip while the endpoint answered
            // "recorded". An administrator would have been told capture had stopped when it had not. The
            // decision is authoritative the moment it commits, and the serving answer now moves with it
            // inside the same lock.
            var updated = _cached.Where(r =>
                    !(string.Equals(r.Account, account, StringComparison.OrdinalIgnoreCase)
                      && string.Equals(r.Machine, machine, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            updated.Add(new TurnLogSwitchEntity
            {
                Account = account,
                Machine = machine,
                Enabled = enabled,
                Actor = actor,
                Reason = reason,
                RecordedUtc = _nowUtc(),
            });
            _cached = updated;
            _lastGoodReadUtc = _nowUtc();
        }

        // Still re-read, so this instance also picks up anything another instance changed. It is now an
        // optimisation rather than the thing the decision depends on, and its failure cannot lose the
        // decision above.
        Refresh();
        FileLog.Write($"[TurnLogSwitchStore] capture {(enabled ? "ON" : "OFF")} for machine={Clean(machine)} (scope recorded)");
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
                if (ReaderForTest is { } read)
                {
                    _cached = read();
                }
                else
                {
                    using var ctx = _db.CreateUnscopedContext();
                    _cached = ctx.TurnLogSwitches.AsNoTracking().ToList();
                }
                _lastGoodReadUtc = _nowUtc();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnLogSwitchStore] Refresh FAILED - the previous decisions still stand: {ex.Message}");
        }
    }

    /// <summary>
    /// An identifier as it is safe to put in a log line. Caller-supplied text reaches the log, and the log
    /// is line-oriented, so an embedded newline lets a caller forge an entry that looks like ours. Control
    /// characters are stripped and the value is capped rather than escaped, because a log line is for
    /// reading and an identifier that needs escaping is already wrong.
    /// </summary>
    internal static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(none)";
        var safe = new string(value.Where(ch => !char.IsControl(ch)).ToArray());
        return safe.Length <= 80 ? safe : safe[..80];
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
