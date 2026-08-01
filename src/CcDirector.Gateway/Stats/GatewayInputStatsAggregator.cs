using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Stats.Data;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// The Gateway's durable, always-available aggregate of the DevThrottle Stats input tally. Every session's
/// per-session tally (submitted turns + character volume by modality and surface) rides up the existing
/// director-stream snapshot/delta path on <see cref="SessionDto.InputStats"/>; this aggregator folds them
/// into all-time totals the private Gateway dashboard reads with no cloud round-trip.
///
/// Correct across BOTH a Director restart and a Gateway restart, with no double-counting, via a high-water
/// increment: for each live session it remembers the last per-bucket counts it saw, and adds only the
/// increase. A session that ends (Forget) leaves its contribution in the totals and its high-water entry is
/// dropped. A session whose reported counts DROP (a Director restarted and the session began a fresh tally)
/// is treated as new activity from zero.
///
/// Only counts ever pass through here - never the text of anything typed or said (mission decision 5).
///
/// STORAGE (mission "SQLite on the Gateway", Phase 1). This used to be six in-memory dictionaries persisted
/// by rewriting one JSON document in full on every counter move, on the GET /sessions request path. It is
/// now rows in gateway-stats.db. Three things follow:
///
///   1. THE OLD NUMBERS ARE NOT CARRIED ACROSS. The owner ruled it: on first run
///      gateway-input-stats.json is renamed aside UNREAD and this store starts empty. There is no import,
///      no baseline and no parity check. Renamed, never deleted - throwing the data away was his call;
///      destroying it irreversibly was not. Consequence he accepted: session_highwater starts empty, so the
///      first roster poll folds each live session's whole current tally as fresh activity and day one lands
///      with a lump rather than at zero. Then it counts normally.
///   2. AGGREGATES ARE QUERIES, NEVER CACHED. Every total, count and sum below is a real query. Caching one
///      would rebuild the in-memory-dictionaries-plus-a-file design this mission exists to delete.
///   3. THE MIRROR HOLDS MEMBERSHIP AND IDENTITY, NEVER A TALLY (Decision 6). The high-water maps, the
///      distinct-session sets and the repository/agent identity maps are mirrored in memory, populated FROM
///      the database at startup and never the reverse. They answer exactly one question - "have I already
///      recorded this?" - which is what keeps an IDLE roster poll at zero writes, exactly as it is today.
///      The mirror only advances AFTER the write commits, so a failed write is a loud failure retried on the
///      next poll, never a silent mirror-only success.
///
/// MTR-08 (production-readiness census rows 49-67): EVERY tally, membership set and identity is now keyed by
/// the OWNING TENANT as well as its old key, and every stored row carries the tenant. Before this the whole
/// aggregator was process-global: two tenants pushing the SAME bare session id shared one high-water entry
/// (so one could suppress the other's first-seen/seed accounting), and two tenants that drove the same
/// "owner/repo" repository, agent or model shared one surrogate identity (so their turns coalesced into one
/// total). The tenant is carried from the Director-statistics ingress - the same authenticated tenant the
/// tunnel/DirectorHub binds - stamped by the producer and required by the reader. On the single-tenant self
/// host there is exactly one tenant (<see cref="TenantId.Local"/>), so the composite key degenerates to the
/// bare key it was and behavior is unchanged; on hosted the private dashboard that reads this is refused in
/// whole (issue #1848), so a reader here only ever runs for Local. A null/invalid tenant is a DENY, never a
/// default: the producer paths pass the bound tenant, and the reader passes the request tenant.
/// </summary>
public sealed class GatewayInputStatsAggregator : IDisposable
{
    // Both constants live with the write path now (StatsHourKey, GatewayStatsAggregatorKeys) and are aliased
    // here rather than restated: the hour format and the meta key are the same fact on both sides of the
    // store, and two spellings of one fact drift.
    private const string HourFormat = StatsHourKey.Format;
    private const string AgentsSinceKey = GatewayStatsAggregatorKeys.AgentsSince;

    // The self-host file, or NULL on hosted. Null is not a degraded state here: it says this aggregator was
    // built over a context factory rather than over a local database, which is the hosted shape. Everything
    // that reads or writes goes through _contexts either way; this field exists only for the two things that
    // are genuinely about a FILE - retiring the legacy JSON document beside it, and naming the path in the
    // startup log.
    private readonly GatewayStatsDatabase? _db;
    private readonly bool _ownsDatabase;
    private readonly object _lock = new();

    // Every READ below goes through this - one provider-neutral Entity Framework model serving SQLite today
    // and PostgreSQL when the hosted wiring lands, rather than a second hand-written dialect of each query.
    // A fresh context per operation; the connection underneath is the one GatewayStatsDatabase owns, so a
    // read sees exactly what the write path has committed to it.
    private readonly IDbContextFactory<GatewayStatsDbContext> _contexts;

    // The write path. Every row this class stores goes through it, on Entity Framework, with an explicit
    // upsert on every high-water and membership write - see GatewayStatsWriter for why that is not
    // negotiable.
    private readonly GatewayStatsWriter _writer;

    /// <summary>This aggregator's own health counters, which the hot-path call sites record a contained
    /// failure on - see <see cref="StatsObservation"/>. This type does NOT contain its own failures: an
    /// observation that cannot be stored still throws to its caller, and the caller decides whether that is
    /// fatal to it. The roster read and the tunnel push decide it is not.</summary>
    public StatsFailureCounters Health { get; } = new("input-stats");

    // ---- The mirror. Membership and identity ONLY - never a tally (Decision 6). ----
    //
    // MTR-08: every map below is keyed by (tenant, ...) so one tenant's membership and identity can never be
    // read, suppressed, or coalesced by another. The session-keyed maps carry the tenant in a composite tuple
    // key (default tuple equality is Ordinal on the tenant value and Ordinal on the session id - exactly the
    // StringComparer.Ordinal they used before). The identity display->id maps nest a per-tenant dictionary so
    // the OrdinalIgnoreCase comparer that DECIDES identity is preserved byte-for-byte per tenant; the id->
    // display maps stay flat because a surrogate id is globally unique (AUTOINCREMENT) and already names its
    // tenant's row.

    private readonly Dictionary<(TenantId Tenant, string SessionId), Dictionary<(string Modality, string Surface), Counters>> _highWater = new();
    private readonly Dictionary<(TenantId Tenant, string SessionId), Counters> _agentDrivenHighWater = new();
    private readonly HashSet<(TenantId Tenant, string SessionId)> _wingmanSessions = new();

    // The last cumulative token spend seen per session (issue #1637), so only the INCREASE folds - the same
    // high-water discipline as _highWater, one dimension over. A dropped count is a restart and folds fresh
    // from zero.
    private readonly Dictionary<(TenantId Tenant, string SessionId), TokenCounters> _tokenHighWater = new();

    // Sessions whose already-counted turns have been attributed to their agent (issue #1633). MUST persist:
    // session_highwater survives a restart, so without this the back-fill would run a second time against a
    // non-empty high-water and double every agent's numbers.
    private readonly HashSet<(TenantId Tenant, string SessionId)> _agentsSeeded = new();

    // Display spelling -> surrogate id, PER TENANT (MTR-08). THE INNER COMPARER IS THE WHOLE REASON THE SCHEMA
    // USES SURROGATE IDS: it is the same StringComparer.OrdinalIgnoreCase the old dictionaries used, so
    // grouping is decided by the identical object with identical semantics and SQLite is never asked to
    // compare a repository, agent or model string. First-seen-wins on the display spelling, which is what a
    // Dictionary does. Nesting per tenant keeps that comparer exact while making tenant A's "owner/repo" a
    // DIFFERENT surrogate id than tenant B's, so their turns cannot coalesce.
    private readonly Dictionary<TenantId, Dictionary<string, long>> _repoIds = new();
    private readonly Dictionary<TenantId, Dictionary<string, long>> _agentIds = new();
    private readonly Dictionary<TenantId, Dictionary<string, long>> _modelIds = new();
    // The checkout (local working directory) dimension retained beside the repository (issue: group the
    // Repos page by repository). repo_id is now the repo name, so worktrees and per-machine clones
    // of one repository share a repo_id; checkout_id keeps the path each turn actually ran in so it is not
    // lost. Same first-seen-wins OrdinalIgnoreCase identity as the others, per tenant.
    private readonly Dictionary<TenantId, Dictionary<string, long>> _checkoutIds = new();
    // id -> display stays flat: a surrogate id is globally unique and belongs to exactly one tenant's row.
    private readonly Dictionary<long, string> _repoDisplay = new();
    private readonly Dictionary<long, string> _agentDisplay = new();
    private readonly Dictionary<long, string> _modelDisplay = new();
    private readonly Dictionary<long, string> _checkoutDisplay = new();

    // The distinct-session sets, keyed by (tenant, surrogate id, session id) - the same (tenant, ...) shape as
    // every other mirror in this class.
    //
    // They used to be keyed by (id, session id) alone, on the argument that the id is already tenant-specific
    // so the tenant adds nothing. That argument is TRUE about the values and WRONG about the safety it buys:
    // it made this the one mirror that could be consulted, and populated, without naming a tenant, and the
    // query that filled it read every tenant's rows. Nothing leaked, because both call sites only ever looked
    // up an id they had already obtained from a tenant-filtered source - which is safety by every call site
    // remembering, exactly the shape that produced the missions leak (devthrottle_internal#1039). Carrying the
    // tenant in the key means the set cannot be consulted for a tenant that was not named, so there is nothing
    // left to remember.
    private readonly HashSet<(TenantId Tenant, long Id, string SessionId)> _repoSessions = new();
    private readonly HashSet<(TenantId Tenant, long Id, string SessionId)> _agentSessions = new();

    // When the per-agent tally started counting, PER TENANT (MTR-08): each tenant's first fold stamps its own
    // start, so the Agents page states the window for THAT tenant rather than a fleet-global first observation.
    private readonly Dictionary<TenantId, string> _agentsSinceUtc = new();
    // When the model DIMENSION started recording. A schema fact (stamped once by the version-2 migration),
    // not a per-tenant statistic, so it stays a single value read tenant-agnostically.
    private string _modelsSinceUtc = "";

    /// <summary>Statements executed through the raw helpers that used to sit at the bottom of this file.
    /// PERMANENTLY ZERO: those helpers are gone (issue #1174 - see the note where they were), because a
    /// SQLite command builder cannot serve a hosted Gateway that has no SQLite connection.
    ///
    /// It is kept, at zero, rather than deleted, because <see cref="StatementsExecuted"/> is the sum and the
    /// term being visibly zero is the honest statement that the raw path executed nothing. It was never a
    /// count of total database traffic - the read projections, the mirror load and the since-stamps all
    /// execute through Entity Framework and were never counted here.</summary>
    internal long RawStatementsExecuted => 0;

    /// <summary>Every statement executed against the database, reads here plus every write the
    /// <see cref="GatewayStatsWriter"/> made. The seam acceptance criterion 3 measures it: an IDLE poll must
    /// not move it at all, and a fold must move it by an amount bounded by what CHANGED, never by how much
    /// history is stored.</summary>
    internal long StatementsExecuted => RawStatementsExecuted + _writer.StatementsExecuted;

    private sealed class Counters
    {
        public long Turns { get; set; }
        public long Characters { get; set; }

        /// <summary>Which INCARNATION of the session's tally this was read from. Advances when the store
        /// adopts a reset. Held so the fold can tell the writer which life its reading belongs to - a
        /// straggling reading from a life that has already ended must count for nothing.</summary>
        public long Generation { get; set; }
    }

    /// <summary>The four CUMULATIVE, additive token spend counts a session carries. Context occupancy is not
    /// here: it is a gauge, not spend, and never enters this arithmetic.</summary>
    private sealed class TokenCounters
    {
        public long Input { get; set; }
        public long Output { get; set; }
        public long CacheRead { get; set; }
        public long CacheCreation { get; set; }

        /// <summary>The incarnation this spend was read from. See <see cref="Counters.Generation"/>.</summary>
        public long Generation { get; set; }
    }

    // IdentityKind moved to CcDirector.Gateway.Stats.Data when the write path became its own type: the fold,
    // the batch and the writer all have to name the same four kinds, and a kind nested inside this class
    // could not be one of them.

    private Dictionary<TenantId, Dictionary<string, long>> OuterIdsFor(IdentityKind kind) => kind switch
    {
        IdentityKind.Repo => _repoIds,
        IdentityKind.Agent => _agentIds,
        IdentityKind.Model => _modelIds,
        IdentityKind.Checkout => _checkoutIds,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown identity kind."),
    };

    // The per-tenant display->id map for one kind, created on first use with the OrdinalIgnoreCase comparer
    // that is the whole point of the surrogate scheme. Creating an empty map for a read-only probe is
    // harmless - it just answers "no identity yet for this tenant".
    private Dictionary<string, long> IdsFor(TenantId tenant, IdentityKind kind)
    {
        var outer = OuterIdsFor(kind);
        if (!outer.TryGetValue(tenant, out var inner))
        {
            inner = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            outer[tenant] = inner;
        }
        return inner;
    }

    private Dictionary<long, string> DisplayFor(IdentityKind kind) => kind switch
    {
        IdentityKind.Repo => _repoDisplay,
        IdentityKind.Agent => _agentDisplay,
        IdentityKind.Model => _modelDisplay,
        IdentityKind.Checkout => _checkoutDisplay,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown identity kind."),
    };

    // The distinct-session sets exist for repositories and agents only. A model and a checkout have none,
    // deliberately, so asking for one is a programming error and says so rather than inventing an empty answer.
    private HashSet<(TenantId Tenant, long Id, string SessionId)> SessionsFor(IdentityKind kind) => kind switch
    {
        IdentityKind.Repo => _repoSessions,
        IdentityKind.Agent => _agentSessions,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind,
            "Only repositories and agents keep distinct-session sets."),
    };

    /// <param name="path">The statistics database. Defaults to gateway-stats.db under the cc-director
    /// storage root, beside the store it replaces.</param>
    public GatewayInputStatsAggregator(string? path = null)
        : this(new GatewayStatsDatabase(path), ownsDatabase: true)
    {
    }

    /// <param name="database">An already-open database. The caller keeps ownership - Phase 2 puts the
    /// concurrency store on this same file.</param>
    public GatewayInputStatsAggregator(GatewayStatsDatabase database)
        : this(database, ownsDatabase: false)
    {
    }

    /// <summary>
    /// THE HOSTED CONSTRUCTOR (issue #1174). Takes the context factory the statistics store published -
    /// pooled Npgsql on hosted - and holds NO file and NO connection of its own.
    ///
    /// This is the constructor that makes <c>/stats</c> and <c>/stats/data</c> answer on a hosted Gateway.
    /// Before it existed the only way to build this type was over a SQLite file, and a hosted Gateway never
    /// opens one by design, so the read path had nothing to read and the two routes answered a named 503
    /// however healthy the PostgreSQL store was. The store's own remarks call that out; this closes it.
    ///
    /// Everything below this line behaves identically on both providers because everything below this line
    /// goes through <see cref="_contexts"/>. There is no self-host branch inside this class and there must
    /// not become one: the difference between the deployments is WHICH FACTORY the caller passes, decided
    /// once at construction, and that is the whole difference.
    ///
    /// There is no legacy-JSON retirement on this path. That step renames a file beside the SQLite database,
    /// and a hosted Gateway has neither the file nor the directory - running it would be looking for a
    /// self-host artifact in a place it cannot exist.
    /// </summary>
    /// <param name="contexts">The statistics context factory, from <c>GatewayStatsStore.Factory</c>. The
    /// caller keeps ownership of whatever is underneath it.</param>
    public GatewayInputStatsAggregator(IDbContextFactory<GatewayStatsDbContext> contexts)
    {
        _db = null;
        _ownsDatabase = false;
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _writer = new GatewayStatsWriter(_contexts);
        LoadMirror();
    }

    private GatewayInputStatsAggregator(GatewayStatsDatabase database, bool ownsDatabase)
    {
        _db = database;
        _ownsDatabase = ownsDatabase;
        // The reads run on Entity Framework over the SAME open connection this database owns - the self-host
        // arm of the provider choice the hosted constructor above makes the other way.
        _contexts = new GatewayStatsSqliteContextFactory(database.Connection);
        // The write path runs on Entity Framework over the SAME open file this class reads through, so the
        // self-host store keeps its single connection and its single writer while the statements that touch
        // it become provider-neutral. The hosted Gateway hands the identical writer a pooled Npgsql factory.
        _writer = new GatewayStatsWriter(_contexts);
        RetireLegacyJsonStore();
        LoadMirror();
    }

    // The owner's ruling: the old numbers are not carried across. The document is renamed aside WITHOUT
    // being read - no parse, no import, no parity. Renamed and never deleted: throwing the data away was his
    // call, destroying it irreversibly was not, and a rename costs nothing and keeps the door open.
    //
    // Self-idempotent by nature: after the rename the file is gone, so there is nothing to mark done and no
    // marker to strand.
    private void RetireLegacyJsonStore()
    {
        if (_db is null) return;
        var legacy = Path.Combine(Path.GetDirectoryName(_db.Path) ?? CcStorage.Root(), "gateway-input-stats.json");
        if (!File.Exists(legacy)) return;

        var aside = legacy + ".retired-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        File.Move(legacy, aside);
        FileLog.Write($"[GatewayInputStatsAggregator] RetireLegacyJsonStore: the old numbers are not carried " +
                      $"across (owner ruling); renamed {legacy} to {aside} UNREAD; starting empty");
    }

    // Populate the mirror FROM the database - never the reverse. Once, at startup.
    private void LoadMirror()
    {
        lock (_lock)
        {
            // Every row carries its owning tenant (MTR-08), so the mirror is rebuilt per tenant - exactly the
            // partition that was written. A surrogate id lands in the flat id->display map (ids are globally
            // unique) AND in its tenant's display->id map.
            //
            // THESE TEN READS WERE RAW SQLite STATEMENTS AND ARE NOW ENTITY FRAMEWORK, for one reason: the
            // hosted Gateway has no SQLite connection to run them on. A mirror that can only be loaded from a
            // file is a read path that can only serve self-host, which is exactly the state issue #1174
            // describes. Same tables, same columns, same order; the provider is now whatever the caller's
            // context factory is pointed at.
            using (var db = _contexts.CreateDbContext())
            {
                foreach (var row in db.SessionHighwater
                             .Select(h => new { h.Tenant, h.SessionId, h.Modality, h.Surface, h.Turns, h.Chars, h.Generation }))
                {
                    var key = (new TenantId(row.Tenant), row.SessionId);
                    if (!_highWater.TryGetValue(key, out var hw))
                    {
                        hw = new Dictionary<(string, string), Counters>();
                        _highWater[key] = hw;
                    }
                    hw[(row.Modality, row.Surface)] = new Counters { Turns = row.Turns, Characters = row.Chars, Generation = row.Generation };
                }

                foreach (var row in db.AgentDrivenHighwater
                             .Select(h => new { h.Tenant, h.SessionId, h.Turns, h.Chars, h.Generation }))
                    _agentDrivenHighWater[(new TenantId(row.Tenant), row.SessionId)] =
                        new Counters { Turns = row.Turns, Characters = row.Chars, Generation = row.Generation };

                foreach (var row in db.TokenHighwater
                             .Select(h => new
                             {
                                 h.Tenant, h.SessionId, h.InputTokens, h.OutputTokens,
                                 h.CacheReadTokens, h.CacheCreationTokens, h.Generation,
                             }))
                    _tokenHighWater[(new TenantId(row.Tenant), row.SessionId)] = new TokenCounters
                    {
                        Input = row.InputTokens, Output = row.OutputTokens, CacheRead = row.CacheReadTokens,
                        CacheCreation = row.CacheCreationTokens, Generation = row.Generation,
                    };

                foreach (var row in db.WingmanSessions.Select(w => new { w.Tenant, w.SessionId }))
                    _wingmanSessions.Add((new TenantId(row.Tenant), row.SessionId));

                foreach (var row in db.AgentsSeeded.Select(a => new { a.Tenant, a.SessionId }))
                    _agentsSeeded.Add((new TenantId(row.Tenant), row.SessionId));

                foreach (var row in db.RepoIdentities.Select(i => new { i.Tenant, i.RepoId, i.RepoDisplay }))
                {
                    IdsFor(new TenantId(row.Tenant), IdentityKind.Repo)[row.RepoDisplay] = row.RepoId;
                    _repoDisplay[row.RepoId] = row.RepoDisplay;
                }

                foreach (var row in db.AgentIdentities.Select(i => new { i.Tenant, i.AgentId, i.AgentDisplay }))
                {
                    IdsFor(new TenantId(row.Tenant), IdentityKind.Agent)[row.AgentDisplay] = row.AgentId;
                    _agentDisplay[row.AgentId] = row.AgentDisplay;
                }

                foreach (var row in db.ModelIdentities.Select(i => new { i.Tenant, i.ModelId, i.ModelDisplay }))
                {
                    IdsFor(new TenantId(row.Tenant), IdentityKind.Model)[row.ModelDisplay] = row.ModelId;
                    _modelDisplay[row.ModelId] = row.ModelDisplay;
                }

                foreach (var row in db.CheckoutIdentities.Select(i => new { i.Tenant, i.CheckoutId, i.CheckoutDisplay }))
                {
                    IdsFor(new TenantId(row.Tenant), IdentityKind.Checkout)[row.CheckoutDisplay] = row.CheckoutId;
                    _checkoutDisplay[row.CheckoutId] = row.CheckoutDisplay;
                }
            }
            // repo_session and agent_session carry NO tenant column at schema version 5 - they are partitioned
            // indirectly, through a surrogate id minted per tenant. So the tenant the mirror key needs comes
            // from the identity table the id belongs to, by an explicit join: it is read FROM THE DATA, not
            // supplied by a caller, which is why the absence of a request tenant at startup is irrelevant here.
            //
            // This IS a whole-table read across every tenant, and it has to be: the mirror is loaded once at
            // startup, before any request has named a tenant, so there is no tenant available to filter by. The
            // join does not narrow the read - it attaches the owning tenant to each key and drops orphans. Do
            // not read it as a scaling measure and do not let it stop you looking: if startup work ever has to
            // be bounded, that needs a different loading strategy, not this join.
            //
            // It is an explicit join rather than a navigation property or a configured relationship on purpose:
            // version 5 has no foreign keys between these tables, and adding one would change insert and delete
            // ordering - a semantic change, in a port that must carry semantics forward unchanged.
            //
            // An INNER join, which drops a membership row whose surrogate id names no identity row. There is no
            // such row: both are written inside one transaction, the identity first, so the id a membership row
            // carries always exists. If one ever did exist it could only be damage, and dropping it costs
            // nothing - the mirror would simply not claim to have recorded it, and the next fold's
            // insert-if-absent would write it again. Keeping it would be worse: an entry that names no tenant.
            using (var db = _contexts.CreateDbContext())
            {
                foreach (var row in db.RepoSessions
                             .Join(db.RepoIdentities, s => s.RepoId, i => i.RepoId,
                                 (s, i) => new { i.Tenant, s.RepoId, s.SessionId }))
                    _repoSessions.Add((new TenantId(row.Tenant), row.RepoId, row.SessionId));

                foreach (var row in db.AgentSessions
                             .Join(db.AgentIdentities, s => s.AgentId, i => i.AgentId,
                                 (s, i) => new { i.Tenant, s.AgentId, s.SessionId }))
                    _agentSessions.Add((new TenantId(row.Tenant), row.AgentId, row.SessionId));
            }
            // The two since-stamps. AgentsSinceUtc serves the first of these straight out of the mirror, so
            // this read IS its query path and it belongs on the same provider-neutral model as the other
            // eleven projections - leaving it in raw SQL would make "every read is Entity Framework" a
            // sentence with an asterisk on it.
            using (var db = _contexts.CreateDbContext())
            {
                // agents_since is PER TENANT now (one row per tenant that has folded).
                foreach (var row in db.Meta.Where(m => m.Name == AgentsSinceKey)
                             .Select(m => new { m.Tenant, m.Value }))
                    _agentsSinceUtc[new TenantId(row.Tenant)] = row.Value;

                // Written by the version 2 migration, so it is present on every database this build can open -
                // the migration runs before this does. A schema fact, not per-tenant, so it is read once with
                // no tenant filter. Read into the mirror because the stats surface reports it on every request
                // and it never changes at runtime.
                var modelsSinceKey = GatewayStatsDatabase.ModelsSinceKey;
                _modelsSinceUtc = db.Meta.Where(m => m.Name == modelsSinceKey)
                    .Select(m => m.Value)
                    .FirstOrDefault() ?? "";
            }

            FileLog.Write($"[GatewayInputStatsAggregator] LoadMirror: {_highWater.Count} live session(s), " +
                          $"{_wingmanSessions.Count} wingman session(s), {_repoDisplay.Count} repo(s), {_agentDisplay.Count} agent(s), " +
                          $"{_modelDisplay.Count} model(s), {_checkoutDisplay.Count} checkout(s), {_agentsSeeded.Count} seeded, " +
                          $"{_agentsSinceUtc.Count} tenant(s) with an agents-since stamp, " +
                          $"modelsSince='{_modelsSinceUtc}' from {_db?.Path ?? "the statistics store's context factory (hosted)"}");
        }
    }

    /// <summary>
    /// Fold every session in a full snapshot into the totals and the per-hour turn log, under
    /// <paramref name="tenant"/> - the authenticated tenant the Director-statistics ingress bound. The whole
    /// snapshot belongs to one tenant (the ingress is per-tenant), so a single tenant scopes the batch.
    /// Defaults to <see cref="TenantId.Local"/> for the self-host shape and the unit-test default; the
    /// production producer paths pass the bound tenant explicitly.
    /// </summary>
    public void ObserveSnapshot(IEnumerable<SessionDto>? sessions, DateTime? nowUtc = null, TenantId? tenant = null)
    {
        if (sessions is null) return;
        var t = RequireTenant(tenant);
        var now = nowUtc ?? DateTime.UtcNow;
        lock (_lock)
        {
            var batch = new StatsWriteBatch(t, now, HourKey(now));
            StampAgentsSinceLocked(batch);
            foreach (var s in sessions) FoldLocked(s, batch);
            CommitLocked(batch);
        }
    }

    /// <summary>Fold one session (a delta) into the totals and the per-hour turn log, under
    /// <paramref name="tenant"/> (the bound ingress tenant; defaults to <see cref="TenantId.Local"/>).</summary>
    public void Observe(SessionDto? session, DateTime? nowUtc = null, TenantId? tenant = null)
    {
        if (session is null) return;
        var t = RequireTenant(tenant);
        var now = nowUtc ?? DateTime.UtcNow;
        lock (_lock)
        {
            var batch = new StatsWriteBatch(t, now, HourKey(now));
            StampAgentsSinceLocked(batch);
            FoldLocked(session, batch);
            CommitLocked(batch);
        }
    }

    // Resolve the tenant a producer or reader supplied. Null means the self-host / unit-test default (Local);
    // an explicitly-passed tenant must be valid - an invalid one is a DENY, never silently defaulted, so a
    // caller that resolved "no tenant" (hosted, unbound) can never file under a guessed owner.
    private static TenantId RequireTenant(TenantId? tenant)
    {
        if (tenant is not { } t) return TenantId.Local;
        if (!t.IsValid) throw new ArgumentException("a valid tenant is required", nameof(tenant));
        return t;
    }

    // Everything one observation wants to write is collected into a StatsWriteBatch (see that type) before
    // ANY of it is written, so the mirror is advanced only after the commit succeeds.

    private void StampAgentsSinceLocked(StatsWriteBatch batch)
    {
        // Per tenant (MTR-08): only stamp when THIS tenant has no start yet, so each account's Agents page
        // states its own window rather than a fleet-global first observation.
        if (_agentsSinceUtc.TryGetValue(batch.Tenant, out var existing) && existing.Length > 0) return;
        batch.StampAgentsSince = batch.NowUtc.ToUniversalTime()
            .ToString("o", System.Globalization.CultureInfo.InvariantCulture);
    }

    // Fold one session's tally. Caller holds the lock.
    //
    // THE CORRECTNESS CORE OF THE MISSION. It is what makes re-reading a roster safe and what lets counts
    // survive a restart without double-counting. Simplifying it breaks the mission. The ORDER below matters
    // and mirrors the original exactly - in particular the wingman registration and the agent-driven fold both
    // happen BEFORE the empty-buckets return.
    //
    // WHAT THIS METHOD NO LONGER DOES, AND WHY THAT IS THE POINT: it does not compute growth. It used to
    // subtract the mirror's counts from the reported ones and queue the difference as a row to append. That
    // made this PRIVATE MIRROR the authority on what changed, while the shared watermark those rows are
    // reconciled against was arbitrated by the DATABASE - and two authorities on one number is not a
    // near-miss, it is a guaranteed drift the moment a second writer exists. Two hosted containers each
    // measuring growth from their own mirror append more in total than the watermark ever moves, and the
    // all-time totals inflate on every interleave.
    //
    // So this reports what it SAW - the session's reported cumulative counts - together with what it BELIEVED
    // the store held. The raise statement compares the two against the row itself and returns what it actually
    // changed, and THAT is what gets appended. The general rule, which holds everywhere in this store: never
    // learn what you changed from your own prior belief; learn it from the response of whatever arbitrates.
    //
    // The mirror still earns its keep, and still holds membership and identity only (Decision 6): an
    // observation is queued only when the reported counts DIFFER from what it believes is stored, which is
    // what keeps an unchanged roster poll at zero statements.
    private void FoldLocked(SessionDto s, StatsWriteBatch batch)
    {
        if (string.IsNullOrEmpty(s.SessionId)) return;

        // Read VoiceMode ONCE and pass it down. Wingman is a property of the session's MODE for this whole
        // observation, not of any bucket, so it is constant across every row this fold writes. Reading it
        // once means no row can carry a different flag than its siblings and - the failure that actually
        // matters - the emitting code has no path to derive the flag from a bucket's own modality. A turn
        // TYPED while voice mode is on IS a wingman turn.
        var wingman = s.VoiceMode;

        // A session "uses the wingman" the moment it is seen with voice mode on - recorded even with no
        // input this fold. BEFORE the empty-buckets return, deliberately; the membership mirror is what
        // keeps it free on an idle poll. Keyed by (tenant, session id) so another tenant's same session id
        // neither counts this one nor is counted by it (MTR-08).
        if (wingman && !_wingmanSessions.Contains((batch.Tenant, s.SessionId)) && !batch.NewWingmanSessions.Contains(s.SessionId))
            batch.NewWingmanSessions.Add(s.SessionId);

        // Issue #1636: turns other agents drove into this session, on their own lane. Folded BEFORE the
        // buckets guard, because a session driven only by other agents has no human buckets at all - and
        // those are exactly the sessions this tally is about.
        FoldAgentDrivenLocked(s, batch);

        // Issue #1637: token spend, on its own lane. Folded BEFORE the buckets guard too, and for a reason
        // that is not obvious: tokens grow at turn-END while the input buckets grow at turn SUBMISSION, so a
        // poll can see this session's spend rise with no NEW bucket delta this interval (the turn was counted
        // last poll). Gating the token fold on a bucket delta would drop exactly those increases.
        FoldTokensLocked(s, batch);

        if (s.InputStats?.Buckets is null || s.InputStats.Buckets.Count == 0) return;

        _highWater.TryGetValue((batch.Tenant, s.SessionId), out var hw);
        var agentKey = s.Agent ?? "";

        // Issue #1633: the first time this session is folded, attribute what it has ALREADY counted to its
        // agent. On a fresh database this contributes nothing (the high-water is empty); after a RESTART it
        // would contribute real turns, which is why agents_seeded is persisted. Keyed by (tenant, session id)
        // so one tenant seeding a session id can never suppress another tenant's back-fill of the same id
        // (MTR-08).
        //
        // The rows are only PROPOSED here. Two writers first-folding one session both find an unseeded mirror,
        // so both would attribute the same standing count and double the agent's numbers; the writer emits
        // these only if its own agents_seeded insert is the one that claimed the row, which the statement
        // reports. Same rule as everywhere else here - the arbiter says who did what, not the belief.
        if (!_agentsSeeded.Contains((batch.Tenant, s.SessionId)))
        {
            var backfill = new List<StatsWriteBatch.AgentBackfillRow>();
            if (hw is not null)
                foreach (var (key, prior) in hw)
                {
                    RegisterAgentLocked(s, batch);
                    if (prior.Turns > 0 || prior.Characters > 0)
                        backfill.Add(new StatsWriteBatch.AgentBackfillRow(
                            agentKey, IsVoice(key.Modality), prior.Turns, prior.Characters));
                }
            batch.Seeding.Add((s.SessionId, backfill));
        }

        // The repository grouping key is a two-tier identity:
        //
        //   1. The "owner/repo" repo name (GitHub or Azure DevOps) the owning Director resolved for this
        //      checkout, when it sent one. This is the ideal key: it folds every worktree AND every per-machine
        //      clone of one repository into a single row, and it never collides two genuinely different repos
        //      that share a folder name.
        //
        //   2. Otherwise the checkout's FOLDER NAME (the last path segment), not its full path. A Director on
        //      an older build sends no repo name, so the Gateway - which has no filesystem to resolve the
        //      origin, and may be on a different machine than the checkout entirely - only has the path.
        //      Grouping by the folder name still collapses the SAME repository worked in from several machines
        //      ("D:\ReposFred\devthrottle", "/Users/soren/ReposFred/devthrottle" and "C:\ReposFred\devthrottle"
        //      are one "devthrottle" row), which is the owner's rule: the same repo is the same repo, whichever
        //      machine drove it. The trade-off is deliberate and bounded to the no-repo-name case: it does NOT
        //      fold a differently-named worktree (only the repo name can, since it reads the shared origin), and
        //      two unrelated repositories that happen to share a folder name would merge. Once the Directors are
        //      on a repo-name-emitting build, tier 1 takes over and both limitations go away.
        //
        // The checkout key stays the raw working-directory path, retained as its own dimension so the store
        // still records exactly which checkout each turn ran in - the individual paths are listed under the
        // merged row, not lost. Separators are not normalized: RepoLeaf treats both '/' and '\' as segment
        // separators, so a Windows and a POSIX checkout of one repo yield the identical folder-name key.
        var checkoutKey = s.RepoPath ?? "";
        var repoKey = !string.IsNullOrWhiteSpace(s.RepoName) ? s.RepoName! : RepoLeaf(checkoutKey);

        // The model this session's agent was last RECORDED using (issue #1637). Unlike the repository and
        // the agent, an unknown model is stored as SQL NULL rather than folded into an empty-string
        // identity: the producer reports null until the agent's own records name a model, so "not said" is a
        // real and permanent state for a session's first turn, and it is not a model named "".
        //
        // Whitespace is treated as absent too. The producer says null, but a display spelling of " " could
        // only ever become an identity row that renders as nothing - the same broken cell an empty string
        // would give, arrived at by a different route.
        var modelKey = string.IsNullOrWhiteSpace(s.CurrentModel) ? null : s.CurrentModel;

        foreach (var b in s.InputStats.Buckets)
        {
            var key = (b.Modality ?? "", b.Surface ?? "");
            Counters? prev = null;
            hw?.TryGetValue(key, out prev);

            // Nothing to report: the counts are exactly what this writer already believes the store holds, so
            // there is no observation to make and no statement to run. This one test is the whole idle-poll
            // property - the roster is re-read every few seconds and almost every bucket lands here.
            if (prev is not null && prev.Turns == b.Turns && prev.Characters == b.Characters) continue;

            // Decided HERE, in C#, with the same case-INSENSITIVE test the original uses - while the totals
            // bucket key stays case-SENSITIVE. That asymmetry is current behaviour and storing the flag keeps
            // it out of the query layer.
            batch.Buckets.Add(new StatsWriteBatch.BucketObservation(
                s.SessionId, key.Item1, key.Item2, IsVoice(key.Item1),
                repoKey, checkoutKey, modelKey, wingman, agentKey,
                b.Turns, b.Characters, prev?.Turns ?? 0, prev?.Characters ?? 0, prev?.Generation ?? 0));

            // A bucket with nothing in it earns no identity and no membership. Whether it contributes a DELTA
            // is the database's ruling, not this method's, but a session that has never submitted a turn at
            // all cannot contribute one under any ruling - so the identities stay unqueued and an empty
            // session does not appear on the Repos page as a session that did nothing.
            if (b.Turns <= 0 && b.Characters <= 0) continue;

            NeedIdentity(repoKey, IdentityKind.Repo, batch);
            if (!KnownIdentitySession(repoKey, s.SessionId, IdentityKind.Repo, batch))
                batch.NewIdentitySessions.Add((repoKey, s.SessionId, IdentityKind.Repo));

            // The checkout the turn ran in earns an identity, retained beside the repository. No
            // distinct-session set (the Repos page counts sessions per repository, not per checkout), so
            // it is never queued into NewIdentitySessions - SessionsFor(Checkout) would refuse it.
            NeedIdentity(checkoutKey, IdentityKind.Checkout, batch);

            // Only a model the Director actually named earns an identity. An absent model writes a null
            // model_id and creates nothing, so model_identity never grows a row for "not said".
            if (modelKey is not null)
                NeedIdentity(modelKey, IdentityKind.Model, batch);

            RegisterAgentLocked(s, batch);
        }
    }

    // The voice test, in one place. Case-INSENSITIVE on the modality, while the totals bucket key stays
    // case-SENSITIVE - an asymmetry carried forward from the original deliberately.
    private static bool IsVoice(string modality) =>
        string.Equals(modality, "voice", StringComparison.OrdinalIgnoreCase);

    // Register the agent CLI this session drives: its identity, and this session in its distinct-session set.
    //
    // It no longer carries the counts. The per-agent tally is attributed from the SAME difference the session
    // bucket got - the one the raise statement returned - because the writer builds both rows from one
    // number. Computing the agent's share here from a second subtraction is exactly how two figures that must
    // agree stop agreeing, and it would put the count back on the wrong side of the arbitration.
    //
    // The agent tally still has its OWN table rather than being derived from stat_delta: the first-fold
    // back-fill has no stat_delta counterpart, so deriving it would either inflate the totals or lose the
    // attribution.
    //
    // The session id is registered even when it brought no turns this fold, so the per-agent session counts
    // describe the agents actually being run rather than only the ones that submitted a turn in the observed
    // window. An agent the Director did not report is counted under the empty key and shown as "(unknown)".
    private void RegisterAgentLocked(SessionDto s, StatsWriteBatch batch)
    {
        var agentKey = s.Agent ?? "";
        NeedIdentity(agentKey, IdentityKind.Agent, batch);

        if (!string.IsNullOrEmpty(s.SessionId) && !KnownIdentitySession(agentKey, s.SessionId, IdentityKind.Agent, batch))
            batch.NewIdentitySessions.Add((agentKey, s.SessionId, IdentityKind.Agent));
    }

    // Fold the turns OTHER agents drove into this session (issue #1636) via the same high-water increment
    // the human buckets use. Attributed to the RECEIVING session's agent. These never enter the totals, the
    // hourly log or the buckets, because the human voice-versus-typed numbers must stay about the human -
    // which is why they live in their own table where they CANNOT be summed in by accident.
    private void FoldAgentDrivenLocked(SessionDto s, StatsWriteBatch batch)
    {
        var turns = s.InputStats?.AgentDrivenTurns ?? 0;
        var chars = s.InputStats?.AgentDrivenCharacters ?? 0;
        if (turns == 0 && chars == 0) return;

        _agentDrivenHighWater.TryGetValue((batch.Tenant, s.SessionId), out var prev);

        // Steady counts say nothing new, so nothing is queued and this session costs no statement. Whether
        // what IS queued turns into a delta row is the raise statement's ruling, not this method's.
        if (prev is not null && prev.Turns == turns && prev.Characters == chars) return;

        var agentKey = s.Agent ?? "";
        NeedIdentity(agentKey, IdentityKind.Agent, batch);
        batch.AgentDriven.Add(new StatsWriteBatch.AgentDrivenObservation(
            s.SessionId, agentKey, turns, chars, prev?.Turns ?? 0, prev?.Characters ?? 0, prev?.Generation ?? 0));
        if (!string.IsNullOrEmpty(s.SessionId) && !KnownIdentitySession(agentKey, s.SessionId, IdentityKind.Agent, batch))
            batch.NewIdentitySessions.Add((agentKey, s.SessionId, IdentityKind.Agent));
    }

    // Fold this session's cumulative token spend (issue #1637) via the same high-water increment the human
    // buckets use: store only the GROWTH since the last poll, and treat a DROP - a Director that restarted
    // the session with a fresh conversation - as fresh spend from zero, never a negative. Attributed to the
    // hour and to the model the session was recorded running, on token_delta's own lane. NO modality or
    // surface: tokens are the model's work, not the human's input channel (see MigrateToVersion3).
    private void FoldTokensLocked(SessionDto s, StatsWriteBatch batch)
    {
        var t = s.TokenTotals;
        if (t is null) return;

        _tokenHighWater.TryGetValue((batch.Tenant, s.SessionId), out var prev);

        // Steady spend says nothing new, so an idle re-poll of a quiet session writes nothing - the same
        // silence the other two high-waters protect. All four scalars are running sums over one transcript,
        // and each is raised and judged on its own by the statement, which is simply the same safe rule
        // applied per column.
        if (prev is not null && prev.Input == t.InputTokens && prev.Output == t.OutputTokens
            && prev.CacheRead == t.CacheReadTokens && prev.CacheCreation == t.CacheCreationTokens) return;

        // Attribute to the model the session was RECORDED running, null until its records name one - the same
        // records-only nullability as stat_delta.model_id. Only a named model earns an identity row.
        var modelKey = string.IsNullOrWhiteSpace(s.CurrentModel) ? null : s.CurrentModel;
        if (modelKey is not null)
            NeedIdentity(modelKey, IdentityKind.Model, batch);

        batch.Tokens.Add(new StatsWriteBatch.TokenObservation(
            s.SessionId, modelKey,
            t.InputTokens, t.OutputTokens, t.CacheReadTokens, t.CacheCreationTokens,
            prev?.Input ?? 0, prev?.Output ?? 0, prev?.CacheRead ?? 0, prev?.CacheCreation ?? 0,
            prev?.Generation ?? 0));
    }

    private void NeedIdentity(string display, IdentityKind kind, StatsWriteBatch batch)
    {
        // Resolve within THIS batch's tenant (MTR-08): a display spelling already known to another tenant is
        // NOT known here, so the two tenants mint separate surrogate ids and their turns never coalesce.
        if (IdsFor(batch.Tenant, kind).ContainsKey(display)) return;
        // The pending list is compared with the SAME comparer, so two spellings differing only by case
        // inside one batch resolve to one identity, exactly as the dictionary would. The whole batch is one
        // tenant, so no tenant test is needed on the pending list.
        foreach (var (d, k) in batch.NewIdentities)
            if (k == kind && StringComparer.OrdinalIgnoreCase.Equals(d, display)) return;
        batch.NewIdentities.Add((display, kind));
    }

    private bool KnownIdentitySession(string display, string sessionId, IdentityKind kind, StatsWriteBatch batch)
    {
        // Both the display->id lookup and the distinct-session set are consulted under THIS batch's tenant, so
        // neither can answer with another tenant's membership.
        if (IdsFor(batch.Tenant, kind).TryGetValue(display, out var id)
            && SessionsFor(kind).Contains((batch.Tenant, id, sessionId)))
            return true;
        foreach (var (d, sid, k) in batch.NewIdentitySessions)
            if (k == kind && sid == sessionId && StringComparer.OrdinalIgnoreCase.Equals(d, display)) return true;
        return false;
    }

    // Write everything the batch observed, in ONE transaction, then advance the mirror TO WHAT THE STORE NOW
    // HOLDS.
    //
    // Every value the mirror takes below comes back from the writer, which read it out of the row the
    // statement wrote - never from what this class proposed. That is the difference between a mirror and a
    // second opinion. The old code set the high-water mirror to the count it had just SENT, which is only the
    // stored value when nobody else is writing: under two hosted containers, and under the reset rule (where
    // the raise deliberately does not take the proposed value), the mirror and the row would part company
    // silently, and every later fold would measure growth from a number the store had never agreed to. On the
    // next Gateway restart the mirror reloads from the row and the disagreement becomes lost turns.
    //
    // The statements themselves live in GatewayStatsWriter, on Entity Framework, so ONE implementation serves
    // the self-host SQLite file and the hosted PostgreSQL database. Two properties this class depends on are
    // enforced there and must not be re-derived here: an EMPTY batch - an idle poll - writes nothing and does
    // not even open a transaction, and every high-water and membership write is an explicit statement rather
    // than a change-tracked read-then-save.
    private void CommitLocked(StatsWriteBatch batch)
    {
        // The identity map this class holds is what DECIDES identity (a case-insensitive comparer no database
        // is ever asked to reproduce), so the writer resolves the spellings this fold did not recognise and
        // asks US for the ones it already knows.
        var committed = _writer.Commit(batch, (display, kind) => IdsFor(batch.Tenant, kind)[display]);

        // ---- Committed. Only now does the mirror move. Every advance is keyed by the batch's tenant. ----
        if (batch.StampAgentsSince is not null) _agentsSinceUtc[batch.Tenant] = batch.StampAgentsSince;
        // The id that WON, which may have been minted by another writer. Recorded first, because the
        // membership advances below resolve their ids through this map.
        foreach (var (kind, resolved) in committed.Identities)
            foreach (var (d, id) in resolved) { IdsFor(batch.Tenant, kind)[d] = id; DisplayFor(kind)[id] = d; }
        foreach (var h in committed.SessionHighWater)
        {
            var key = (batch.Tenant, h.SessionId);
            if (!_highWater.TryGetValue(key, out var hw))
            {
                hw = new Dictionary<(string, string), Counters>();
                _highWater[key] = hw;
            }
            hw[(h.Modality, h.Surface)] = new Counters { Turns = h.Turns, Characters = h.Chars, Generation = h.Generation };
        }
        foreach (var h in committed.AgentDrivenHighWater)
            _agentDrivenHighWater[(batch.Tenant, h.SessionId)] = new Counters { Turns = h.Turns, Characters = h.Chars, Generation = h.Generation };
        foreach (var h in committed.TokenHighWater)
            _tokenHighWater[(batch.Tenant, h.SessionId)] = new TokenCounters
            {
                Input = h.Input, Output = h.Output, CacheRead = h.CacheRead, CacheCreation = h.CacheCreation,
                Generation = h.Generation,
            };
        foreach (var sid in batch.NewWingmanSessions) _wingmanSessions.Add((batch.Tenant, sid));
        // Marked seeded whichever writer claimed it: after the commit the row exists either way, and this
        // mirror answers only "have I already recorded this?", never "did I record it?".
        foreach (var (sessionId, _) in batch.Seeding) _agentsSeeded.Add((batch.Tenant, sessionId));
        foreach (var (display, sessionId, kind) in batch.NewIdentitySessions)
            SessionsFor(kind).Add((batch.Tenant, IdsFor(batch.Tenant, kind)[display], sessionId));
    }

    /// <summary>
    /// Forget a removed session's high-water entry. Its contribution stays in the totals (it was folded in
    /// as it happened); dropping the high-water entry just stops the map growing without bound.
    /// </summary>
    public void Forget(string? sessionId, TenantId? tenant = null)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        var t = RequireTenant(tenant);
        lock (_lock)
        {
            // Scoped to (tenant, session id) (MTR-08): forgetting one tenant's session must not drop another
            // tenant's high-water for the same bare session id.
            //
            // The token high-water is cleaned up on its own, not under the session_highwater guard: a session
            // has both, and dropping only one would leave the other's map growing without bound. Each is
            // removed only if present, so forgetting a session that never spent a token is still a no-op.
            if (_highWater.Remove((t, sessionId)))
                _writer.DeleteSessionHighWater(t.Value, sessionId);
            if (_tokenHighWater.Remove((t, sessionId)))
                _writer.DeleteTokenHighWater(t.Value, sessionId);
        }
    }

    // The prune - the ninety-day archive fold and the delete behind it - moved into GatewayStatsWriter with
    // the rest of the write path. It still runs inside the caller's own per-tenant transaction and is still
    // constrained to that one tenant.

    /// <summary>
    /// All-time turns that agents drove into other agents' sessions (issue #1636), and their character
    /// volume. NOT part of the human totals: this is the fleet driving itself, which is a different
    /// question from how the owner drives.
    /// </summary>
    public (long Turns, long Characters) AgentDrivenUsage(TenantId? tenant = null)
    {
        var t = RequireTenant(tenant);
        var tenantValue = t.Value;
        lock (_lock)
        {
            using var db = _contexts.CreateDbContext();
            // GroupBy over a constant is the whole-table aggregate: one group, one round trip. An EMPTY table
            // produces no group at all, which is the null this reads as zero - the same answer the COALESCE
            // around each SUM gave.
            var totals = db.AgentDrivenDeltas
                .Where(d => d.Tenant == tenantValue)
                .GroupBy(_ => 1)
                .Select(g => new { Turns = g.Sum(x => x.Turns), Chars = g.Sum(x => x.Chars) })
                .FirstOrDefault();
            return (totals?.Turns ?? 0, totals?.Chars ?? 0);
        }
    }

    /// <summary>An immutable snapshot of the all-time totals for the dashboard, buckets in a stable order.</summary>
    public InputStatsDto CurrentTotals(TenantId? tenant = null)
    {
        var t = RequireTenant(tenant);
        var tenantValue = t.Value;
        lock (_lock)
        {
            using var db = _contexts.CreateDbContext();
            // ARCHIVE rows are INCLUDED - that is the point of archiving: an all-time total must not shrink
            // when the hourly detail behind it is pruned. Scoped to the request tenant (MTR-08).
            var rows = db.StatDeltas
                .Where(d => d.Tenant == tenantValue)
                .GroupBy(d => new { d.Modality, d.Surface })
                .Select(g => new InputStatBucketDto
                {
                    Modality = g.Key.Modality,
                    Surface = g.Key.Surface,
                    Turns = g.Sum(x => x.Turns),
                    Characters = g.Sum(x => x.Chars),
                })
                .ToList();

            var dto = new InputStatsDto();
            // Ordered in C#, ordinal - the comparer the original projection used.
            foreach (var b in rows.OrderBy(b => b.Modality, StringComparer.Ordinal)
                                  .ThenBy(b => b.Surface, StringComparer.Ordinal))
                dto.Buckets.Add(b);
            return dto;
        }
    }

    /// <summary>All-time wingman usage: the number of submitted turns folded while a session had voice mode
    /// on, and the count of distinct sessions ever seen with voice mode on.</summary>
    public WingmanUsageDto WingmanUsage(TenantId? tenant = null)
    {
        var t = RequireTenant(tenant);
        var tenantValue = t.Value;
        lock (_lock)
        {
            using var db = _contexts.CreateDbContext();
            // SUM(turns) WHERE wingman - never anything keyed on modality. A turn TYPED while voice mode
            // was on is a wingman turn. Both halves scoped to the request tenant (MTR-08).
            //
            // The (long?) cast is what makes the empty case read as zero on BOTH providers: SUM over no rows
            // is SQL NULL, and asking for a non-nullable long back would leave that to the provider's own
            // COALESCE habit rather than saying it here.
            var turns = db.StatDeltas
                .Where(d => d.Tenant == tenantValue && d.Wingman)
                .Sum(d => (long?)d.Turns) ?? 0;
            var sessions = db.WingmanSessions.Count(w => w.Tenant == tenantValue);
            return new WingmanUsageDto { Turns = turns, Sessions = sessions };
        }
    }

    /// <summary>The per-hour turn log (the "working day" series: turns by modality and character volume per
    /// UTC clock hour), oldest hour first.</summary>
    public IReadOnlyList<InputHourDto> HourlyTurns(TenantId? tenant = null)
    {
        var t = RequireTenant(tenant);
        var tenantValue = t.Value;
        var marker = GatewayStatsDatabase.ArchiveMarker;
        lock (_lock)
        {
            using var db = _contexts.CreateDbContext();
            // ARCHIVE rows are EXCLUDED: they are real all-time data with no real hour, so letting them
            // through would invent a bucket in this series that was never an hour of the working day. Scoped
            // to the request tenant (MTR-08).
            var rows = db.StatDeltas
                .Where(d => d.Tenant == tenantValue && d.HourUtc != marker)
                .GroupBy(d => d.HourUtc)
                .Select(g => new
                {
                    Hour = g.Key,
                    Voice = g.Sum(x => x.IsVoice ? x.Turns : 0),
                    Typed = g.Sum(x => x.IsVoice ? 0 : x.Turns),
                    Chars = g.Sum(x => x.Chars),
                })
                .ToList();

            var list = new List<InputHourDto>();
            foreach (var r in rows)
                list.Add(new InputHourDto
                {
                    Hour = r.Hour,
                    VoiceTurns = r.Voice,
                    TypedTurns = r.Typed,
                    Turns = r.Voice + r.Typed,
                    Characters = r.Chars,
                });
            // Ordered in C#, ORDINAL. Deliberately not an ORDER BY: an ordinal compare in C# and a
            // collation-dependent sort in the database are different functions, and the hour keys are text.
            list.Sort((a, b) => string.CompareOrdinal(a.Hour, b.Hour));
            return list;
        }
    }

    /// <summary>The per-repository all-time tally (turns by modality, character volume, distinct sessions),
    /// ranked most-driven first, for the private Repos page. Repos with no counted turns are omitted.</summary>
    public IReadOnlyList<RepoStatBucketDto> RepoTotals(TenantId? tenant = null)
    {
        var t = RequireTenant(tenant);
        var tenantValue = t.Value;
        lock (_lock)
        {
            // Scoped to the request tenant, which this method already holds. The accessor CANNOT return
            // another tenant's repository, so the caller has nothing to remember (MTR-08).
            var sessions = RepoSessionCounts(t);

            using var db = _contexts.CreateDbContext();

            // The local checkouts (worktrees, per-machine clones) that rolled up into each repository, kept so
            // the page can still show which working directories a repo's turns came from - the path is not
            // lost when the repo name collapses them. Display spellings come from the identity mirror, never from a
            // SQL join; sorted here so the retained list is stable rather than in row-insert order.
            var checkoutsByRepo = new Dictionary<long, List<string>>();
            var pairs = db.StatDeltas
                .Where(d => d.Tenant == tenantValue && d.CheckoutId != null)
                .Select(d => new { d.RepoId, CheckoutId = d.CheckoutId!.Value })
                .Distinct()
                .ToList();
            foreach (var pair in pairs)
            {
                if (!_checkoutDisplay.TryGetValue(pair.CheckoutId, out var path)) continue;
                if (!checkoutsByRepo.TryGetValue(pair.RepoId, out var paths))
                    checkoutsByRepo[pair.RepoId] = paths = new List<string>();
                paths.Add(path);
            }
            foreach (var paths in checkoutsByRepo.Values)
                paths.Sort(StringComparer.OrdinalIgnoreCase);

            var totals = db.StatDeltas
                .Where(d => d.Tenant == tenantValue)
                .GroupBy(d => d.RepoId)
                .Select(g => new
                {
                    RepoId = g.Key,
                    Voice = g.Sum(x => x.IsVoice ? x.Turns : 0),
                    Typed = g.Sum(x => x.IsVoice ? 0 : x.Turns),
                    Chars = g.Sum(x => x.Chars),
                })
                .ToList();

            var list = new List<RepoStatBucketDto>();
            foreach (var row in totals)
            {
                // The display spelling comes from the identity mirror, never from a SQL join that might be
                // tempted to group or order by the string.
                var display = _repoDisplay.TryGetValue(row.RepoId, out var d) ? d : "";
                list.Add(new RepoStatBucketDto
                {
                    Repo = display,
                    RepoName = RepoLeaf(display),
                    Turns = row.Voice + row.Typed,
                    VoiceTurns = row.Voice,
                    TypedTurns = row.Typed,
                    Characters = row.Chars,
                    Sessions = sessions.TryGetValue(row.RepoId, out var n) ? n : 0,
                    Checkouts = checkoutsByRepo.TryGetValue(row.RepoId, out var cks) ? cks : new List<string>(),
                });
            }
            // Ranked in C#, matching the original ordering exactly. The last tie-break is an ORDINAL string
            // compare and stays in C# on purpose: the equivalent ORDER BY would be a collation-dependent sort,
            // which is a different function and would rank two providers differently on the same rows.
            list.Sort((a, b) =>
            {
                var byTurns = b.Turns.CompareTo(a.Turns);
                if (byTurns != 0) return byTurns;
                var byChars = b.Characters.CompareTo(a.Characters);
                return byChars != 0 ? byChars : string.CompareOrdinal(a.RepoName, b.RepoName);
            });
            return list;
        }
    }

    /// <summary>The per-agent all-time tally (turns by modality, character volume, distinct sessions),
    /// ranked most-driven first, for the private Agents page.</summary>
    public IReadOnlyList<AgentStatBucketDto> AgentTotals(TenantId? tenant = null)
    {
        var t = RequireTenant(tenant);
        var tenantValue = t.Value;
        lock (_lock)
        {
            // Scoped to the request tenant, which this method already holds - see RepoTotals (MTR-08).
            var sessions = AgentSessionCounts(t);

            using var db = _contexts.CreateDbContext();

            var human = db.AgentDeltas
                .Where(d => d.Tenant == tenantValue)
                .GroupBy(d => d.AgentId)
                .Select(g => new
                {
                    AgentId = g.Key,
                    Voice = g.Sum(x => x.IsVoice ? x.Turns : 0),
                    Typed = g.Sum(x => x.IsVoice ? 0 : x.Turns),
                    Chars = g.Sum(x => x.Chars),
                })
                .ToDictionary(x => x.AgentId, x => (x.Voice, x.Typed, x.Chars));

            var driven = db.AgentDrivenDeltas
                .Where(d => d.Tenant == tenantValue)
                .GroupBy(d => d.AgentId)
                .Select(g => new { AgentId = g.Key, Turns = g.Sum(x => x.Turns), Chars = g.Sum(x => x.Chars) })
                .ToDictionary(x => x.AgentId, x => (x.Turns, x.Chars));

            // Every agent EVER attributed appears, including one registered with no turns - the original
            // creates the tally entry on attribution regardless, so the page describes the agents actually
            // being run rather than only those that submitted a turn in the window. Iterate THIS TENANT'S
            // identity map (MTR-08), not the global id->display: iterating the global map would list every
            // other tenant's agents (each as a zero-turn row) - a cross-tenant leak of who runs what. The
            // per-tenant map holds exactly the ids this tenant minted, so both the zero-turn agents and the
            // active ones are its own.
            var list = new List<AgentStatBucketDto>();
            foreach (var (display, id) in IdsFor(t, IdentityKind.Agent))
            {
                human.TryGetValue(id, out var h);
                driven.TryGetValue(id, out var d);
                list.Add(new AgentStatBucketDto
                {
                    Agent = display,
                    AgentName = AgentDisplayName(display),
                    Turns = h.Voice + h.Typed,
                    VoiceTurns = h.Voice,
                    TypedTurns = h.Typed,
                    Characters = h.Chars,
                    AgentDrivenTurns = d.Turns,
                    AgentDrivenCharacters = d.Chars,
                    Sessions = sessions.TryGetValue(id, out var n) ? n : 0,
                });
            }
            // The last tie-break is an ORDINAL string compare and stays in C# - see RepoTotals.
            list.Sort((a, b) =>
            {
                var byTurns = b.Turns.CompareTo(a.Turns);
                if (byTurns != 0) return byTurns;
                var byChars = b.Characters.CompareTo(a.Characters);
                return byChars != 0 ? byChars : string.CompareOrdinal(a.AgentName, b.AgentName);
            });
            return list;
        }
    }

    /// <summary>
    /// Distinct sessions per repository, for ONE tenant. <c>repo_session</c> carries no tenant column at
    /// schema version 5 (it is partitioned indirectly, through a surrogate id minted per tenant), so the
    /// scope comes from an explicit join to <c>repo_identity</c>, which does carry it.
    ///
    /// The join is the point. Reading <c>repo_session</c> alone would return every tenant's counts and be
    /// safe only while every call site remembered to look up ids it had already obtained from a
    /// tenant-filtered source - safety by remembering, which is the shape that produced the missions leak
    /// (devthrottle_internal#1039). This accessor cannot return a foreign row, so there is nothing to
    /// remember. It also stops the Repos page reading the whole table across every tenant on every render,
    /// which on a hosted store is a network round trip that grows with the fleet.
    ///
    /// Explicitly a JOIN, not a navigation property and not a configured relationship: schema version 5 has
    /// no foreign keys between these tables and adding one would change insert and delete ordering.
    /// </summary>
    internal Dictionary<long, int> RepoSessionCounts(TenantId tenant)
    {
        var tenantValue = tenant.Value;
        using var db = _contexts.CreateDbContext();
        return db.RepoSessions
            .Join(db.RepoIdentities.Where(i => i.Tenant == tenantValue),
                s => s.RepoId, i => i.RepoId, (s, i) => s.RepoId)
            .GroupBy(id => id)
            .Select(g => new { RepoId = g.Key, Sessions = g.Count() })
            .ToDictionary(x => x.RepoId, x => x.Sessions);
    }

    /// <summary>Distinct sessions per agent, for ONE tenant. Same shape and the same reasoning as
    /// <see cref="RepoSessionCounts"/>, one table over.</summary>
    internal Dictionary<long, int> AgentSessionCounts(TenantId tenant)
    {
        var tenantValue = tenant.Value;
        using var db = _contexts.CreateDbContext();
        return db.AgentSessions
            .Join(db.AgentIdentities.Where(i => i.Tenant == tenantValue),
                s => s.AgentId, i => i.AgentId, (s, i) => s.AgentId)
            .GroupBy(id => id)
            .Select(g => new { AgentId = g.Key, Sessions = g.Count() })
            .ToDictionary(x => x.AgentId, x => x.Sessions);
    }

    /// <summary>When the per-agent tally started counting for <paramref name="tenant"/> (round-trip UTC), or
    /// "" if that tenant has never folded (MTR-08: the window is per tenant).</summary>
    public string AgentsSinceUtc(TenantId? tenant = null)
    {
        var t = RequireTenant(tenant);
        lock (_lock) { return _agentsSinceUtc.TryGetValue(t, out var v) ? v : ""; }
    }

    /// <summary>
    /// When the model dimension started recording (round-trip UTC), stamped by the schema version 2
    /// migration. A page NEEDS this to read a null model honestly: a turn folded before this moment predates
    /// the dimension and could never have carried a model, while one folded after it belongs to a session
    /// whose agent had recorded no model yet. Both store null and only this stamp tells them apart.
    /// </summary>
    public string ModelsSinceUtc
    {
        get { lock (_lock) { return _modelsSinceUtc; } }
    }

    /// <summary>
    /// The per-model all-time tally (turns by modality, character volume), ranked most-driven first, for the
    /// private Models page. Includes the null-model bucket, which is a real answer and not a gap - see
    /// <see cref="ModelStatBucketDto"/>.
    ///
    /// No distinct-session count, unlike the repository and agent tallies: a session's model can CHANGE
    /// mid-session (the whole reason the producer re-reads it at every turn-end), so "sessions that ran this
    /// model" is not a question this store can answer without a per-session-per-model set nothing keeps. A
    /// count of sessions here would be a number that looks like the neighbouring ones and means something
    /// weaker, so it is absent rather than approximated.
    /// </summary>
    public IReadOnlyList<ModelStatBucketDto> ModelTotals(TenantId? tenant = null)
    {
        var t = RequireTenant(tenant);
        var tenantValue = t.Value;
        lock (_lock)
        {
            using var db = _contexts.CreateDbContext();
            // Archive rows are INCLUDED - the marker convention only excludes them from hourly and
            // working-day series, and an all-time tally that dropped them would shrink as history is pruned.
            // Scoped to the request tenant (MTR-08).
            //
            // Grouping on a NULLABLE key: every unknown-model row lands in ONE group on both providers, which
            // is what makes the null bucket a single honest row rather than one row per unrecorded turn.
            var rows = db.StatDeltas
                .Where(d => d.Tenant == tenantValue)
                .GroupBy(d => d.ModelId)
                .Select(g => new
                {
                    ModelId = g.Key,
                    Voice = g.Sum(x => x.IsVoice ? x.Turns : 0),
                    Typed = g.Sum(x => x.IsVoice ? 0 : x.Turns),
                    Chars = g.Sum(x => x.Chars),
                })
                .ToList();

            var list = new List<ModelStatBucketDto>();
            foreach (var row in rows)
            {
                // A null model_id is the "not recorded" bucket and stays null all the way to the page. The
                // display spelling comes from the identity mirror, never from a SQL join that might be
                // tempted to group or order by the string.
                string? display = row.ModelId is not { } id
                    ? null
                    : (_modelDisplay.TryGetValue(id, out var d) ? d : "");
                list.Add(new ModelStatBucketDto
                {
                    Model = display,
                    Turns = row.Voice + row.Typed,
                    VoiceTurns = row.Voice,
                    TypedTurns = row.Typed,
                    Characters = row.Chars,
                });
            }
            // Ranked in C#, matching the repository and agent tallies. The null bucket sorts by its numbers
            // like any other and is deliberately not pinned anywhere: it is a bucket, not a footnote.
            list.Sort((a, b) =>
            {
                var byTurns = b.Turns.CompareTo(a.Turns);
                if (byTurns != 0) return byTurns;
                var byChars = b.Characters.CompareTo(a.Characters);
                return byChars != 0 ? byChars : string.CompareOrdinal(a.Model ?? "", b.Model ?? "");
            });
            return list;
        }
    }

    /// <summary>
    /// All-time token spend (issue #1637): the running sums of input, output, cache-read and cache-creation
    /// tokens across the whole record. INCLUDES archive rows - the same reason the model tally does: an
    /// all-time figure that dropped them would shrink as history is pruned. No context occupancy here; it is
    /// not spend and was never stored.
    /// </summary>
    public TokenSpendDto TokenSpend(TenantId? tenant = null)
    {
        var t = RequireTenant(tenant);
        var tenantValue = t.Value;
        lock (_lock)
        {
            using var db = _contexts.CreateDbContext();
            // One group over the whole partition - see AgentDrivenUsage for why an empty table reads as zero.
            var totals = db.TokenDeltas
                .Where(d => d.Tenant == tenantValue)
                .GroupBy(_ => 1)
                .Select(g => new
                {
                    Input = g.Sum(x => x.InputTokens),
                    Output = g.Sum(x => x.OutputTokens),
                    CacheRead = g.Sum(x => x.CacheReadTokens),
                    CacheCreation = g.Sum(x => x.CacheCreationTokens),
                })
                .FirstOrDefault();

            return new TokenSpendDto
            {
                InputTokens = totals?.Input ?? 0,
                OutputTokens = totals?.Output ?? 0,
                CacheReadTokens = totals?.CacheRead ?? 0,
                CacheCreationTokens = totals?.CacheCreation ?? 0,
            };
        }
    }

    /// <summary>
    /// Token spend per UTC hour (issue #1637), for the working-day "what did I spend" series. EXCLUDES the
    /// archive marker, exactly as the hourly turn series does: an archive row is not a real hour and would
    /// grow a phantom bucket. Ordered by hour.
    /// </summary>
    public IReadOnlyList<TokenHourDto> TokenSpendByHour(TenantId? tenant = null)
    {
        var t = RequireTenant(tenant);
        var tenantValue = t.Value;
        var marker = GatewayStatsDatabase.ArchiveMarker;
        lock (_lock)
        {
            using var db = _contexts.CreateDbContext();
            var list = db.TokenDeltas
                .Where(d => d.Tenant == tenantValue && d.HourUtc != marker)
                .GroupBy(d => d.HourUtc)
                .Select(g => new TokenHourDto
                {
                    Hour = g.Key,
                    InputTokens = g.Sum(x => x.InputTokens),
                    OutputTokens = g.Sum(x => x.OutputTokens),
                    CacheReadTokens = g.Sum(x => x.CacheReadTokens),
                    CacheCreationTokens = g.Sum(x => x.CacheCreationTokens),
                })
                .ToList();
            // Ordered in C#, ORDINAL - see HourlyTurns.
            list.Sort((a, b) => string.CompareOrdinal(a.Hour, b.Hour));
            return list;
        }
    }

    /// <summary>
    /// Token spend per model (issue #1637), ranked most-spent first, for "which model cost what". INCLUDES
    /// archive rows. The null-model bucket is a REAL bucket - spend folded before the session's model was
    /// recorded - and is reported, not filtered, so the per-model spend sums to the all-time total.
    /// </summary>
    public IReadOnlyList<ModelSpendDto> TokenSpendByModel(TenantId? tenant = null)
    {
        var t = RequireTenant(tenant);
        var tenantValue = t.Value;
        lock (_lock)
        {
            using var db = _contexts.CreateDbContext();
            var rows = db.TokenDeltas
                .Where(d => d.Tenant == tenantValue)
                .GroupBy(d => d.ModelId)
                .Select(g => new
                {
                    ModelId = g.Key,
                    Input = g.Sum(x => x.InputTokens),
                    Output = g.Sum(x => x.OutputTokens),
                    CacheRead = g.Sum(x => x.CacheReadTokens),
                    CacheCreation = g.Sum(x => x.CacheCreationTokens),
                })
                .ToList();

            var list = new List<ModelSpendDto>();
            foreach (var row in rows)
            {
                // A null model_id is the "not recorded" bucket and stays null to the page; the display
                // spelling comes from the identity mirror, never a SQL join over the string.
                string? display = row.ModelId is not { } id
                    ? null
                    : (_modelDisplay.TryGetValue(id, out var d) ? d : "");
                list.Add(new ModelSpendDto
                {
                    Model = display,
                    InputTokens = row.Input,
                    OutputTokens = row.Output,
                    CacheReadTokens = row.CacheRead,
                    CacheCreationTokens = row.CacheCreation,
                });
            }
            // The tie-break is an ORDINAL string compare and stays in C# - see RepoTotals.
            list.Sort((a, b) =>
            {
                var byTotal = b.TotalTokens.CompareTo(a.TotalTokens);
                return byTotal != 0 ? byTotal : string.CompareOrdinal(a.Model ?? "", b.Model ?? "");
            });
            return list;
        }
    }

    // An explicit map, not a PascalCase splitter: "OpenCode" is the product's own spelling and must not
    // become "Open Code". An unrecognised token is shown verbatim - it is a real value we simply do not have
    // a nicer name for, so showing it is honest where hiding it behind "Other" would not be.
    private static string AgentDisplayName(string agent) => agent switch
    {
        "" => "(unknown)",
        "ClaudeCode" => "Claude Code",
        "RawCli" => "Raw CLI",
        _ => agent,
    };

    // The last segment of the grouping key, used as the row's short name. For a repo name
    // ("thefrederiksen/devthrottle") this is the short repository name ("devthrottle"); for a fallback path
    // ("D:\ReposFred\devthrottle") it is the folder name. The same split serves both because '/' and '\' are
    // both segment separators here.
    private static string RepoLeaf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "(unknown)";
        var trimmed = path.TrimEnd('/', '\\');
        var idx = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        return idx >= 0 && idx < trimmed.Length - 1 ? trimmed[(idx + 1)..] : trimmed;
    }

    private static string HourKey(DateTime utc) =>
        utc.ToUniversalTime().ToString(HourFormat, System.Globalization.CultureInfo.InvariantCulture);

    // ---- Plumbing. THERE IS NO RAW PATH LEFT. ----
    //
    // Four helpers used to live here - Execute, ExecuteScalarLong, Read and ReadScalarString - each building
    // a SqliteCommand on the connection GatewayStatsDatabase owns. Three had already lost their last caller
    // when the writer took the write path; the fourth, Read, served LoadMirror and is now Entity Framework
    // like every other read in this class. They are DELETED rather than left in place because a
    // SqliteCommand helper sitting in a provider-neutral type is an invitation: the next read that is a
    // little awkward to express gets written against it, and the hosted Gateway - which has no SQLite
    // connection at all - gets a NullReferenceException at the one moment it is asked for statistics.
    //
    // RawStatementsExecuted therefore stays at zero for the process lifetime and is kept, at zero,
    // deliberately: StatementsExecuted is what the write-path tests count, and it is the sum, so leaving the
    // term visible says "the raw path executed nothing" rather than hiding that there ever was one.

    public void Dispose()
    {
        // Null on the hosted path - there is no file to own, and the context factory belongs to the
        // statistics store, which disposes it.
        if (_ownsDatabase) _db?.Dispose();
    }
}

/// <summary>One hour of the input "working day" log: the turns (total and by modality) and character
/// volume submitted in that UTC clock hour ("yyyy-MM-ddTHH").</summary>
public sealed class InputHourDto
{
    public string Hour { get; set; } = "";
    public long Turns { get; set; }
    public long VoiceTurns { get; set; }
    public long TypedTurns { get; set; }
    public long Characters { get; set; }
}

/// <summary>All-time wingman usage (owner's definition: a session uses the wingman when it has voice mode
/// on): the count of turns submitted while a session had voice mode on, and the number of distinct sessions
/// ever seen with voice mode on.</summary>
public sealed class WingmanUsageDto
{
    public long Turns { get; set; }
    public int Sessions { get; set; }
}

/// <summary>One repository's all-time input tally for the private Repos page.</summary>
public sealed class RepoStatBucketDto
{
    /// <summary>The grouping key: the "owner/repo" repo name the sessions' checkouts belong to
    /// (e.g. "thefrederiksen/devthrottle") when a Director resolved one, so every worktree and every
    /// per-machine clone of one repository is one row. Falls back to the checkout's FOLDER NAME (not its full
    /// path) for a checkout with no repo name, so the same repository worked in from several machines still
    /// folds into one row.</summary>
    public string Repo { get; set; } = "";

    /// <summary>The display leaf of <see cref="Repo"/> (its last segment): the short repository name from an
    /// "owner/repo" key, e.g. "devthrottle". For the folder-name fallback this equals <see cref="Repo"/>.</summary>
    public string RepoName { get; set; } = "";

    public long Turns { get; set; }
    public long VoiceTurns { get; set; }
    public long TypedTurns { get; set; }
    public long Characters { get; set; }

    /// <summary>Distinct sessions that drove counted input into this repo.</summary>
    public int Sessions { get; set; }

    /// <summary>The local checkout paths (worktrees, per-machine clones) whose turns rolled up into this
    /// repository, sorted. Retained so the page can still show which working directories a repo's work came
    /// from; empty only for a legacy row written before the checkout dimension existed.</summary>
    public List<string> Checkouts { get; set; } = new();
}

/// <summary>
/// One model's all-time input tally for the private Models page (issue #1637).
///
/// The "unknown model" row is a REAL row here, not an omission: it is where every turn folded before its
/// session's agent had recorded a model lands, and it is the only bucket that can never shrink to zero -
/// each session's first turn is permanently in it. A page must show it and label it honestly rather than
/// filter it out, or the model turns will not add up to the total turns and the page will look broken.
/// </summary>
public sealed class ModelStatBucketDto
{
    /// <summary>The model the owning Director recorded, e.g. "claude-opus-4-8" - or null for the bucket of
    /// turns folded when no model had been recorded. Never an empty string: absence is null.</summary>
    public string? Model { get; set; }

    public long Turns { get; set; }
    public long VoiceTurns { get; set; }
    public long TypedTurns { get; set; }
    public long Characters { get; set; }
}

/// <summary>All-time token spend (issue #1637): the four cumulative, additive counts. No context occupancy -
/// that is a gauge, not spend, and is never summed.</summary>
public sealed class TokenSpendDto
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }

    /// <summary>Every token the work cost, cached or not - the single "how much did I spend" figure.</summary>
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

/// <summary>Token spend in one UTC hour (issue #1637), for the working-day series.</summary>
public sealed class TokenHourDto
{
    /// <summary>The hour key, "yyyy-MM-ddTHH".</summary>
    public string Hour { get; set; } = "";

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }

    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

/// <summary>One model's all-time token spend (issue #1637), for "which model cost what". The null-model
/// bucket is a real answer - spend folded before the session's model was recorded - not an omission.</summary>
public sealed class ModelSpendDto
{
    /// <summary>The model the owning Director recorded, or null for spend folded when no model had been
    /// recorded. Never an empty string: absence is null.</summary>
    public string? Model { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }

    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

/// <summary>One agent CLI's all-time input tally for the private Agents page.</summary>
public sealed class AgentStatBucketDto
{
    /// <summary>The agent token the Director reported (the AgentKind name), or "" when none.</summary>
    public string Agent { get; set; } = "";

    /// <summary>The display name of <see cref="Agent"/>; "(unknown)" when empty.</summary>
    public string AgentName { get; set; } = "";

    public long Turns { get; set; }
    public long VoiceTurns { get; set; }
    public long TypedTurns { get; set; }
    public long Characters { get; set; }

    /// <summary>Turns OTHER agents drove into the sessions running this agent (issue #1636).</summary>
    public long AgentDrivenTurns { get; set; }

    /// <summary>Character volume other agents drove into the sessions running this agent.</summary>
    public long AgentDrivenCharacters { get; set; }

    /// <summary>Distinct sessions that drove counted input through this agent.</summary>
    public int Sessions { get; set; }
}
