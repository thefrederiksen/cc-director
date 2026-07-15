using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using Microsoft.Data.Sqlite;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// The Gateway's statistics database: one SQLite file holding what used to be a set of hand-rolled JSON
/// documents that were rewritten in full on every counter move. Owns the connection, the schema, and the
/// schema version.
///
/// Why this exists at all (mission "SQLite on the Gateway", Phase 1). The JSON stores cost real money three
/// ways: every new question the owner asked cost a new dictionary, a new store field, and a deploy; a shape
/// change had no version to migrate from, so it either quarantined the file and lost the numbers (pull
/// request #1376 wiped the all-time concurrency peak this way) or, worse, deserialized to defaults and came
/// up silently with zeros; and a counter move rewrote the whole document on a request path. A narrow table
/// of rows answers a new question with a GROUP BY, PRAGMA user_version gives a real migration instead of
/// quarantine-and-lose, and a counter move costs work bounded by what changed rather than by how much
/// history is stored.
///
/// Schema versioning is the point, so it is here from day one. <see cref="SchemaVersion"/> is the version
/// this build understands; the file carries its own in PRAGMA user_version. An older file is migrated
/// forward inside a transaction. A NEWER file - written by a build that knows something this one does not -
/// is a LOUD failure, never a downgrade attempt: silently opening it is how a store loses data, which is the
/// exact failure this mission exists to end.
///
/// No Entity Framework, no Dapper. Raw <see cref="SqliteConnection"/> with CREATE TABLE IF NOT EXISTS,
/// matching the house pattern already in Core (Communications/Services/DatabaseService.cs:47-60).
///
/// Threading: the Gateway is a single process and therefore a single writer, so there is no cross-process
/// write contention. One long-lived connection is held open and every caller reaches it under the owning
/// aggregator's lock, which is also what keeps an unchanged roster poll from paying a connection open.
/// </summary>
public sealed class GatewayStatsDatabase : IDisposable
{
    /// <summary>The schema version this build understands. Bump it and add a migration step; never reshape
    /// an existing table in place without one.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The marker written into hour_utc and session_id when a pruned row is folded into an archive
    /// row. Cannot collide with a real hour key ("yyyy-MM-ddTHH") or a real session id.
    ///
    /// The rule that goes with it, because leaving it implicit is how this design breaks: all-time aggregate
    /// queries INCLUDE archive rows - that is the whole point of archiving, the totals must not shrink when
    /// detail is pruned - while hourly and working-day queries MUST exclude this marker, or the series grows
    /// a phantom bucket that was never a real hour.</summary>
    public const string ArchiveMarker = "ARCHIVE";

    private readonly string _path;
    private readonly SqliteConnection _connection;
    private bool _disposed;

    /// <summary>The open connection. Callers hold the owning aggregator's lock.</summary>
    public SqliteConnection Connection => _connection;

    /// <summary>The database file path, for logging and for the fail-loud messages.</summary>
    public string Path => _path;

    /// <param name="path">The database file. Defaults to gateway-stats.db under the cc-director storage
    /// root, beside the stores it replaces.</param>
    public GatewayStatsDatabase(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path)
            ? System.IO.Path.Combine(CcStorage.Root(), "gateway-stats.db")
            : path!;

        FileLog.Write($"[GatewayStatsDatabase] Open: path={_path}");
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            _connection = new SqliteConnection($"Data Source={_path}");
            _connection.Open();

            // Write-ahead logging: a reader never blocks the single writer. Single process, single writer,
            // so there is nothing to contend with.
            Execute("PRAGMA journal_mode=WAL");
            Execute("PRAGMA foreign_keys=ON");

            Migrate();

            FileLog.Write($"[GatewayStatsDatabase] Open: ready at version {SchemaVersion}, path={_path}");
        }
        catch (Exception ex)
        {
            // No fallback to the JSON store, ever. A database that will not open is a loud failure with a
            // clear message; coming up empty or quietly reverting to JSON is the failure mode this mission
            // exists to end.
            FileLog.Write($"[GatewayStatsDatabase] Open FAILED: path={_path}: {ex.Message}");
            throw new InvalidOperationException(
                $"The Gateway statistics database at '{_path}' could not be opened: {ex.Message}. " +
                "The Gateway will not fall back to the old JSON stores. Fix the database file " +
                "(or move it aside to start a fresh one) and restart the Gateway.", ex);
        }
    }

    private void Migrate()
    {
        var current = QueryUserVersion();
        FileLog.Write($"[GatewayStatsDatabase] Migrate: file version={current}, build version={SchemaVersion}");

        if (current == SchemaVersion)
            return;

        if (current > SchemaVersion)
        {
            // A file written by a newer build. Opening it anyway would be a downgrade against a shape this
            // build does not know - the fastest way to lose the owner's numbers.
            throw new InvalidOperationException(
                $"The Gateway statistics database at '{_path}' is at schema version {current}, but this " +
                $"build only understands version {SchemaVersion}. This database was written by a newer " +
                "build of DevThrottle. Upgrade the Gateway rather than running an older build against it.");
        }

        // Every migration step runs inside one transaction together with its version stamp, so a crash
        // mid-migration can never leave a half-migrated file claiming to be fully migrated.
        using var tx = _connection.BeginTransaction();

        if (current < 1)
            MigrateToVersion1(tx);

        Execute($"PRAGMA user_version={SchemaVersion}", tx);
        tx.Commit();

        FileLog.Write($"[GatewayStatsDatabase] Migrate: {current} -> {SchemaVersion} applied");
    }

    // Version 1: the input-store schema (mission Phase 1).
    //
    // The transaction is threaded explicitly through every statement here. Schema commands on this same
    // connection do enlist in the surrounding transaction without it - the Codex reviewer verified that
    // with a rollback probe rather than taking it on trust - but passing it makes the atomicity visible at
    // each call site instead of relying on a reader knowing that rule.
    private void MigrateToVersion1(SqliteTransaction tx)
    {
        // One row per observed delta, recorded from the cutover FORWARD only.
        //
        // Historical rows are NEVER synthesized here, and there is no history to synthesize them from.
        // The owner ruled that the old numbers are not carried across, so gateway-input-stats.json is
        // renamed aside UNREAD on first run and every number in this database starts at the cutover. There
        // are no baseline tables and there is no import.
        //
        // Worth knowing why the import could not have been faithful even if it had been wanted: the old
        // JSON held three independent projections of history - by hour, by repository, by agent - each
        // already collapsed on a different dimension. Which hour went with which repository with which
        // agent was never written down, so the cross-product could not be recovered from the disk at all.
        // Synthesizing rows would have invented data, and summing the invented rows on one dimension would
        // have disagreed with the real totals on another.
        //
        // repo_id and agent_id are SURROGATE INTEGERS. Not a repository or agent string in any form - not
        // raw, and not folded.
        //
        // The dictionaries these replace group with StringComparer.OrdinalIgnoreCase
        // (GatewayInputStatsAggregator.cs:55 and :61), while SQLite's default text comparison is
        // case-sensitive BINARY, so a plain GROUP BY over a raw string column would SPLIT what the current
        // code MERGES. The obvious repair - store a FOLDED string and group on that - cannot actually be
        // built: it needs a normalizing function, and none exists. StringComparer.OrdinalIgnoreCase is a
        // COMPARER, not a normalizer; there is no Fold(x) exactly equivalent to it. ToLowerInvariant is a
        // different function (it can even change a string's length, at U+0130) and ToUpperInvariant is only
        // close enough that it would "almost certainly never" bite - which is not this mission's standard.
        //
        // With a surrogate id there is no normalizer, because there is no folded string. A
        // Dictionary<string, long> built with StringComparer.OrdinalIgnoreCase resolves a display spelling
        // to an id in memory - that dictionary IS today's comparer, the same object with the same
        // semantics - so parity holds BY CONSTRUCTION rather than by care. SQLite never compares a
        // repository string, so its collation cannot be wrong about one.
        //
        // The principle, which generalises past this column: prefer the design where the mistake is
        // IMPOSSIBLE over the design where the mistake is merely avoided. A rule you have to remember is
        // weaker than a schema that cannot express the error. This deletes the rule rather than obeying it.
        //
        // is_voice, not LOWER(modality)='voice': the voice test at :366 is case-INSENSITIVE while _totals
        // at :32 is case-SENSITIVE. Storing the flag preserves that asymmetry exactly and removes any
        // dependence on a SQL collation.
        //
        // wingman is NOT modality='voice': :425-427 folds a session's ENTIRE turn delta into the wingman
        // count whenever the session has voice mode on, and that delta includes TYPED turns. A turn typed
        // while voice mode is on is a wingman turn. This column records SessionDto.VoiceMode as observed at
        // fold time. Without it, post-cutover wingman turns could not be derived from rows at all.
        Execute(@"
            CREATE TABLE IF NOT EXISTS stat_delta (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                hour_utc     TEXT    NOT NULL,
                session_id   TEXT    NOT NULL,
                modality     TEXT    NOT NULL,
                surface      TEXT    NOT NULL,
                is_voice     INTEGER NOT NULL,
                repo_id      INTEGER NOT NULL,
                agent_id     INTEGER NOT NULL,
                wingman      INTEGER NOT NULL,
                turns        INTEGER NOT NULL,
                chars        INTEGER NOT NULL
            )", tx);
        Execute("CREATE INDEX IF NOT EXISTS ix_stat_delta_hour ON stat_delta(hour_utc)", tx);

        // Operational state for live sessions: the last per-bucket counts seen, so only the INCREASE is
        // folded. This is what makes re-reading a roster safe and what lets counts survive a Director or
        // Gateway restart without double-counting. Its semantics are preserved exactly from
        // GatewayInputStatsAggregator.cs:336-354. Cleared for one session by Forget.
        Execute(@"
            CREATE TABLE IF NOT EXISTS session_highwater (
                session_id TEXT    NOT NULL,
                modality   TEXT    NOT NULL,
                surface    TEXT    NOT NULL,
                turns      INTEGER NOT NULL,
                chars      INTEGER NOT NULL,
                PRIMARY KEY (session_id, modality, surface)
            )", tx);

        // The all-time distinct-session sets. These are DELIBERATELY never pruned - the code says so at
        // :45-47, :51-54, and :57-61 - and they are a requirement to preserve, not a bug to fix.
        //
        // They are NOT COUNT(DISTINCT session_id) over stat_delta: that is exact only while every
        // contributing row is still present, so it stops being exact the moment pruning starts, and it
        // cannot see a pre-cutover session at all.
        Execute("CREATE TABLE IF NOT EXISTS wingman_session (session_id TEXT PRIMARY KEY)", tx);
        Execute(@"
            CREATE TABLE IF NOT EXISTS repo_session (
                repo_id    INTEGER NOT NULL,
                session_id TEXT    NOT NULL,
                PRIMARY KEY (repo_id, session_id)
            )", tx);
        Execute(@"
            CREATE TABLE IF NOT EXISTS agent_session (
                agent_id   INTEGER NOT NULL,
                session_id TEXT    NOT NULL,
                PRIMARY KEY (agent_id, session_id)
            )", tx);

        // Surrogate id to FIRST-SEEN display spelling. First-seen wins is exactly what a .NET Dictionary
        // with an OrdinalIgnoreCase comparer does - it keeps the key it was first given - so the spelling
        // the pages display does not change.
        //
        // These columns are, from SQLite's point of view, write-only: they are read once at startup to
        // rebuild the in-memory identity map, and SQLite is never asked to compare or group by them. That is
        // the whole point - the only component that decides whether two repository strings are equal is the
        // same StringComparer.OrdinalIgnoreCase that decides it today.
        //
        // Deliberately NO UNIQUE constraint on the display column: uniqueness here is case-INSENSITIVE and
        // SQLite could only enforce it case-SENSITIVELY (BINARY), which would be the wrong question asked
        // authoritatively. The in-memory map is what guarantees one id per distinct-ignoring-case spelling.
        Execute(@"
            CREATE TABLE IF NOT EXISTS repo_identity (
                repo_id      INTEGER PRIMARY KEY AUTOINCREMENT,
                repo_display TEXT    NOT NULL
            )", tx);
        Execute(@"
            CREATE TABLE IF NOT EXISTS agent_identity (
                agent_id      INTEGER PRIMARY KEY AUTOINCREMENT,
                agent_display TEXT    NOT NULL
            )", tx);

        // ---- The agent-to-agent lane (issue #1636). A SEPARATE TABLE, deliberately. ----
        //
        // These are turns OTHER AGENTS drove into a session, and the code is explicit that they "never enter
        // _totals, _hourly or the buckets, because the human voice-versus-typed numbers must stay about the
        // human" (GatewayInputStatsAggregator.cs:102-105). Putting them in stat_delta behind a lane flag
        // would make that a RULE every human aggregate query has to remember - the archive-marker problem
        // again, and this one fails SILENTLY: the owner's voice-versus-typed share would quietly start
        // including agent traffic and nothing would look wrong. In their own table they CANNOT be summed
        // into the human totals by accident.
        //
        // The shapes also disagree, which is the second reason: the agent-driven high-water is keyed by
        // SESSION ALONE, while the human high-water is keyed by session AND modality AND surface. One table
        // would force one of them to lie about its own key.
        //
        // No hour, no repository, no modality: this lane feeds only the per-agent tally and a global pair,
        // so carrying columns nothing populates would be a dimension nothing emits.
        Execute(@"
            CREATE TABLE IF NOT EXISTS agent_driven_delta (
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                agent_id INTEGER NOT NULL,
                turns    INTEGER NOT NULL,
                chars    INTEGER NOT NULL
            )", tx);

        // High-watered exactly like the human buckets - only the increase counts, and a reported count that
        // DROPPED (a Director restarted this session id) is fresh activity from zero.
        Execute(@"
            CREATE TABLE IF NOT EXISTS agent_driven_highwater (
                session_id TEXT    PRIMARY KEY,
                turns      INTEGER NOT NULL,
                chars      INTEGER NOT NULL
            )", tx);

        // Sessions whose already-counted turns have been attributed to their agent (issue #1633).
        //
        // THIS IS LIVE BEHAVIOUR, NOT MIGRATION SCAFFOLDING, and the distinction nearly cost the owner's
        // agent numbers. On a fresh database the first-fold back-fill contributes nothing, because a new
        // session's high-water is empty - so this table looks like dead weight. But session_highwater
        // PERSISTS across a Gateway restart, and without this set the first fold after a restart would
        // back-fill every live session a SECOND time and double every agent's turns. The aggregator says so
        // itself at GatewayInputStatsAggregator.cs:80-81. It survives because of what it DOES, not because
        // of what its name suggests it was for.
        Execute("CREATE TABLE IF NOT EXISTS agents_seeded (session_id TEXT PRIMARY KEY)", tx);


        // Runtime scalars, keyed by name. Its only occupant today is agents_since_utc - when the per-agent
        // breakdown started counting, stamped on the first observation by StampAgentsSinceLocked and never
        // moved after that.
        //
        // It lives here rather than in a table of its own because it is not a statistic: it is a fact about
        // when a statistic began. It is also NOT history - it is stamped at runtime by the running product -
        // which is why it survived the deletion of everything that carried the old numbers across.
        Execute(@"
            CREATE TABLE IF NOT EXISTS meta (
                name  TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )", tx);
    }

    private int QueryUserVersion()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        var result = cmd.ExecuteScalar();
        return result is null ? 0 : Convert.ToInt32(result);
    }

    private void Execute(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = _connection.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Close();
        _connection.Dispose();
        FileLog.Write($"[GatewayStatsDatabase] Dispose: closed {_path}");
    }
}
