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
/// THE MOST SPECIFIC SCOPE WINS. A decision about one machine beats a decision about its account, which
/// beats a decision about the fleet. That ordering is what makes it possible to capture a whole account
/// except one machine, and to capture one machine inside an account that is otherwise left alone.
///
/// A FALSE ROW IS A DECISION AND BEATS A WIDER TRUE ONE. Turning a scope off writes a row saying off rather
/// than deleting the row that said on, so "we decided not to capture this" survives as a fact instead of
/// decaying into "nobody has ever considered it".
/// </summary>
public sealed class TurnLogSwitchStore
{
    /// <summary>
    /// How long a read of the switch table is reused before it is taken again. The recorder asks this
    /// question on every turn end - a few hundred times a day - and the answer changes when a person clicks
    /// something, which is to say almost never. Half a minute keeps a database round trip off a path that is
    /// supposed to cost the product nothing, and it bounds how long capture keeps running after somebody
    /// switches it off, which is the direction that matters.
    /// </summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private readonly GatewayDatabase _db;
    private readonly Func<DateTime> _nowUtc;
    private readonly object _gate = new();
    private List<TurnLogSwitchEntity>? _cached;
    private DateTime _cachedAtUtc = DateTime.MinValue;

    public TurnLogSwitchStore(GatewayDatabase db, Func<DateTime>? nowUtc = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Is capture on for this account and machine? Answers the most specific decision that covers them.
    ///
    /// A failure to read the table answers FALSE. An instrument that cannot tell whether it is allowed to
    /// record must not record: the alternative is capturing an account's terminal because a query timed out.
    /// </summary>
    public bool IsEnabled(string account, string machine)
    {
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(machine)) return false;
        try
        {
            var rows = Rows();
            if (rows.Count == 0) return false;

            var any = TurnLogSwitchEntity.Any;
            // Most specific first; the first scope with a row decides, whichever way that row points.
            foreach (var (a, m) in new[]
                     {
                         (account, machine),
                         (account, any),
                         (any, machine),
                         (any, any),
                     })
            {
                var match = rows.FirstOrDefault(r =>
                    string.Equals(r.Account, a, StringComparison.Ordinal) &&
                    string.Equals(r.Machine, m, StringComparison.Ordinal));
                if (match is not null) return match.Enabled;
            }
            return false;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnLogSwitchStore] IsEnabled FAILED - capture stays off: {ex.Message}");
            return false;
        }
    }

    /// <summary>Every decision on record, for an administrator screen to show. Ordered so the widest scopes
    /// read last, matching how they are applied.</summary>
    public IReadOnlyList<TurnLogSwitchEntity> All()
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
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
            using var ctx = _db.CreateContext();
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
            // Drop the cache rather than patch it: a switch somebody just threw must take effect now, and
            // the read that refills it is one query.
            _cached = null;
        }
        FileLog.Write($"[TurnLogSwitchStore] capture {(enabled ? "ON" : "OFF")} for machine={machine} (scope recorded)");
    }

    private List<TurnLogSwitchEntity> Rows()
    {
        lock (_gate)
        {
            var now = _nowUtc();
            if (_cached is not null && now - _cachedAtUtc < CacheFor) return _cached;
            using var ctx = _db.CreateContext();
            _cached = ctx.TurnLogSwitches.AsNoTracking().ToList();
            _cachedAtUtc = now;
            return _cached;
        }
    }
}
