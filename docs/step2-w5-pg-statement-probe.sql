-- SYNTAX AND SEMANTICS PROBE for the concurrency store's PostgreSQL statements.
--
-- WHAT THIS PROVES: that the exact statement text the store generates for Npgsql is valid PostgreSQL,
-- that GREATEST keeps the higher maximum through an interleave, that each timestamp moves ONLY on the
-- write whose own maximum advanced, that the CASE-with-NULL first insert types correctly when the
-- parameters arrive typed (PREPARE names the same types Npgsql infers), and that the retention prune's
-- text range orders the hour keys chronologically under this server's collation.
--
-- WHAT THIS DOES NOT PROVE, and must not be reported as: it is NOT a run of the store. It does not
-- exercise the C# path, Npgsql's actual parameter inference, the model-to-table mapping, or any store
-- logic. It is run as the superuser in a throwaway schema, so it says nothing about the restricted
-- role's privileges either. The gated Postgres test class is what proves those, and it is still owed.

\set ON_ERROR_STOP on

DROP SCHEMA IF EXISTS probe_w5 CASCADE;
CREATE SCHEMA probe_w5;

CREATE TABLE probe_w5.concurrency_peak (
    tenant            text PRIMARY KEY,
    live_max          integer NOT NULL,
    live_max_at_utc   timestamptz NULL,
    working_max       integer NOT NULL,
    working_max_at_utc timestamptz NULL
);

CREATE TABLE probe_w5.concurrency_hour (
    tenant            text NOT NULL,
    hour_utc          text NOT NULL,
    max_live          integer NOT NULL,
    max_working       integer NOT NULL,
    distinct_sessions integer NOT NULL,
    distinct_machines integer NOT NULL,
    distinct_repos    integer NOT NULL,
    PRIMARY KEY (tenant, hour_utc)
);

CREATE TABLE probe_w5.concurrency_hour_member (
    tenant    text NOT NULL,
    hour_utc  text NOT NULL,
    kind      text NOT NULL,
    member_id text NOT NULL,
    PRIMARY KEY (tenant, hour_utc, kind, member_id)
);

-- The peak upsert, verbatim in shape, with the parameters typed as Npgsql types them.
PREPARE upsert_peak(text, integer, integer, timestamptz) AS
INSERT INTO probe_w5.concurrency_peak (tenant, live_max, live_max_at_utc, working_max, working_max_at_utc)
VALUES ($1, $2, CASE WHEN $2 > 0 THEN $4 ELSE NULL END, $3, CASE WHEN $3 > 0 THEN $4 ELSE NULL END)
ON CONFLICT (tenant) DO UPDATE SET
    live_max_at_utc = CASE WHEN excluded.live_max > concurrency_peak.live_max THEN excluded.live_max_at_utc ELSE concurrency_peak.live_max_at_utc END,
    live_max = GREATEST(excluded.live_max, concurrency_peak.live_max),
    working_max_at_utc = CASE WHEN excluded.working_max > concurrency_peak.working_max THEN excluded.working_max_at_utc ELSE concurrency_peak.working_max_at_utc END,
    working_max = GREATEST(excluded.working_max, concurrency_peak.working_max);

PREPARE upsert_hour(text, text, integer, integer, integer, integer, integer) AS
INSERT INTO probe_w5.concurrency_hour (tenant, hour_utc, max_live, max_working, distinct_sessions, distinct_machines, distinct_repos)
VALUES ($1, $2, $3, $4, $5, $6, $7)
ON CONFLICT (tenant, hour_utc) DO UPDATE SET
    max_live = GREATEST(excluded.max_live, concurrency_hour.max_live),
    max_working = GREATEST(excluded.max_working, concurrency_hour.max_working),
    distinct_sessions = GREATEST(excluded.distinct_sessions, concurrency_hour.distinct_sessions),
    distinct_machines = GREATEST(excluded.distinct_machines, concurrency_hour.distinct_machines),
    distinct_repos = GREATEST(excluded.distinct_repos, concurrency_hour.distinct_repos);

-- A first observation where live peaked and working did not: the working timestamp must be NULL, not
-- invented. This is the CASE-with-NULL branch the C# side relies on.
EXECUTE upsert_peak('local', 5, 0, timestamptz '2026-07-11T20:00:00Z');
\echo '--- after first write: live_max 5 stamped, working_max 0 with a NULL instant ---'
SELECT live_max, live_max_at_utc, working_max, working_max_at_utc FROM probe_w5.concurrency_peak;

-- The race: container A writes 8 at 20:02, container B writes 7 at 20:03 and lands LAST.
EXECUTE upsert_peak('local', 8, 0, timestamptz '2026-07-11T20:02:00Z');
EXECUTE upsert_peak('local', 7, 0, timestamptz '2026-07-11T20:03:00Z');
\echo '--- after the race: live_max must be 8, stamped 20:02 (the write that SET it), not 20:03 ---'
SELECT live_max, live_max_at_utc, working_max, working_max_at_utc FROM probe_w5.concurrency_peak;

-- A later write that advances ONLY working must not drag the live timestamp forward.
EXECUTE upsert_peak('local', 2, 9, timestamptz '2026-07-11T21:00:00Z');
\echo '--- live stays 8 at 20:02; working becomes 9 at 21:00 ---'
SELECT live_max, live_max_at_utc, working_max, working_max_at_utc FROM probe_w5.concurrency_peak;

EXECUTE upsert_hour('local', '2026-07-11T20', 5, 2, 5, 1, 1);
EXECUTE upsert_hour('local', '2026-07-11T20', 8, 1, 3, 2, 1);
EXECUTE upsert_hour('local', '2026-07-11T20', 7, 0, 9, 1, 1);
\echo '--- every per-hour column is a maximum: 8, 2, 9, 2, 1 ---'
SELECT max_live, max_working, distinct_sessions, distinct_machines, distinct_repos FROM probe_w5.concurrency_hour;

-- Membership is insert-if-absent, and the ordinal key admits two spellings of one machine.
INSERT INTO probe_w5.concurrency_hour_member (tenant, hour_utc, kind, member_id)
VALUES ('local','2026-07-11T20','machine','SOREN_NORTH'), ('local','2026-07-11T20','machine','Soren_North')
ON CONFLICT (tenant, hour_utc, kind, member_id) DO NOTHING;
INSERT INTO probe_w5.concurrency_hour_member (tenant, hour_utc, kind, member_id)
VALUES ('local','2026-07-11T20','machine','SOREN_NORTH')
ON CONFLICT (tenant, hour_utc, kind, member_id) DO NOTHING;
\echo '--- two ordinally-distinct machine rows, and the repeat inserted nothing ---'
SELECT count(*) AS machine_rows FROM probe_w5.concurrency_hour_member WHERE kind = 'machine';

-- The retention range: does a text comparison on the fixed-width hour key order chronologically here?
INSERT INTO probe_w5.concurrency_hour (tenant, hour_utc, max_live, max_working, distinct_sessions, distinct_machines, distinct_repos)
VALUES ('local','2026-03-13T20',1,1,1,1,1), ('local','2026-04-01T09',1,1,1,1,1), ('local','2026-12-31T23',1,1,1,1,1);
\echo '--- collation check: these must come back in chronological order ---'
SELECT hour_utc FROM probe_w5.concurrency_hour WHERE tenant = 'local' ORDER BY hour_utc;
\echo '--- the prune range must take the two old hours and leave July and December ---'
DELETE FROM probe_w5.concurrency_hour WHERE tenant = 'local' AND hour_utc < '2026-07-11T20';
SELECT hour_utc FROM probe_w5.concurrency_hour WHERE tenant = 'local' ORDER BY hour_utc;

-- And the current-hour discard, which is what a returning hour relies on.
INSERT INTO probe_w5.concurrency_hour_member (tenant, hour_utc, kind, member_id)
VALUES ('local','2026-07-11T21','session','session-c') ON CONFLICT DO NOTHING;
DELETE FROM probe_w5.concurrency_hour_member WHERE tenant = 'local' AND hour_utc <> '2026-07-11T21';
\echo '--- only the new current hour survives the discard ---'
SELECT hour_utc, kind, member_id FROM probe_w5.concurrency_hour_member ORDER BY hour_utc, kind, member_id;

DROP SCHEMA probe_w5 CASCADE;
\echo '--- probe complete ---'
