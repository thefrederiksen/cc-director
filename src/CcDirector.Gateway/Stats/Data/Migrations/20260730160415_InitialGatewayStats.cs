using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Stats.Data.Migrations
{
    /// <summary>
    /// The SQLite baseline for the statistics store: the LITERAL schema version 5 data definition language,
    /// as a real gateway-stats.db carries it on disk today.
    ///
    /// WHY THIS IS RAW SQL AND NOT A GENERATED CreateTable CHAIN. This baseline is not merely "how to build
    /// the schema" - it is the thing the self-host adoption step points at when it stamps this migration as
    /// applied against a file it did not create. That stamp TELLS Entity Framework the file on disk is what
    /// this migration would have produced. If the two shapes differ at all, we have not adopted the file, we
    /// have told the framework a lie about it, and every later migration in this chain is then applied to a
    /// database that is not the one the chain believes it is operating on - surfacing much later, on a real
    /// user's machine, as a missing index or an absent constraint, with a stack trace pointing nowhere near
    /// adoption.
    ///
    /// A generated migration got four of those differences wrong at once: it dropped the tenant column's
    /// DEFAULT 'local', it emitted INTEGER NOT NULL ... PRIMARY KEY AUTOINCREMENT where version 5 emits a
    /// bare INTEGER PRIMARY KEY AUTOINCREMENT (so PRAGMA table_info reports notnull=1 against the real
    /// file's 0), it left PRAGMA user_version at 0, and it named all sixteen primary key constraints where
    /// the hand-written version 5 names none - and SQLite stores those names in sqlite_master. Chasing those
    /// one at a time by nudging the migration builder is a game that cannot be won: it matches the shapes
    /// somebody happened to check, and the next divergence is the one nobody listed. Writing the version 5
    /// text itself makes equivalence true BY CONSTRUCTION, and turns
    /// <c>GatewayStatsSqliteBaselineEquivalenceTests</c> from a chore that has to be re-verified by hand into
    /// a check that cannot drift.
    ///
    /// SO THIS TEXT IS VERBATIM AND MUST NOT BE TIDIED. It is copied from the <c>sql</c> column of
    /// <c>sqlite_master</c> in a database built by RUNNING the shipped
    /// <see cref="CcDirector.Gateway.Stats.GatewayStatsDatabase"/>, which is why it carries scars that look
    /// like mistakes and are not:
    ///
    ///  - The eight delta and identity tables end with their added columns hanging off the closing line
    ///    (<c>, model_id INTEGER, checkout_id INTEGER, tenant TEXT NOT NULL DEFAULT 'local')</c>). That is
    ///    what SQLite's ALTER TABLE ADD COLUMN does to the stored statement, and version 5 reached those
    ///    columns by ALTER.
    ///  - Six tables have QUOTED names. Those are the ones version 5 rebuilt to put the tenant into their
    ///    PRIMARY KEY; ALTER TABLE ... RENAME TO rewrites the stored name in quotes.
    ///  - The rowid keys are a bare <c>INTEGER PRIMARY KEY AUTOINCREMENT</c> with no NOT NULL and no
    ///    constraint name, and the composite keys carry no constraint name either.
    ///
    /// Reformatting any of that, or "fixing" the odd comma placement, breaks equivalence with the very files
    /// this baseline exists to describe.
    ///
    /// THE VERSION STAMP IS PART OF THE SCHEMA. This baseline stamps PRAGMA user_version = 5, exactly as the
    /// hand-rolled path does. It is not decoration: the shipped versioned code refuses, loudly and correctly,
    /// to open a file whose version exceeds the build it is running, so a user who rolls a desktop build back
    /// gets a clean refusal that names the problem. A file left at 0 would instead make that older build
    /// decide the store predates every migration and run its version 1 through 5 steps against tables that
    /// already exist, dying on a duplicate ALTER TABLE.
    ///
    /// ANY FUTURE MIGRATION IN THIS CHAIN MUST BUMP THAT STAMP. That rule is not left to memory:
    /// <c>GatewayStatsSqliteVersionStampTests</c> fails when a migration is added and the stamp does not move.
    /// </summary>
    public partial class InitialGatewayStats : Migration
    {
        /// <summary>Every table in a version 5 store, in the order version 5's own migration steps introduced
        /// them, each exactly as that store carries it.</summary>
        private static readonly string[] Version5Tables =
        {
            // ---- schema version 1: the input-store schema -------------------------------------------
            //
            // stat_delta gained model_id at version 2, checkout_id at version 4 and tenant at version 5, all
            // by ALTER TABLE ADD COLUMN - hence the three columns hanging off the closing line.
            @"CREATE TABLE stat_delta (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                hour_utc     TEXT    NOT NULL,
                session_id   TEXT    NOT NULL,
                modality     TEXT    NOT NULL,
                surface      TEXT    NOT NULL,
                is_voice     INTEGER NOT NULL,
                repo_id      INTEGER NOT NULL,
                wingman      INTEGER NOT NULL,
                turns        INTEGER NOT NULL,
                chars        INTEGER NOT NULL
            , model_id INTEGER, checkout_id INTEGER, tenant TEXT NOT NULL DEFAULT 'local')",

            // Rebuilt at version 5 to put the tenant into the primary key, hence the quoted name.
            @"CREATE TABLE ""session_highwater"" (
                  tenant     TEXT    NOT NULL,
                  session_id TEXT    NOT NULL,
                  modality   TEXT    NOT NULL,
                  surface    TEXT    NOT NULL,
                  turns      INTEGER NOT NULL,
                  chars      INTEGER NOT NULL,
                  PRIMARY KEY (tenant, session_id, modality, surface)
              )",

            @"CREATE TABLE ""wingman_session"" (
                  tenant     TEXT NOT NULL,
                  session_id TEXT NOT NULL,
                  PRIMARY KEY (tenant, session_id)
              )",

            // repo_session and agent_session are the two tables version 5 left alone entirely: no tenant
            // column, no rebuild, so they are still exactly as version 1 wrote them. They are partitioned
            // indirectly, through surrogate ids minted per tenant.
            @"CREATE TABLE repo_session (
                repo_id    INTEGER NOT NULL,
                session_id TEXT    NOT NULL,
                PRIMARY KEY (repo_id, session_id)
            )",

            @"CREATE TABLE agent_session (
                agent_id   INTEGER NOT NULL,
                session_id TEXT    NOT NULL,
                PRIMARY KEY (agent_id, session_id)
            )",

            @"CREATE TABLE repo_identity (
                repo_id      INTEGER PRIMARY KEY AUTOINCREMENT,
                repo_display TEXT    NOT NULL
            , tenant TEXT NOT NULL DEFAULT 'local')",

            @"CREATE TABLE agent_identity (
                agent_id      INTEGER PRIMARY KEY AUTOINCREMENT,
                agent_display TEXT    NOT NULL
            , tenant TEXT NOT NULL DEFAULT 'local')",

            @"CREATE TABLE agent_delta (
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                agent_id INTEGER NOT NULL,
                is_voice INTEGER NOT NULL,
                turns    INTEGER NOT NULL,
                chars    INTEGER NOT NULL
            , tenant TEXT NOT NULL DEFAULT 'local')",

            @"CREATE TABLE agent_driven_delta (
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                agent_id INTEGER NOT NULL,
                turns    INTEGER NOT NULL,
                chars    INTEGER NOT NULL
            , tenant TEXT NOT NULL DEFAULT 'local')",

            @"CREATE TABLE ""agent_driven_highwater"" (
                  tenant     TEXT    NOT NULL,
                  session_id TEXT    NOT NULL,
                  turns      INTEGER NOT NULL,
                  chars      INTEGER NOT NULL,
                  PRIMARY KEY (tenant, session_id)
              )",

            @"CREATE TABLE ""agents_seeded"" (
                  tenant     TEXT NOT NULL,
                  session_id TEXT NOT NULL,
                  PRIMARY KEY (tenant, session_id)
              )",

            @"CREATE TABLE ""meta"" (
                  tenant TEXT NOT NULL,
                  name   TEXT NOT NULL,
                  value  TEXT NOT NULL,
                  PRIMARY KEY (tenant, name)
              )",

            // ---- schema version 2: the model dimension ----------------------------------------------
            @"CREATE TABLE model_identity (
                model_id      INTEGER PRIMARY KEY AUTOINCREMENT,
                model_display TEXT    NOT NULL
            , tenant TEXT NOT NULL DEFAULT 'local')",

            // ---- schema version 3: the token dimension ----------------------------------------------
            @"CREATE TABLE token_delta (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                hour_utc              TEXT    NOT NULL,
                model_id              INTEGER,
                input_tokens          INTEGER NOT NULL,
                output_tokens         INTEGER NOT NULL,
                cache_read_tokens     INTEGER NOT NULL,
                cache_creation_tokens INTEGER NOT NULL
            , tenant TEXT NOT NULL DEFAULT 'local')",

            @"CREATE TABLE ""token_highwater"" (
                  tenant                TEXT    NOT NULL,
                  session_id            TEXT    NOT NULL,
                  input_tokens          INTEGER NOT NULL,
                  output_tokens         INTEGER NOT NULL,
                  cache_read_tokens     INTEGER NOT NULL,
                  cache_creation_tokens INTEGER NOT NULL,
                  PRIMARY KEY (tenant, session_id)
              )",

            // ---- schema version 4: the checkout dimension -------------------------------------------
            @"CREATE TABLE checkout_identity (
                checkout_id      INTEGER PRIMARY KEY AUTOINCREMENT,
                checkout_display TEXT    NOT NULL
            , tenant TEXT NOT NULL DEFAULT 'local')",
        };

        /// <summary>The four indexes a version 5 store carries, with the names it gave them.</summary>
        private static readonly string[] Version5Indexes =
        {
            "CREATE INDEX ix_stat_delta_hour ON stat_delta(hour_utc)",
            "CREATE INDEX ix_token_delta_hour ON token_delta(hour_utc)",
            "CREATE INDEX ix_stat_delta_tenant_hour ON stat_delta(tenant, hour_utc)",
            "CREATE INDEX ix_token_delta_tenant_hour ON token_delta(tenant, hour_utc)",
        };

        /// <summary>The table names, for <see cref="Down"/> - written out beside the statements that create
        /// them.</summary>
        private static readonly string[] TableNames =
        {
            "stat_delta", "token_delta", "agent_delta", "agent_driven_delta",
            "repo_identity", "agent_identity", "model_identity", "checkout_identity",
            "session_highwater", "token_highwater", "agent_driven_highwater",
            "wingman_session", "agents_seeded", "repo_session", "agent_session", "meta",
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Version5Tables)
                migrationBuilder.Sql(table);

            foreach (var index in Version5Indexes)
                migrationBuilder.Sql(index);

            // The version stamp the hand-rolled path writes, so a store built by this chain is
            // indistinguishable from one built by that path - including to an OLDER build, which reads this
            // stamp to decide whether it understands the file at all.
            migrationBuilder.Sql("PRAGMA user_version = 5");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The indexes go with their tables, so only the tables are dropped. Back to an unstamped file,
            // which is what a store that has never held this schema looks like.
            foreach (var table in TableNames)
                migrationBuilder.Sql($"DROP TABLE IF EXISTS \"{table}\"");

            migrationBuilder.Sql("PRAGMA user_version = 0");
        }
    }
}
