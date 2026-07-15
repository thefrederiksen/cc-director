# Mission Brief: SQLite on the Gateway

Status: PROPOSED - awaiting review by the Codex reviewer and approval by the owner. No code is
written until both approve this document.

Written 2026-07-15 by the Architect session ("Gateway SQLite - Architect", session 0a4297a9,
machine SOREN_NORTH). This document is the Architect's handover to the Manager. The Manager owns
execution from here; the Architect settles the design and then lets the Manager drive.

Every claim below was verified against `origin/main` at commit 8c64a049 on 2026-07-15, in the
mission worktree `D:\ReposFred\devthrottle-gateway-sqlite` (branch `feat/gateway-sqlite`), and
carries file and line citations. Disk measurements were taken on SOREN_NORTH the same day.

## The why

The Gateway keeps its statistics in seven hand-rolled JSON files. That was the right call when
there was one store. There are now seven, each with its own copy of the same load, serialize,
atomic temp-write-and-rename, and quarantine-on-corrupt code, and the pattern has started to cost
real money in three specific ways:

1. **Every new question costs a schema change and a deploy.** `GatewayInputStatsAggregator` keeps a
   separate dictionary per question: `_totals`, `_highWater`, `_hourly`, `_wingmanSessions`,
   `_repos`, `_agents` (`src/CcDirector.Gateway/Stats/GatewayInputStatsAggregator.cs:32-66`). Each
   new dimension the owner asks for is a new dictionary, a new field on the store file, a new
   data-transfer object field, and a redeploy. Per-repository and per-agent tallies were both added
   this way after the Stats mission was declared finished, which is the evidence that the questions
   keep coming.

2. **A shape change has already silently destroyed live data.** There is no schema versioning
   anywhere in these stores. A store whose shape changed is treated as corrupt: it is renamed to
   `.corrupt-{timestamp}` and the store restarts empty
   (`GatewaySessionConcurrencyStats.cs:249-298`). That path is exactly how pull request #1376 wiped
   the all-time fleet concurrency peak of 35 on deploy. It was noticed only because someone happened
   to be looking.

3. **The write is a whole-document rewrite on a request path.** Both stats stores re-serialize the
   entire document and rewrite the file whenever any counter moves
   (`GatewayInputStatsAggregator.cs:123` and `:137`, `Save()` at `:555-583`), and the fold runs on
   **every** `GET /sessions` read (`src/CcDirector.Gateway/Api/GatewayEndpoints.cs:813` and `:820`).
   That is O(all history) of synchronous work under a lock, per roster poll. It compounds with the
   deliberately unbounded distinct-session sets described below: the file grows forever *and* is
   rewritten constantly.

SQLite makes all three go away: one narrow table of rows answers new questions with a `GROUP BY`
instead of a deploy, `PRAGMA user_version` gives real migrations instead of quarantine-and-lose,
and a counter move becomes an `UPDATE` of one row instead of a rewrite of the file.

The cost is near zero. `Microsoft.Data.Sqlite` 9.0.2 is already a package reference in
`src/CcDirector.Core/CcDirector.Core.csproj:18`, the Gateway already references Core
(`src/CcDirector.Gateway/CcDirector.Gateway.csproj:49`), and the house pattern for using it
already exists in `src/CcDirector.Core/Communications/Services/DatabaseService.cs:47-60` (raw
`SqliteConnection`, `CREATE TABLE IF NOT EXISTS`). There is no Entity Framework, no Dapper, and no
LiteDB in this repository, and this mission does not introduce one.

**This mission is a refactor. It ships no new user-visible feature.** The honest justification is
the three costs above, not disk space. Anyone who reads this document and concludes "we are storing
a lot of statistics, so we need a database" has the wrong reason - see the next section.

## What is actually stored today, and what is NOT in scope

Measured on SOREN_NORTH, 2026-07-15. All paths resolve through `CcStorage.Root()`
(`src/CcDirector.Core/Storage/CcStorage.cs:28-38`) to `%LOCALAPPDATA%\cc-director`.

| Store | Class | Size | Shape |
|---|---|---|---|
| `gateway-input-stats.json` | `Stats/GatewayInputStatsAggregator.cs:107` | 57 KB | Counters, hourly buckets, all-time sets |
| `carmode-telemetry.json` | `CarMode/CarModeTelemetryStore.cs:46` | 32 KB | Per-turn events in a rewritten list |
| `netdiag-rollup.json` | network diagnostics | 21 KB | Rollup |
| `netdiag-devices.json` | network diagnostics | 20 KB | Rollup |
| `gateway-concurrency-stats.json` | `Stats/GatewaySessionConcurrencyStats.cs:69` | 16 KB | Hourly buckets, all-time peaks |
| `cronruns.json` | scheduler | 15 KB | Run history |
| `voice-sessions.json` | voice | 13 KB | Session records |

That is about 174 KB in total. **Size is not the problem and must not be used to argue this
mission.** These are rolled-up aggregates, and aggregates stay small.

**Explicitly out of scope - the append-only logs stay exactly as they are.** Two stores are
JSON Lines files rather than rewritten documents:

- `prompt-log/conversation-yyyyMMdd.jsonl`, 3.3 MB across 5 days
  (`src/CcDirector.Gateway/Prompts/GatewayPromptLog.cs:51-55`)
- `transcription-log/transcription-YYYYMMDD.jsonl`, 1.7 MB across 7 days
  (`src/CcDirector.Gateway/Transcription/TranscriptionTelemetryLog.cs:50-54`)

An earlier read of this ground called their lack of pruning a retention gap that should be fixed
before this mission. **That was wrong, and the correction is recorded here so nobody re-opens it.**
The prompt log's unbounded retention is a deliberate, documented decision:
`GatewayPromptLog.cs:26-27` reads "Retention is unbounded. The point is looking back across weeks
and months, and the text is small. Nothing prunes this." JSON Lines is the correct format for an
append-only event stream: it does not rewrite the file, and it is trivially inspectable. Moving
these to SQLite would solve a problem they do not have.

The transcription log carries no stated retention policy either way. It grows about 1.7 MB a week
on one machine and holds transcript text. That is a real open question but it is **not** this
mission, and it is not a blocker. It is recorded in the follow-ups section.

Likewise, the all-time distinct-session sets in the input aggregator (`_repos[].Sessions`,
`_agents[].Sessions`, `_wingmanSessions`) are **deliberately** never pruned - the code says so at
`GatewayInputStatsAggregator.cs:45-47`, `:51-54`, and `:57-61`. They back all-time distinct counts.
They are not a bug to fix; they are a requirement to preserve. What is wrong is only *where* they
live: inside a document that is fully rewritten on every roster read.

**The scope line for this mission, stated once:** a store moves to SQLite if and only if it
rewrites its whole document to record an incremental change. Append-only logs stay on disk as
JSON Lines.

## The design

Five decisions the Architect is settling. The Manager owns everything below this line's detail.

**Decision 1 - One database file, raw Microsoft.Data.Sqlite.** `gateway-stats.db` under
`CcStorage.Root()`, beside the stores it replaces. Raw `SqliteConnection` with
`CREATE TABLE IF NOT EXISTS`, matching `DatabaseService.cs:47-60`. No Entity Framework, no Dapper.
Write-ahead logging mode. The Gateway is a single process and therefore a single writer, so no
cross-process write contention exists. The Gateway must gain its own explicit `PackageReference`
rather than leaning on the transitive one through Core.

**Decision 2 - Rows, not counters. This is the whole point of the mission.** Do NOT port the
existing dictionaries into six tables; that would buy nothing but a new dependency. Store one
narrow delta row per observed increase, carrying its dimensions as columns:

```
stat_delta(hour_utc, session_id, modality, surface, repo, agent, turns, chars)
```

Every number the dashboard shows today is then a query, not a field:

- all-time totals by modality and surface - `SUM(...) GROUP BY modality, surface`
- the working-day series - `GROUP BY hour_utc`
- the per-repository page - `GROUP BY repo`
- the per-agent page - `GROUP BY agent`
- distinct session counts - `COUNT(DISTINCT session_id)`, exact, with no HashSet to persist

Six dictionaries collapse to one table, and the next question the owner asks is a query rather than
a deploy. That is the deliverable.

**Decision 3 - The high-water logic survives unchanged, because SQL does not replace it.** The
idempotent fold (`FoldLocked`, `GatewayInputStatsAggregator.cs:320`) is what makes re-reading a
roster safe and what lets counts survive a Director or Gateway restart without double-counting. It
is the hard part of this code and it is correct. It moves to a `session_highwater` table
(operational state for live sessions, cleared by `Forget`) and keeps its exact current semantics.
**A worker who "simplifies" the high-water fold has broken the mission.**

**Decision 4 - Pruning must not cost an all-time number.** Today `PruneLocked`
(`GatewayInputStatsAggregator.cs:310-315`) prunes only the hourly buckets, at
`RetentionDays = 90`, and the all-time totals live in a separate dictionary so they survive. With
one delta table, naively deleting rows past the cutoff would silently shrink the all-time totals -
the same class of failure as #1376. So: on prune, fold the departing rows into an archive row
before deleting them, so all-time sums stay exact forever while the detailed rows stay bounded.
The distinct-session sets keep their own narrow table (`INSERT OR IGNORE`), unpruned, as today.

**Decision 5 - Migration preserves the numbers, and that is the acceptance test.** On first run
against an existing JSON store, import it into the database, then rename the JSON aside rather than
deleting it. The owner's all-time totals, peaks, and distinct counts must read **identically**
before and after. Pull request #1376 reset the concurrency peak on deploy and #1379 got the same
class of change right by not reshaping the existing fields; this mission does not get to be the
third data point. `PRAGMA user_version` carries the schema version from day one, so the next shape
change is a migration rather than a quarantine.

## Phases

Each phase is a self-contained increment that builds, tests green, and is reviewed by the Codex
reviewer before it is committed. Per the owner's instruction for this mission, phases accumulate on
`feat/gateway-sqlite` and are **not** merged to `origin/main` (see Constraints).

- **Phase 1 - Foundation plus the input store.** The database file, the connection and migration
  helper, `PRAGMA user_version`, the `stat_delta` and `session_highwater` tables, the one-time JSON
  import, and `GatewayInputStatsAggregator` rewritten onto it. This is the proof: the largest,
  worst-behaved store, ported with its numbers intact.
- **Phase 2 - The concurrency store.** `GatewaySessionConcurrencyStats` onto the same database,
  same import discipline. Restores the all-time peak that #1376 destroyed if the quarantined file
  is still on disk; if it is not, say so plainly rather than inventing a number.
- **Phase 3 - Car Mode telemetry.** The worst *shape* of the three: per-turn events held in a JSON
  list that is rewritten in full on every append. The retention policy itself is sound and is not
  the problem - `RetentionDays = 90` is what the owner asked for and does the real limiting, and
  `MaxRecords = 10000` is documented as an unbounded-growth guard set far above any realistic
  90-day volume (`CarModeTelemetryStore.cs:26-31`). Both carry over: retention becomes a
  `DELETE WHERE recorded_at < ...`, and the guard stays as the same cheap safety net it is today.
  What changes is only the shape - events belong in rows, appended one at a time, instead of a
  document rewritten from scratch on every turn.
- **Phase 4 - The remainder, only if Phases 1 to 3 prove out.** `netdiag-rollup`,
  `netdiag-devices`, `cronruns`, `voice-sessions`. Each is judged against the scope line above on
  its own merit. This phase may correctly end in "leave them alone."

Phase 1 must be complete and verified before Phase 2 starts. A phase is not done because the code
compiles; it is done when the numbers are proven unchanged.

## Acceptance

The mission succeeds when all of the following hold:

1. `GET /stats/data` returns the same numbers after the port as before it, for the owner's real
   data on this machine. Captured before, captured after, compared field by field. This is the
   primary proof and it is not negotiable.
2. The `/stats` page and the "Your Throttle" views in the cockpit and the mobile application render
   unchanged. No user-visible change is a feature of this mission, not a shortfall.
3. A counter move performs one row write, not a document rewrite. Demonstrated, not asserted.
4. A new question can be answered with a query against `stat_delta` and no schema change. The
   Manager picks one the owner has not asked for yet and shows the query.
5. Tests are green across all seven test projects, not only Core and Gateway.
6. A regression test pins the migration: given a real JSON store, the imported totals match.

## Constraints

- **Owner override on merging.** The owner's explicit instruction for this mission: the Architect
  may commit freely, but this work is **not** pushed and **not** merged to `origin/main`. This
  deliberately suspends the usual trunk rule that merged-to-main is the only "done" for the
  duration of this mission. The mission accumulates on `feat/gateway-sqlite` in the worktree
  `D:\ReposFred\devthrottle-gateway-sqlite`.
- **One worktree for the whole mission.** All roles work in
  `D:\ReposFred\devthrottle-gateway-sqlite`. The shared checkout `D:\ReposFred\devthrottle` is
  never used for this work and never holds uncommitted mission files.
- **The Codex reviewer gates every commit.** No code is committed until the Codex reviewer has
  reviewed it. See Roles.
- **Do not touch the Director-side counting seam.** The honest count happens at
  `Session.SendInput` and `Session.SendTextAsync`, pinned by
  `src/CcDirector.Core.Tests/TerminalPromptInjectionChokepointTests.cs`. This mission changes
  where the Gateway *stores* what it is told, and nothing about what is counted or what it means.
- **No fallback programming.** A database that fails to open is a loud failure with a clear
  message, never a silent fall back to the JSON store.
- **No Unicode characters anywhere** - plain ASCII in all code, comments, output, and documents.
- **Plain English, no abbreviations** in all output.

## Roles

- **Architect** - session 0a4297a9, "Gateway SQLite - Architect". Settles the design in this
  document, then lets the Manager drive without gating it. Does not implement.
- **Manager** - one session, "Gateway SQLite - Manager", spawned once this document is approved.
  Owns execution and drives the phases. Reset per phase.
- **Codex reviewer** - the `codex` command line tool, version 0.143.0, at
  `C:\Users\soren\AppData\Local\Programs\OpenAI\Codex\bin\codex.exe`. Reviews this document before
  work starts, and reviews every code change before it is committed. Its concerns are addressed or
  explicitly answered - not ignored.
- **Workers** - as many as the Manager needs, all in the one mission worktree.

## Gate

Work starts only when both are true:

1. The Codex reviewer has reviewed and approved this document.
2. The owner has approved this document.

After both, the mission runs autonomously through the phases without bothering the owner again
until the quality-assurance report.

## Follow-ups, deliberately not in this mission

- The transcription log has no retention policy, stated either way, and grows about 1.7 MB per week
  per machine while holding transcript text. Worth an owner decision at some point. Not a blocker.
- The `/stats` page is a self-contained embedded HTML document
  (`Stats/StatsPageEndpoint.cs:74-79`) rather than a cockpit route. Deliberate. Not touched here.
- An agent-facing `cc-devthrottle throttle` verb was owed by the earlier Stats mission and never
  built. Unrelated to storage. Not picked up here.
</content>
</invoke>
