using System.Globalization;
using System.Text;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats.Data;
using CcDirector.Gateway.Stats.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// The Gateway's durable record of fleet CONCURRENCY and its hourly activity log, held in the statistics
/// DATABASE. This is <see cref="GatewaySessionConcurrencyStats"/> - the same numbers, the same snapshot -
/// moved off <c>gateway-concurrency-stats.json</c>.
///
/// WHY IT MOVED, because "it is only a stats file" is the wrong reading of it. That JSON file was rewritten
/// IN FULL, from the hottest path in the system, on every <c>/sessions</c> read on which anything changed.
/// On 2026-07-30 a slot swap ran two containers against one shared Azure Files share and corrupted a
/// database living on it; the hosted Gateway answered HTTP 500 to every client for 32 minutes. This file was
/// 53 KB and was being written by the same two containers through the same window. It is corruptible by
/// exactly that race - it simply fails later, and with a parse error instead of a malformed-image error.
///
/// FOUR PROPERTIES CARRIED OVER UNCHANGED. Getting any of them wrong changes numbers on the owner's own
/// dashboard, so each is written out where it is implemented as well as here:
///
///  1. The two CURRENT values are RUNTIME-ONLY and are not persisted - they were not in the JSON file
///     either. They live in <see cref="TenantShadow"/> and reset to zero when the process restarts.
///  2. Every peak and every per-hour figure is a MAXIMUM and only ever grows, so every one of them is
///     written with an explicit <c>ON CONFLICT DO UPDATE ... GREATEST</c>, never a change-tracked
///     read-then-save, and each all-time timestamp moves only on the write where its own maximum actually
///     advanced. Read-modify-write is a lost-update generator the moment two containers observe the same
///     roster - which is precisely what a slot swap does - and single-writer SQLite never exposed it.
///  3. The current-hour dedup sets stay IN MEMORY with their existing comparers (Ordinal for session ids,
///     OrdinalIgnoreCase for machines and repositories). <c>concurrency_hour_member</c> is only how they
///     survive a restart. See <see cref="ConcurrencyHourMemberEntity"/> for the consequence and why it is
///     not a bug.
///  4. Retention is 90 days of hour buckets, pruned on write, and the member rows prune WITH their hour.
///
/// Threading: one in-process lock guards the in-memory shadow, exactly as the JSON store's lock did. It does
/// NOT (and cannot) serialize other containers - that is what the upserts are for.
/// </summary>
public sealed class GatewaySessionConcurrencyStore
{
    private const int RetentionDays = 90;
    private const string HourFormat = "yyyy-MM-ddTHH";

    /// <summary>How many member rows go into one INSERT. Bounded so a large fleet's first observation of an
    /// hour cannot build a statement with more parameters than a provider accepts (PostgreSQL's limit is
    /// 65535 per statement, and each row here costs two).</summary>
    private const int MemberInsertBatchRows = 200;

    private readonly IDbContextFactory<GatewayStatsDbContext> _factory;
    private readonly object _lock = new();

    // The in-memory shadow, per tenant. It is NOT a cache of the store's contents and is never read to
    // answer a snapshot: it holds the two runtime-only current values, plus enough of what THIS process has
    // written to decide whether a write is needed at all. The shadow's maxima are always less than or equal
    // to the stored ones (they advance only when we write, and the stored ones only ever grow), so skipping
    // a write because the observed value does not beat the shadow can never skip a write that would have
    // changed the store.
    private readonly Dictionary<TenantId, TenantShadow> _shadows = new();

    // Built once from the model, so the statements can never drift from the mapped table and column names.
    private Statements? _statements;

    private sealed class TenantShadow
    {
        // Runtime-only (property 1). Never written to any table.
        public int LiveCurrent;
        public int WorkingCurrent;

        // The all-time peaks as far as this process knows. No timestamps here on purpose: the shadow exists
        // only to decide whether a write is needed, and only the maxima decide that. The authoritative
        // timestamps live in the table and are read back by Snapshot.
        public int LiveMax;
        public int WorkingMax;
        public bool PeakLoaded;

        // The current hour and this process's picture of its row.
        public string CurrentHourKey = "";
        public bool HourExists;
        public int HourMaxLive;
        public int HourMaxWorking;
        public int HourDistinctSessions;
        public int HourDistinctMachines;
        public int HourDistinctRepos;

        // The dedup sets for the CURRENT hour, with the comparers that have always decided identity here
        // (property 3). Rehydrated from concurrency_hour_member when the hour rolls or the tenant is first
        // touched, so a restart mid-hour keeps counting the same hour correctly.
        //
        // KNOWN LIMITATION, CARRIED FORWARD DELIBERATELY - do not "fix" it by counting rows in the member
        // table. Two containers folding the same hour hold SEPARATE sets, and each rehydrates only when the
        // hour rolls or it first touches the tenant. So within one hour each can write a distinct count
        // below the true union of what both saw, and the stored count is the larger of the two rather than
        // the count of the union. This is exactly what the JSON store did (and strictly better than its
        // whole-file last-writer-wins clobber), and counting rows instead would hand the decision "are
        // these two machine names the same machine" to the database's collation, which is not equivalent to
        // the OrdinalIgnoreCase comparer that decides it here. Ruled on and recorded rather than improved.
        public readonly HashSet<string> CurSessions = new(StringComparer.Ordinal);
        public readonly HashSet<string> CurMachines = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> CurRepos = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <param name="factory">The statistics context factory. The store takes a fresh context per operation
    /// and never holds one open.</param>
    public GatewaySessionConcurrencyStore(IDbContextFactory<GatewayStatsDbContext> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        FileLog.Write("[GatewaySessionConcurrencyStore] ctor: fleet concurrency is recorded in the statistics database, not in a shared file");
    }

    private static string HourKey(DateTime utc) =>
        utc.ToUniversalTime().ToString(HourFormat, CultureInfo.InvariantCulture);

    // Resolve the tenant a producer or reader supplied. Null is the self-host / unit-test default (Local); an
    // explicitly-passed tenant must be valid - an invalid one is a DENY, never silently defaulted. Carried
    // over verbatim from the JSON store.
    private static TenantId RequireTenant(TenantId? tenant)
    {
        if (tenant is not { } t) return TenantId.Local;
        if (!t.IsValid) throw new ArgumentException("a valid tenant is required", nameof(tenant));
        return t;
    }

    private TenantShadow ShadowFor(TenantId tenant)
    {
        if (!_shadows.TryGetValue(tenant, out var sh))
        {
            sh = new TenantShadow();
            _shadows[tenant] = sh;
        }
        return sh;
    }

    /// <summary>
    /// Observe the current fleet <paramref name="roster"/> for <paramref name="tenant"/> at
    /// <paramref name="nowUtc"/>: update that tenant's live and working current values and all-time peaks, and
    /// fold this hour's max concurrency plus its distinct session / machine / repository counts. Idempotent
    /// within an hour - a peak or distinct count only ever grows - so folding on every <c>/sessions</c> read
    /// captures the hourly log without inflating anything. Defaults to <see cref="TenantId.Local"/> for the
    /// self-host shape and the unit-test default; the production <c>/sessions</c> path passes the request
    /// tenant.
    ///
    /// It writes NOTHING when nothing advanced and no new member appeared, which is the common case on a busy
    /// Gateway - the same reason the JSON store had a "changed" flag before rewriting 53 KB.
    ///
    /// Deliberately NOT logged per call: this runs on every <c>/sessions</c> read, and a log line per
    /// observation would bury the log. The events worth a line - first touch of a tenant, an hour roll, a
    /// prune that actually deleted something, and any failure - are logged where they happen.
    /// </summary>
    public void Observe(IReadOnlyCollection<SessionDto>? roster, DateTime nowUtc, TenantId? tenant = null)
    {
        if (roster is null) return;
        var t = RequireTenant(tenant);
        var tenantValue = t.Value;
        var key = HourKey(nowUtc);

        lock (_lock)
        {
            var sh = ShadowFor(t);
            GatewayStatsDbContext? ctx = null;
            try
            {
                if (!sh.PeakLoaded || key != sh.CurrentHourKey)
                {
                    ctx = _factory.CreateDbContext();
                    if (!sh.PeakLoaded) LoadPeak(ctx, tenantValue, sh);
                    if (key != sh.CurrentHourKey) RollHour(ctx, tenantValue, sh, key);
                }

                var liveCount = 0;
                var workingCount = 0;
                // Only the members this observation ADDS to a set need a row - HashSet.Add tells us exactly
                // which those are, and the set already holds everything the store has for this hour.
                var newMembers = new List<(string Kind, string Member)>();
                foreach (var s in roster)
                {
                    if (string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase)) continue;
                    liveCount++;
                    if (string.Equals(s.ActivityState, "Working", StringComparison.OrdinalIgnoreCase)) workingCount++;
                    if (!string.IsNullOrEmpty(s.SessionId) && sh.CurSessions.Add(s.SessionId))
                        newMembers.Add((ConcurrencyMemberKinds.Session, s.SessionId));
                    if (!string.IsNullOrWhiteSpace(s.MachineName) && sh.CurMachines.Add(s.MachineName))
                        newMembers.Add((ConcurrencyMemberKinds.Machine, s.MachineName));
                    if (!string.IsNullOrWhiteSpace(s.RepoPath) && sh.CurRepos.Add(s.RepoPath))
                        newMembers.Add((ConcurrencyMemberKinds.Repo, s.RepoPath));
                }

                sh.LiveCurrent = liveCount;
                sh.WorkingCurrent = workingCount;

                var peakAdvanced = liveCount > sh.LiveMax || workingCount > sh.WorkingMax;
                // A brand-new hour is written even when every figure in it is zero: the JSON store created
                // the bucket on first observation of the hour, so the hour appears in the snapshot with
                // zeroes rather than being absent. That is a visible difference on the 24-hour chart.
                var hourAdvanced = !sh.HourExists
                    || liveCount > sh.HourMaxLive
                    || workingCount > sh.HourMaxWorking
                    || sh.CurSessions.Count > sh.HourDistinctSessions
                    || sh.CurMachines.Count > sh.HourDistinctMachines
                    || sh.CurRepos.Count > sh.HourDistinctRepos;

                if (!peakAdvanced && !hourAdvanced && newMembers.Count == 0)
                    return;

                ctx ??= _factory.CreateDbContext();
                var sql = StatementsFor(ctx);

                // One transaction for the whole observation: a crash between the hour row and its member
                // rows would otherwise leave a count that the members cannot reproduce after a restart.
                using var tx = ctx.Database.BeginTransaction();
                try
                {
                    if (peakAdvanced)
                        ctx.Database.ExecuteSqlRaw(sql.UpsertPeak, tenantValue, liveCount, workingCount, nowUtc);

                    if (hourAdvanced)
                        ctx.Database.ExecuteSqlRaw(sql.UpsertHour, tenantValue, key, liveCount, workingCount,
                            sh.CurSessions.Count, sh.CurMachines.Count, sh.CurRepos.Count);

                    if (newMembers.Count > 0)
                        InsertMembers(ctx, sql, tenantValue, key, newMembers);

                    Prune(ctx, sql, tenantValue, nowUtc);
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    // The fold above already put this observation's new members into the dedup sets, but
                    // nothing was stored. Take them back out. Left in, they would be treated as
                    // already-persisted for the rest of the hour - HashSet.Add is what decides whether a
                    // member row is written - so their rows would never be written at all, and a container
                    // restarting later in the same hour would rehydrate an incomplete set.
                    foreach (var (kind, member) in newMembers)
                    {
                        switch (kind)
                        {
                            case ConcurrencyMemberKinds.Session: sh.CurSessions.Remove(member); break;
                            case ConcurrencyMemberKinds.Machine: sh.CurMachines.Remove(member); break;
                            case ConcurrencyMemberKinds.Repo: sh.CurRepos.Remove(member); break;
                        }
                    }

                    FileLog.Write($"[GatewaySessionConcurrencyStore] Observe FAILED: tenant={tenantValue} hour={key}: {ex.Message}");
                    throw;
                }

                // Only after the write is committed does the shadow advance. If the write threw, the shadow
                // still describes what is actually stored, so the next observation retries the same write
                // rather than deciding it has nothing to do.
                if (liveCount > sh.LiveMax) sh.LiveMax = liveCount;
                if (workingCount > sh.WorkingMax) sh.WorkingMax = workingCount;
                sh.HourExists = true;
                if (liveCount > sh.HourMaxLive) sh.HourMaxLive = liveCount;
                if (workingCount > sh.HourMaxWorking) sh.HourMaxWorking = workingCount;
                if (sh.CurSessions.Count > sh.HourDistinctSessions) sh.HourDistinctSessions = sh.CurSessions.Count;
                if (sh.CurMachines.Count > sh.HourDistinctMachines) sh.HourDistinctMachines = sh.CurMachines.Count;
                if (sh.CurRepos.Count > sh.HourDistinctRepos) sh.HourDistinctRepos = sh.CurRepos.Count;
            }
            finally
            {
                ctx?.Dispose();
            }
        }
    }

    /// <summary>A read-only snapshot for the dashboard / agent API for <paramref name="tenant"/>: the live and
    /// working series (current, all-time peak, derived weekly max) and the full hourly log. An unseen tenant
    /// returns an all-zero snapshot with no hours - there is simply nothing of theirs to read.</summary>
    public ConcurrencySnapshot Snapshot(DateTime nowUtc, TenantId? tenant = null)
    {
        var t = RequireTenant(tenant);
        var tenantValue = t.Value;

        lock (_lock)
        {
            // The two current values are runtime-only, so they come from this process's shadow and are zero
            // for a tenant this process has not observed - which is exactly what the JSON store reported
            // after a restart, because it never persisted them either.
            var liveCurrent = 0;
            var workingCurrent = 0;
            if (_shadows.TryGetValue(t, out var sh))
            {
                liveCurrent = sh.LiveCurrent;
                workingCurrent = sh.WorkingCurrent;
            }

            using var ctx = _factory.CreateDbContext();
            var peak = ctx.ConcurrencyPeaks.AsNoTracking().FirstOrDefault(p => p.Tenant == tenantValue);
            var rows = ctx.ConcurrencyHours.AsNoTracking().Where(h => h.Tenant == tenantValue).ToList();

            var weeklyCutoff = nowUtc.AddDays(-7);
            var liveWeekly = 0;
            var workingWeekly = 0;
            var hourly = new List<ConcurrencyHourDto>(rows.Count);
            foreach (var r in rows)
            {
                hourly.Add(new ConcurrencyHourDto
                {
                    Hour = r.HourUtc,
                    MaxLive = r.MaxLive,
                    MaxWorking = r.MaxWorking,
                    Sessions = r.DistinctSessions,
                    Machines = r.DistinctMachines,
                    Repos = r.DistinctRepos,
                });
                if (TryParseHour(r.HourUtc, out var dt) && dt >= weeklyCutoff)
                {
                    if (r.MaxLive > liveWeekly) liveWeekly = r.MaxLive;
                    if (r.MaxWorking > workingWeekly) workingWeekly = r.MaxWorking;
                }
            }

            // Sorted HERE, ordinally, not by the database. Text ordering is a collation decision and the two
            // providers do not have to agree on one; the page's hour axis must not depend on which provider
            // served it.
            hourly.Sort((a, b) => string.CompareOrdinal(a.Hour, b.Hour));

            return new ConcurrencySnapshot(
                new ConcurrencySeriesDto
                {
                    Current = liveCurrent,
                    AllTimeMax = peak?.LiveMax ?? 0,
                    AllTimeMaxAtUtc = peak?.LiveMaxAtUtc,
                    WeeklyMax = liveWeekly,
                },
                new ConcurrencySeriesDto
                {
                    Current = workingCurrent,
                    AllTimeMax = peak?.WorkingMax ?? 0,
                    AllTimeMaxAtUtc = peak?.WorkingMaxAtUtc,
                    WeeklyMax = workingWeekly,
                },
                hourly);
        }
    }

    private static bool TryParseHour(string key, out DateTime utc) =>
        DateTime.TryParseExact(key, HourFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);

    private static void LoadPeak(GatewayStatsDbContext ctx, string tenantValue, TenantShadow sh)
    {
        var row = ctx.ConcurrencyPeaks.AsNoTracking().FirstOrDefault(p => p.Tenant == tenantValue);
        if (row is not null)
        {
            sh.LiveMax = row.LiveMax;
            sh.WorkingMax = row.WorkingMax;
        }
        sh.PeakLoaded = true;
        FileLog.Write($"[GatewaySessionConcurrencyStore] LoadPeak: tenant={tenantValue} liveMax={sh.LiveMax} workingMax={sh.WorkingMax} (row={(row is null ? "none" : "found")})");
    }

    // Move the shadow onto a new clock hour: clear the dedup sets, rebuild them and this hour's row from the
    // store, and DISCARD every member row this tenant holds for any other hour.
    //
    // The discard is what makes this a port rather than an improvement, and it is worth reading twice. The
    // JSON store held exactly ONE hour's dedup sets - three lists in the file, beside a single
    // CurrentHourKey - and it cleared them whenever the observed hour differed from that key. That test is
    // "different", not "later", so an observation for an EARLIER hour cleared them too: a returning hour
    // started its distinct counts from nothing, and because the stored counts are maxima that only grow, its
    // stored number did not move. Keeping every hour's members and unioning them would report a HIGHER
    // number than the file store did for that sequence - a better answer, and the wrong one here. Parity is
    // the bar; if the union is what we want, it is asked for as a change on its own merits, where the owner
    // can see a number move and knows why. So the member table holds the current hour and nothing else,
    // which is precisely what the three lists in the file were.
    //
    // Rebuilding the current hour's sets from the table is what makes a restart mid-hour keep counting the
    // same hour rather than starting its distinct counts again - also exactly what the JSON store's Load did.
    private void RollHour(GatewayStatsDbContext ctx, string tenantValue, TenantShadow sh, string key)
    {
        sh.CurrentHourKey = key;
        sh.CurSessions.Clear();
        sh.CurMachines.Clear();
        sh.CurRepos.Clear();

        var row = ctx.ConcurrencyHours.AsNoTracking().FirstOrDefault(h => h.Tenant == tenantValue && h.HourUtc == key);
        sh.HourExists = row is not null;
        sh.HourMaxLive = row?.MaxLive ?? 0;
        sh.HourMaxWorking = row?.MaxWorking ?? 0;
        sh.HourDistinctSessions = row?.DistinctSessions ?? 0;
        sh.HourDistinctMachines = row?.DistinctMachines ?? 0;
        sh.HourDistinctRepos = row?.DistinctRepos ?? 0;

        // The RAW strings go straight back into the sets whose comparers decide identity. Two rows differing
        // only in case collapse to one machine here, which is the whole reason the table is allowed to hold
        // both - see ConcurrencyHourMemberEntity.
        var members = ctx.ConcurrencyHourMembers.AsNoTracking()
            .Where(m => m.Tenant == tenantValue && m.HourUtc == key)
            .Select(m => new { m.Kind, m.MemberId })
            .ToList();

        // Everything this tenant holds for any OTHER hour goes now, at the same moment the file store
        // cleared its lists. Deliberately unconditional and in both directions of travel.
        var discarded = ctx.Database.ExecuteSqlRaw(StatementsFor(ctx).DeleteMembersOfOtherHours, tenantValue, key);

        foreach (var m in members)
        {
            switch (m.Kind)
            {
                case ConcurrencyMemberKinds.Session: sh.CurSessions.Add(m.MemberId); break;
                case ConcurrencyMemberKinds.Machine: sh.CurMachines.Add(m.MemberId); break;
                case ConcurrencyMemberKinds.Repo: sh.CurRepos.Add(m.MemberId); break;
                default:
                    throw new InvalidOperationException(
                        $"concurrency_hour_member holds an unknown kind '{m.Kind}' for tenant {tenantValue} hour {key}. " +
                        "The only kinds this store writes are 'session', 'machine' and 'repo'; a fourth means " +
                        "something else has written to this table.");
            }
        }

        FileLog.Write($"[GatewaySessionConcurrencyStore] RollHour: tenant={tenantValue} hour={key} rowExists={sh.HourExists} rehydrated sessions={sh.CurSessions.Count} machines={sh.CurMachines.Count} repos={sh.CurRepos.Count} discardedOtherHourMembers={discarded}");
    }

    private static void InsertMembers(GatewayStatsDbContext ctx, Statements sql, string tenantValue, string key,
        List<(string Kind, string Member)> members)
    {
        for (var offset = 0; offset < members.Count; offset += MemberInsertBatchRows)
        {
            var count = Math.Min(MemberInsertBatchRows, members.Count - offset);
            var text = new StringBuilder(sql.MemberInsertHead);
            // Placeholder {0} is the tenant and {1} the hour - both shared by every row in the batch - then
            // one (kind, member) parameter pair per row.
            var parameters = new object[2 + (count * 2)];
            parameters[0] = tenantValue;
            parameters[1] = key;
            for (var i = 0; i < count; i++)
            {
                var kindIndex = 2 + (i * 2);
                if (i > 0) text.Append(", ");
                text.Append("({0}, {1}, {").Append(kindIndex).Append("}, {").Append(kindIndex + 1).Append("})");
                parameters[kindIndex] = members[offset + i].Kind;
                parameters[kindIndex + 1] = members[offset + i].Member;
            }
            text.Append(sql.MemberInsertTail);
            ctx.Database.ExecuteSqlRaw(text.ToString(), parameters);
        }
    }

    // Retention: 90 days of hour buckets, and the member rows go with their hour. Pruned on write, exactly
    // as the JSON store pruned before each save.
    private static void Prune(GatewayStatsDbContext ctx, Statements sql, string tenantValue, DateTime nowUtc)
    {
        var cutoff = nowUtc.AddDays(-RetentionDays);
        var cutoffHourStart = new DateTime(cutoff.Year, cutoff.Month, cutoff.Day, cutoff.Hour, 0, 0, DateTimeKind.Utc);

        // The JSON store dropped a bucket when the START of its hour was before the cutoff INSTANT. The hour
        // key names the start of the hour, so when the cutoff falls part-way through an hour that hour is
        // itself stale and the comparison has to include it; when the cutoff lands exactly on the hour it
        // does not. Getting this wrong keeps (or drops) exactly one bucket more than the file store did,
        // which is a visible row on the chart.
        var inclusive = cutoff > cutoffHourStart;
        var cutoffKey = HourKey(cutoffHourStart);

        // A plain text range is a correct time range here because every key this store writes is the
        // fixed-width zero-padded yyyy-MM-ddTHH form, so text order is time order. Nothing else writes these
        // tables, so there is no free-text key to mis-sort.
        var hourDeleted = ctx.Database.ExecuteSqlRaw(inclusive ? sql.PruneHourInclusive : sql.PruneHourExclusive, tenantValue, cutoffKey);
        var memberDeleted = ctx.Database.ExecuteSqlRaw(inclusive ? sql.PruneMemberInclusive : sql.PruneMemberExclusive, tenantValue, cutoffKey);
        if (hourDeleted > 0 || memberDeleted > 0)
            FileLog.Write($"[GatewaySessionConcurrencyStore] Prune: tenant={tenantValue} cutoff={cutoffKey} inclusive={inclusive} hours={hourDeleted} members={memberDeleted}");
    }

    // ---- the statements ----

    /// <summary>The store's SQL, built once from the mapped model so a table or column rename cannot leave a
    /// statement pointing at a name that no longer exists.</summary>
    private sealed class Statements
    {
        public required string UpsertPeak { get; init; }
        public required string UpsertHour { get; init; }
        public required string MemberInsertHead { get; init; }
        public required string MemberInsertTail { get; init; }
        public required string PruneHourExclusive { get; init; }
        public required string PruneHourInclusive { get; init; }
        public required string PruneMemberExclusive { get; init; }
        public required string PruneMemberInclusive { get; init; }
        public required string DeleteMembersOfOtherHours { get; init; }
    }

    private Statements StatementsFor(GatewayStatsDbContext ctx)
    {
        if (_statements is not null) return _statements;

        var peak = TableRef(ctx, typeof(ConcurrencyPeakEntity));
        var hour = TableRef(ctx, typeof(ConcurrencyHourEntity));
        var member = TableRef(ctx, typeof(ConcurrencyHourMemberEntity));

        // The ONE dialect difference in this store: PostgreSQL spells the two-argument maximum GREATEST,
        // SQLite spells the same scalar function MAX. Everything else - INSERT ... ON CONFLICT (key) DO
        // UPDATE, the excluded pseudo-table, DO NOTHING - is the same text on both. A third provider is not
        // silently accommodated: it fails loud, because a store whose whole correctness argument is "the
        // maximum is computed by the database, atomically" cannot guess at another dialect's spelling.
        string greatest;
        if (ctx.Database.IsNpgsql()) greatest = "GREATEST";
        else if (ctx.Database.IsSqlite()) greatest = "MAX";
        else
            throw new InvalidOperationException(
                $"The concurrency store runs on SQLite or PostgreSQL; this context is on '{ctx.Database.ProviderName}'. " +
                "Its high-water writes are provider-specific upserts and there is no provider-neutral fallback " +
                "for them - add the dialect explicitly.");

        // Note on the conflict target reference: the INSERT names the table schema-qualified, but the DO
        // UPDATE clause must refer to the existing row by the BARE table name. PostgreSQL rejects a
        // schema-qualified reference there.
        //
        // Note on evaluation: every expression on the right of SET reads the row as it was BEFORE this
        // statement, on both providers, so the order of the assignments below does not matter - the
        // timestamp's CASE still sees the old maximum even though the maximum is assigned above it.
        //
        // The timestamp on a first insert is written only when the value it belongs to is above zero. That
        // is what the file store did (a peak had to beat an initial zero to stamp a time), so a tenant whose
        // roster has only ever been empty reports a null instant rather than a made-up one.
        // Written as $$""" raw strings so the {0}, {1} ... parameter placeholders stand as plain text and the
        // doubled braces are the interpolations. The alternative spelling escapes every placeholder and is
        // unreadable in exactly the statements that most need reading.
        var upsertPeak = $$"""
            INSERT INTO {{peak.Qualified}} (tenant, live_max, live_max_at_utc, working_max, working_max_at_utc)
            VALUES ({0}, {1}, CASE WHEN {1} > 0 THEN {3} ELSE NULL END, {2}, CASE WHEN {2} > 0 THEN {3} ELSE NULL END)
            ON CONFLICT (tenant) DO UPDATE SET
                live_max_at_utc = CASE WHEN excluded.live_max > {{peak.Bare}}.live_max THEN excluded.live_max_at_utc ELSE {{peak.Bare}}.live_max_at_utc END,
                live_max = {{greatest}}(excluded.live_max, {{peak.Bare}}.live_max),
                working_max_at_utc = CASE WHEN excluded.working_max > {{peak.Bare}}.working_max THEN excluded.working_max_at_utc ELSE {{peak.Bare}}.working_max_at_utc END,
                working_max = {{greatest}}(excluded.working_max, {{peak.Bare}}.working_max)
            """;

        var upsertHour = $$"""
            INSERT INTO {{hour.Qualified}} (tenant, hour_utc, max_live, max_working, distinct_sessions, distinct_machines, distinct_repos)
            VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6})
            ON CONFLICT (tenant, hour_utc) DO UPDATE SET
                max_live = {{greatest}}(excluded.max_live, {{hour.Bare}}.max_live),
                max_working = {{greatest}}(excluded.max_working, {{hour.Bare}}.max_working),
                distinct_sessions = {{greatest}}(excluded.distinct_sessions, {{hour.Bare}}.distinct_sessions),
                distinct_machines = {{greatest}}(excluded.distinct_machines, {{hour.Bare}}.distinct_machines),
                distinct_repos = {{greatest}}(excluded.distinct_repos, {{hour.Bare}}.distinct_repos)
            """;

        _statements = new Statements
        {
            UpsertPeak = upsertPeak,
            UpsertHour = upsertHour,
            // Membership is insert-if-absent: the row IS the fact, and a second container seeing the same
            // session in the same hour has nothing to add. DO NOTHING, never a read-then-insert.
            MemberInsertHead = $"INSERT INTO {member.Qualified} (tenant, hour_utc, kind, member_id) VALUES ",
            MemberInsertTail = " ON CONFLICT (tenant, hour_utc, kind, member_id) DO NOTHING",
            PruneHourExclusive = $"DELETE FROM {hour.Qualified} WHERE tenant = {{0}} AND hour_utc < {{1}}",
            PruneHourInclusive = $"DELETE FROM {hour.Qualified} WHERE tenant = {{0}} AND hour_utc <= {{1}}",
            PruneMemberExclusive = $"DELETE FROM {member.Qualified} WHERE tenant = {{0}} AND hour_utc < {{1}}",
            PruneMemberInclusive = $"DELETE FROM {member.Qualified} WHERE tenant = {{0}} AND hour_utc <= {{1}}",
            // The dedup sets belong to ONE hour, so the members of any other hour are discarded the moment
            // the hour changes - in either direction. See RollHour for why this is the port and not a
            // shortcut.
            DeleteMembersOfOtherHours = $"DELETE FROM {member.Qualified} WHERE tenant = {{0}} AND hour_utc <> {{1}}",
        };
        return _statements;
    }

    /// <summary>The mapped table name, both schema-qualified (for the INSERT target) and bare (for the
    /// conflict clause's reference to the existing row), read from the model rather than written out again.</summary>
    private static (string Qualified, string Bare) TableRef(GatewayStatsDbContext ctx, Type clrType)
    {
        var entity = ctx.Model.FindEntityType(clrType)
            ?? throw new InvalidOperationException($"{clrType.Name} is not mapped on the statistics context.");
        var table = entity.GetTableName()
            ?? throw new InvalidOperationException($"{clrType.Name} has no table name on the statistics context.");
        var schema = entity.GetSchema();
        return (string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}", table);
    }
}
