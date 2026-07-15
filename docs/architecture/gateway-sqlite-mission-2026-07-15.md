# Mission Brief: SQLite on the Gateway

Status: PROPOSED - revised twice after Codex review, awaiting the Codex reviewer's final sign-off
and the owner's approval. No code is written until both approve this document.

Written 2026-07-15 by the Architect session ("Gateway SQLite - Architect", session 8c17dc1c,
machine SOREN_NORTH). This document is the Architect's handover to the Manager. The Manager owns
execution from here; the Architect settles the design and then lets the Manager drive.

Revision 2 folded in the Codex reviewer's round-one findings, recorded in full at
`docs/architecture/gateway-sqlite-review-codex-2026-07-15.md`. That review returned CHANGES
REQUIRED and was right to: it found that the original import design promised something the existing
data cannot deliver. The design is materially different as a result. The section "What the
historical data cannot tell us" is the most important section in this document.

Revision 3 folds in the round-two findings, recorded at
`docs/architecture/gateway-sqlite-review-codex-round2-2026-07-15.md`, which approved the baseline
design and then found three more holes in it. The sharpest: the row schema had no way to express a
wingman turn, because wingman turns are **not** voice-modality turns (Decision 2 explains). The
other two: a fold is not "one row write" and the brief should stop claiming it, and archive rows
sharing a table with real rows need an explicit query rule or they invent a phantom bucket in the
working-day series. All three are folded in below.

Two reviews, eleven accepted defects. That is the reviewer doing its job on a document, which is
the cheapest place in this mission for it to happen.

Every claim below carries a file and line citation. Revision 1 asserted that all of its citations
had been verified against `origin/main`; the Codex review proved that claim was itself overstated -
two citations did not resolve to what revision 1 said they did, and both are corrected here.

**Every citation in this document was re-checked, line by line, against `origin/main` at commit
1c1fa17c, after rebasing this branch onto it on 2026-07-15.** This matters: the branch was cut at
8c64a049 and `origin/main` moved six commits underneath it while this document was being reviewed.
One of those commits (#1624) reshaped `GatewayEndpoints.cs`, which moved the two fold citations in
point 3 below from lines 813 and 820 to 842 and 849. The mechanism was unchanged, but the line
numbers had quietly become fiction - which is precisely the failure this repository has a standing
rule against, and it happened to this document during its own review. It is recorded here rather
than quietly fixed, because the next person to leave a branch sitting while it is reviewed should
expect the same. Any future revision of this brief must re-verify against `origin/main` at the time
of writing, not trust this line.

Disk measurements were taken on SOREN_NORTH on 2026-07-15, independently by the Architect and the
Codex reviewer.

## The why

The Gateway keeps its statistics in a set of hand-rolled JSON files. That was the right call when
there was one store. There are several now, and the pattern has started to cost real money in three
specific ways:

1. **Every new question costs a schema change and a deploy.** `GatewayInputStatsAggregator` keeps a
   separate dictionary per question: `_totals`, `_highWater`, `_hourly`, `_wingmanSessions`,
   `_repos`, `_agents` (`src/CcDirector.Gateway/Stats/GatewayInputStatsAggregator.cs:31-66`). Each
   new dimension the owner asks for is a new dictionary, a new field on the store file, a new
   data-transfer object field, and a redeploy. Per-repository and per-agent tallies were both added
   this way after the Stats mission was declared finished, which is the evidence that the questions
   keep coming.

2. **A shape change has already silently destroyed live data.** There is no schema versioning
   anywhere in these stores. When the store file fails to deserialize, or deserializes to null, it
   is renamed to `.corrupt-{timestamp}` and the store restarts empty (the load path is
   `GatewaySessionConcurrencyStats.cs:205-227`; the quarantine method itself is `:249-254`). That
   path is how pull request #1376 wiped the all-time fleet concurrency peak of 35 on deploy. It was
   noticed only because someone happened to be looking.

   The precise statement matters, because the reality is worse than "a shape change is treated as
   corrupt". A shape change is quarantined only when deserialization *throws* or returns null. A
   change that merely removes or renames a field does not throw - it deserializes to defaults, and
   the store silently comes up with zeros where real numbers used to be, with no `.corrupt` file to
   notice. Loud quarantine is the *detectable* failure mode; silent default-filling is the one that
   could pass unremarked.

3. **The write is a whole-document rewrite on a request path.** The input stats store re-serializes
   the entire document and rewrites the file whenever any counter moves
   (`GatewayInputStatsAggregator.cs:123` and `:137` call `Save()`; `Save()` builds the full
   document, serializes it, writes a temporary file, and moves it over the original at `:555-583`),
   and the fold runs on **every** `GET /sessions` read
   (`src/CcDirector.Gateway/Api/GatewayEndpoints.cs:842` for input stats, `:849` for concurrency).
   That is O(all history) of synchronous work under a lock, per roster poll. It compounds with the
   deliberately unbounded distinct-session sets described below: the file grows forever *and* is
   rewritten constantly.

SQLite makes all three go away: a narrow table of rows answers new questions with a `GROUP BY`
instead of a deploy, `PRAGMA user_version` gives real migrations instead of quarantine-and-lose,
and a counter move becomes one delta row plus a little bounded bookkeeping, instead of a rewrite of
the entire file. (Revision 1 said "one row write". That was too strong, and Decision 2 below states
what a fold actually costs.)

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

Measured on SOREN_NORTH, 2026-07-15, independently by both the Architect and the Codex reviewer
(the two measurements agree to within a few hundred bytes; these are live files). All paths resolve
through `CcStorage.Root()` (`src/CcDirector.Core/Storage/CcStorage.cs:28-38`) to
`%LOCALAPPDATA%\cc-director`.

| Store | Class | Size | Shape |
|---|---|---|---|
| `gateway-input-stats.json` | `Stats/GatewayInputStatsAggregator.cs:107` | 59 KB | Counters, hourly buckets, all-time sets |
| `carmode-telemetry.json` | `CarMode/CarModeTelemetryStore.cs:45-47` | 32 KB | Per-turn events in a rewritten list |
| `netdiag-rollup.json` | network diagnostics | 22 KB | Rollup |
| `netdiag-devices.json` | network diagnostics | 20 KB | Rollup |
| `gateway-concurrency-stats.json` | `Stats/GatewaySessionConcurrencyStats.cs:68-70` | 16 KB | Hourly buckets, all-time peaks |
| `cronruns.json` | scheduler | 16 KB | Run history |
| `voice-sessions.json` | `Wingman/WingmanVoiceService.cs:105` | 13 KB | A flat array of session id strings |

That is about 178 KB in total. **Size is not the problem and must not be used to argue this
mission.** These are rolled-up aggregates, and aggregates stay small.

**A premise from revision 1 that was wrong, corrected here.** Revision 1 said these were seven
files "each with its own copy of the same load, serialize, atomic temp-write-and-rename, and
quarantine-on-corrupt code". That is not true of all of them, and `voice-sessions.json` is the
clearest counter-example: it is a flat `string[]` of session ids, loaded at
`WingmanVoiceService.cs:113-122` and saved with a direct `File.WriteAllText` at `:125-128`. It has
no atomic temporary-write-and-rename, no quarantine, and it is not a statistics rollup at all - it
is an operational set of which sessions have voice mode on. It should never have been listed as one
of the "same pattern" stores. It is dealt with under Phase 4 below on its own merit, and the honest
default for it is "leave it alone".

**Explicitly out of scope - the append-only logs stay exactly as they are.** Two stores are
JSON Lines files rather than rewritten documents:

- `prompt-log/conversation-yyyyMMdd.jsonl`, 3.3 MB across 5 days
  (`src/CcDirector.Gateway/Prompts/GatewayPromptLog.cs:51-55`)
- `transcription-log/transcription-YYYYMMDD.jsonl`, 1.7 MB across 7 days
  (`src/CcDirector.Gateway/Transcription/TranscriptionTelemetryLog.cs:49-54`, appending one line at
  `:67-70`)

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

**The scope line for this mission, stated once.** Revision 1 said "a store moves to SQLite if and
only if it rewrites its whole document to record an incremental change". Read literally that sweeps
in most of the Gateway - `WorkListStore`, `SnoozeRegistry`, `PushSubscriptionStore` and other
operational stores all persist that way and have nothing to do with statistics. The rewrite test is
necessary but not sufficient. The scope line is therefore narrowed to both conditions:

> A store is in scope only if (a) it is one of the statistics, diagnostics, scheduler-history, or
> Car Mode telemetry files named in the table above, **and** (b) it rewrites its whole document to
> record an incremental change.

Append-only JSON Lines logs stay on disk as JSON Lines. Operational stores outside the table are
not touched by this mission, whatever their write pattern.

## What the historical data cannot tell us

**This section is new in revision 2 and it changes the design. It is the single most important
thing in this document.**

Revision 1 promised: import the existing JSON into `stat_delta` rows so that all-time totals,
peaks, and distinct counts read identically before and after. The Codex reviewer showed that the
first half of that promise cannot be kept, and reading the code confirms it.

The existing store does not hold history. It holds **three independent projections of history,
each already collapsed on a different dimension** (`GatewayInputStatsAggregator.cs:448-457` defines
the persisted document; `:460-481` define the shapes):

- `Hourly`: keyed by clock hour, holding `VoiceTurns`, `TypedTurns`, `Characters`. It does not know
  which session, repository, or agent.
- `Repos`: keyed by repository path, holding the same three counters plus a `Sessions` list. It
  does not know which hour.
- `Agents`: keyed by agent name, holding the same three counters plus a `Sessions` list. It does
  not know which hour.
- `Totals`: keyed by modality and surface (`:31-32`). It does not know hour, repository, or agent.

A single past turn contributed to a bucket in each projection, but **which** hour goes with
**which** repository with **which** agent was never written down. The cross-product does not exist
on disk and cannot be recovered. Therefore a row of the form
`(hour_utc, session_id, modality, surface, repo, agent, turns, chars)` **cannot be reconstructed
for any historical turn.** Any attempt to synthesize such rows would be inventing data, and summing
the synthetic rows across one dimension would disagree with the real totals in another.

This is not a novel discovery about this codebase so much as a rediscovery. The code already hit
exactly this wall and already answered it honestly. `_agentsSinceUtc` exists for this reason, and
its comment (`GatewayInputStatsAggregator.cs:63-66`) says it plainly:

> "When the per-agent tally started counting. The totals predate this breakdown, so the agent
> numbers do NOT reconcile with them and the page must say so rather than imply the earlier turns
> had no agent."

That is the precedent, set by this repository, for the correct answer: when a dimension did not
exist before a date, you record the date and you say so. You do not back-fill a guess.

**The design consequence, stated plainly:** the past is a baseline, and only the future is rows.
The mission still delivers its point - the next question the owner asks becomes a query rather than
a deploy - but it delivers it **for data from the cutover forward**, not retroactively. Any brief,
this one included, that implies otherwise is promising something the disk cannot supply. The owner
should approve this mission knowing that.

## The design

Five decisions the Architect is settling. The Manager owns everything below this line's detail.

**Decision 1 - One database file, raw Microsoft.Data.Sqlite.** `gateway-stats.db` under
`CcStorage.Root()`, beside the stores it replaces. Raw `SqliteConnection` with
`CREATE TABLE IF NOT EXISTS`, matching `DatabaseService.cs:47-60`. No Entity Framework, no Dapper.
Write-ahead logging mode. The Gateway is a single process and therefore a single writer, so no
cross-process write contention exists. The Gateway must gain its own explicit `PackageReference`
rather than leaning on the transitive one through Core.

**Decision 2 - Rows, not counters, from the cutover forward. This is the point of the mission.**
Do NOT port the existing dictionaries into six tables; that would buy nothing but a new dependency.
For every delta observed **after** the cutover, store one narrow row carrying its dimensions as
columns:

```
stat_delta(hour_utc, session_id, modality, surface, is_voice, repo, agent, wingman, turns, chars)
```

**The `wingman` column is load-bearing and is not the same thing as `modality = 'voice'`.** It
records `SessionDto.VoiceMode` as observed at fold time. The distinction is easy to miss and the
Codex reviewer caught the brief missing it: `GatewayInputStatsAggregator.cs:425-427` folds the
session's **entire** turn delta into `_wingmanTurns` whenever `s.VoiceMode` is true, and that delta
includes typed turns. A turn typed while voice mode is on is a wingman turn today. The owner's
definition, recorded at `:44-47`, is "a session uses the wingman when it has voice mode on" - it is
a property of the session's mode, not of how any single turn was entered. Without this column,
post-cutover wingman turns could not be derived from rows at all, and no `GROUP BY modality` would
recover them. Wingman turns become `SUM(turns) WHERE wingman = 1`.

Each dashboard number becomes a baseline value plus a query over the rows recorded since:

- all-time totals by modality and surface - baseline plus `SUM(...) GROUP BY modality, surface`
- the working-day series - baseline hourly buckets, plus `GROUP BY hour_utc` over real hour keys
  for recent hours (see the archive-marker rule in Decision 4)
- the per-repository page - baseline plus `GROUP BY repo`
- the per-agent page - baseline plus `GROUP BY agent`
- wingman turns - baseline plus `SUM(turns) WHERE wingman = 1`

Three honest qualifications, all raised by the Codex reviewer and all accepted:

- **Equality must be decided in C#, not by SQLite's collation.** `_repos` and `_agents` are keyed
  `OrdinalIgnoreCase` (`GatewayInputStatsAggregator.cs:55`, `:61`), while SQLite's default `BINARY`
  collation is case-sensitive. A naive `GROUP BY repo` would therefore split buckets that are one
  bucket today. The live store happens to contain no case collisions, which is what makes this
  dangerous: parity would pass green and the split would appear later, silently, the first time a
  path was reported with different casing. `COLLATE NOCASE` is not the answer either - it folds
  only ASCII, so it agrees with `OrdinalIgnoreCase` right up until a non-ASCII repository path,
  and then quietly disagrees. So: fold the key in C# and store the folded key for grouping
  alongside a first-seen display value in an identity table. One layer decides equality, and it is
  the same layer that decides it today. The same reasoning gives `stat_delta` an explicit
  `is_voice` column computed in C# at fold time rather than re-derived from a string comparison in
  SQL, because `_totals` is keyed case-sensitively while the voice test is not, and that mismatch
  must not be reproduced in the query layer.
- **A fold is not "one row write", and it is not one row either.** A fold walks the session's input
  buckets (`foreach (var b in s.InputStats.Buckets)`, `GatewayInputStatsAggregator.cs:343`), so a
  correct post-cutover fold is one `INSERT` into `stat_delta` **per changed bucket** - a session
  reporting movement on three modality-and-surface pairs writes three rows, not one - plus one
  upsert into `session_highwater`, plus possibly an `INSERT OR IGNORE` into a distinct-id table
  when a repository, agent, or wingman session is seen for the first time.

  **The work is bounded by what changed, not by how much history exists**, and that - not any
  particular statement count - is the actual win over an O(all history) document rewrite. Be
  careful with the word "bounded": it describes the write cost of a fold. It does **not** describe
  the distinct-id tables, which are deliberately never pruned (Decision 2,
  `GatewayInputStatsAggregator.cs:45-61`) and grow forever by design. Both facts are true at once
  and the brief means the first one.

- **"A new question is a query, not a deploy" holds only for dimensions already carried on the
  row.** A question about a dimension `stat_delta` does not capture still needs a schema change and
  a deploy - it is just that the migration is now a real migration instead of a quarantine. The
  win is real but it is narrower than revision 1 implied.
- **Distinct counts are not `COUNT(DISTINCT session_id)` over `stat_delta`.** That is exact only
  while every contributing row is still present, so it stops being exact the moment pruning starts
  (see Decision 4), and it cannot see pre-cutover sessions at all. The all-time distinct sets keep
  their own narrow, never-pruned identifier tables, seeded from the existing `Sessions` lists and
  `_wingmanSessions`, and extended with `INSERT OR IGNORE`. This preserves exactly the semantics
  the code documents at `GatewayInputStatsAggregator.cs:45-61`.

**Decision 3 - The high-water logic survives unchanged, because SQL does not replace it.** The
idempotent fold (`FoldLocked`, `GatewayInputStatsAggregator.cs:320`, comparing reported counters
against previous high-water at `:336-354` and handling counter reset at `:350-354`) is what makes
re-reading a roster safe and what lets counts survive a Director or Gateway restart without
double-counting. It is the hard part of this code and it is correct. It moves to a
`session_highwater` table (operational state for live sessions, cleared by `Forget`) and keeps its
exact current semantics. **A worker who "simplifies" the high-water fold has broken the mission.**

**Decision 4 - Pruning must not cost an all-time number.** Today `PruneLocked`
(`GatewayInputStatsAggregator.cs:310-315`) prunes only the hourly buckets, at `RetentionDays = 90`,
and the all-time totals live in separate dictionaries so they survive. With a delta table, naively
deleting rows past the cutoff would silently shrink the all-time totals - the same class of failure
as #1376. So, on prune, departing rows are folded into archive rows before deletion.

Revision 1 said "an archive row", singular, which is not enough and would itself have lost data.
The archive must **preserve every grouping dimension that any all-time answer is derived from** -
modality, surface, repository, agent, and the `wingman` flag. Concretely: pruning collapses the
hour and the session identifier, and nothing else. An archive row is a `stat_delta` row with its
`hour_utc` and `session_id` replaced by archive markers and every other column preserved, so that
every all-time `GROUP BY` over the remaining dimensions still returns the same sum. Distinct counts
are unaffected because they never read `stat_delta` (Decision 2).

**The archive-marker query rule, stated explicitly because leaving it implicit is how this design
breaks.** Archive rows and real rows share one table, so every query must declare which it wants:

- **All-time aggregate queries include archive rows.** That is the entire point of archiving - the
  totals must not shrink when detail is pruned.
- **Hourly and working-day queries filter to real hour keys and exclude the archive marker.**
  `HourlyTurns()` (`GatewayInputStatsAggregator.cs:185-203`) returns an ordered hour series today,
  and pruning drops old hours from it rather than folding them into a catch-all bucket
  (`:310-315`). A plain `GROUP BY hour_utc` over a table containing archive rows would invent a
  fake bucket in that series and change what the user sees. It must not.

Whether an all-time answer survives pruning is a testable property, and Acceptance criterion 7
pins it. That the working-day series does **not** grow a phantom bucket is equally testable, and
criterion 7 pins that too.

**Decision 5 - The import is a baseline, not a reconstruction, and it is fail-loud.** This decision
is rewritten from revision 1, per the section "What the historical data cannot tell us".

On first run against an existing JSON store, each historical projection is imported **as it stands**
into its own baseline table. No historical `stat_delta` rows are synthesized, ever. Every reported
number is then baseline plus post-cutover rows, which is exact by construction and matches the
existing `_agentsSinceUtc` precedent for a dimension that starts partway through.

**Import all eight sections of `StoreFile`. Not six. This is stated as a count because the count is
how the omission was caught.** The persisted document has exactly eight sections
(`GatewayInputStatsAggregator.cs:448-457`): `Totals`, `HighWater`, `Hourly`, `WingmanTurns`,
`WingmanSessions`, `Repos`, `Agents`, and `AgentsSinceUtc`. Revisions 1 through 3 of this brief
listed six of them and silently dropped `HighWater` and `AgentsSinceUtc`. The Manager caught it
before any code was written. What that omission would have cost, measured on the owner's real store
on 2026-07-15:

> All-time totals hold **1404** turns. `HighWater` holds **842** turns across **115** live
> sessions. Start `session_highwater` empty and the very first `GET /sessions` poll refolds every
> one of those sessions from zero - `FoldLocked` treats a missing high-water entry as
> `prevTurns = 0` and folds the session's **entire** tally as fresh activity
> (`GatewayInputStatsAggregator.cs:336-354`). The owner's totals become **2246**. A silent 60 per
> cent inflation of every number this mission promised to preserve.
>
> (These counts are a snapshot of a live, moving store - see criterion 1 below. The exact figures
> drift by the minute; the ratio is the point, and the ratio is roughly three fifths.)

**`session_highwater` is not a baseline. It is live operational state, and it must be imported so
that the fold's idempotency survives the cutover.** The high-water map is precisely what makes
re-reading a roster safe (Decision 3). An import that restores the aggregates but not the
high-water has not preserved the numbers - it has armed a bomb on the next poll.

**This omission also proves the endpoint-field parity check is necessary but NOT sufficient**, and
that is the more important lesson. `/stats/data` exposes no high-water field
(`Stats/StatsPageEndpoint.cs:40-71`), so the parity check specified below would have read every
exposed field, found every one of them correct, passed green, and renamed the JSON aside - and the
damage would have landed on the *next* roster poll, after the only copy of the truth was already
moved. **A parity check can only see what the endpoint exposes. State that a store's internal
operational state is invisible to it, and check that separately.** Acceptance criterion 6 gains an
idempotent-re-observe leg for exactly this reason.

`AgentsSinceUtc` is the other dropped section. It is a single string, it is load-bearing for an
honest page (`GatewayInputStatsAggregator.cs:63-66`), and losing it would silently re-stamp the
agent breakdown as starting at the cutover - making the agent numbers look like they reconcile with
the totals when they do not. Import it verbatim.

The import sequence is fixed and is not open to a worker's interpretation:

1. Import inside a **single transaction**, and stamp an import marker plus `PRAGMA user_version` in
   that same transaction, so a crash cannot double-import or strand a partial import.
2. Read every affected endpoint field back **out of SQLite** and compare it field by field against
   what the JSON store reported before the import.
3. Only on a complete match, rename the JSON aside (never delete it).
4. **On any mismatch, fail loudly**: leave the JSON in place as the source of truth, do not mark
   the import done, and surface a clear error. Never come up empty, never come up with partial
   numbers, never silently fall back. This follows the house rule against fallback programming, and
   it is the direct antidote to the failure mode in point 2 of "The why": the existing code's
   default-filling comes up quietly with zeros, and this must not.

`PRAGMA user_version` carries the schema version from day one, so the next shape change is a
migration rather than a quarantine. Pull request #1376 reset the concurrency peak on deploy and
#1379 got the same class of change right by not reshaping the existing fields; this mission does
not get to be the third data point.

## Phases

Each phase is a self-contained increment that builds, tests green, and is reviewed by the Codex
reviewer before it is committed. Per the owner's instruction for this mission, phases accumulate on
`feat/gateway-sqlite` and are **not** merged to `origin/main` (see Constraints).

- **Phase 1 - Foundation plus the input store.** The database file, the connection and migration
  helper, `PRAGMA user_version`, the `stat_delta`, `session_highwater`, baseline, and distinct-id
  tables, the one-time baseline import with its parity check, and `GatewayInputStatsAggregator`
  rewritten onto it. This is the proof: the largest, worst-behaved store, ported with its numbers
  intact.
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
  document rewritten from scratch on every turn. Note this store genuinely does hold per-event
  records, so unlike the input store its history ports across as real rows.
- **Phase 4 - The remainder, only if Phases 1 to 3 prove out.** `netdiag-rollup`, `netdiag-devices`,
  `cronruns`, and `voice-sessions`, each judged against the narrowed scope line on its own merit.
  `voice-sessions` in particular is an operational set of identifiers, not statistics, and the
  expected answer for it is "leave it alone" unless a specific reason appears. This phase may
  correctly end in "leave them all alone."

Phase 1 must be complete and verified before Phase 2 starts. A phase is not done because the code
compiles; it is done when the numbers are proven unchanged.

Phase 1 proves the input-store design, the baseline import discipline, and the migration mechanism.
It does **not** prove concurrency-peak restoration or any Phase 4 store choice; those are proven in
their own phases and must not be waved through on Phase 1's evidence.

## Acceptance

The mission succeeds when all of the following hold. Each is falsifiable; a criterion that cannot
fail is not a criterion.

1. `GET /stats/data` returns the same numbers after the port as before it, for the owner's real
   data on this machine. Captured before, captured after, compared field by field
   (`Stats/StatsPageEndpoint.cs:40-71` maps this response from the aggregator outputs). This is the
   primary proof and it is not negotiable.

   **Run it with the Gateway STOPPED, against a frozen copy of the store.** The live store is a
   moving target: the Manager observed it go from 1401 to 1404 turns while reading it twice, partly
   because this mission's own sessions are generating turns into the very store it is porting. A
   before-and-after comparison against a running Gateway will therefore differ for reasons that
   have nothing to do with the port, and the tempting response - loosen the comparison until it
   passes - would throw away the only real proof this phase has. An exact comparison against a
   frozen snapshot is worth more than a fuzzy one against live data. If this criterion is ever
   reported as passing with a tolerance, it did not pass.

   The import's own parity check (Decision 5) is unaffected by this and stays exact: it compares
   what SQLite reports against the same single in-memory load of the JSON document, so nothing can
   move underneath it.
2. The `/stats` page (`Stats/StatsPageEndpoint.cs:74-79`), the cockpit "Your Throttle" view
   (`apps/cockpit/src/throttle/YourThrottleView.tsx`), and the mobile "Your Throttle" view
   (`apps/mobile/src/pages/YourThrottle.tsx`) render unchanged. **Evidence, not opinion:** the
   captured `/stats/data` payload from criterion 1, plus a before-and-after screenshot of each of
   the three surfaces, attached to the phase report. "Looks fine" is not acceptance.
3. A counter move costs work bounded by what changed, and that cost does not grow with history,
   rather than a whole-document rewrite. **Demonstrated by:** an integration test that observes a
   delta and asserts the expected statement mix for the real schema - one `INSERT` into
   `stat_delta` **per changed bucket**, one upsert into `session_highwater`, and at most one
   `INSERT OR IGNORE` per distinct-id table - plus an assertion that no JSON store file's
   last-write timestamp changes after the import has completed.

   Two things this test must get right, both of which a careless version gets wrong:
   - **Expect one row per changed bucket, not one per fold.** A session reporting movement on three
     modality-and-surface pairs must write three rows. A test asserting "one row per fold" pins the
     wrong behaviour and would go green against a fold that silently drops buckets.
   - **Pin that the mix is unchanged when the table already holds a large number of historical
     rows.** "Does not grow with history" is the actual claim being proven, and a test against an
     empty table proves nothing about it.
4. A new question that uses dimensions already carried on `stat_delta` is answered by a query with
   no schema change, for data recorded since the cutover. The Manager picks one the owner has not
   asked for yet and shows the query and its result.
5. Tests are green across all seven test projects in `cc-director.sln` (Avalonia, Core, Engine,
   Gateway, HostedAgent, Launcher, and Terminal.Avalonia), not only Core and Gateway.
6. Regression tests pin the baseline import, on three legs:

   (a) Given a real JSON store as a fixture, every imported value matches the value the JSON store
   reported - across **all eight** `StoreFile` sections, not only the ones `/stats/data` exposes.

   (b) **The idempotent re-observe leg.** After importing, observe the *same* roster the store was
   already carrying high-water for, and assert every all-time number is **unchanged**. This is the
   leg that catches a dropped or mis-imported `session_highwater`, and it exists because nothing
   else can: `/stats/data` exposes no high-water field, so the endpoint parity check passes green
   while the totals are one poll away from inflating by three fifths. A test that only compares
   exposed endpoint fields is not sufficient and this criterion does not accept one.

   (c) The import refuses to complete on an induced mismatch, leaving the JSON as the source of
   truth.

   Legs (b) and (c) must be proven by watching them go red - revert the fix, see the reported
   symptom, restore it - not asserted. A test that has never failed on purpose is not evidence.
7. Regression tests pin pruning, on both legs: given rows spanning the retention boundary, (a) every
   all-time answer - including wingman turns and every per-repository and per-agent tally - is
   identical before and after a prune runs, and (b) the working-day series contains exactly the
   real hours it contained before, with no phantom bucket from an archive marker.
8. A regression test pins wingman-turn semantics: a turn **typed** while the session has voice mode
   on is counted as a wingman turn, matching `GatewayInputStatsAggregator.cs:425-427` today. This
   is pinned because the distinction is subtle enough that the first draft of this brief missed it,
   and a worker who assumes wingman turns means `modality = 'voice'` would silently change the
   owner's numbers.

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
  reviewed it. Reviews are written to files under `docs/architecture/`, never left in a terminal -
  the session terminal buffer captures only spinner redraw and the findings are unrecoverable from
  it. See Roles.
- **Do not touch the Director-side counting seam.** The honest count happens at
  `Session.SendInput` and `Session.SendTextAsync`, pinned by
  `src/CcDirector.Core.Tests/TerminalPromptInjectionChokepointTests.cs`. This mission changes
  where the Gateway *stores* what it is told, and nothing about what is counted or what it means.
- **No fallback programming.** A database that fails to open is a loud failure with a clear
  message, never a silent fall back to the JSON store. An import that does not reconcile is a loud
  failure, never a partial import.
- **No Unicode characters anywhere** - plain ASCII in all code, comments, output, and documents.
- **Plain English, no abbreviations** in all output.

## Roles

- **Architect** - session 8c17dc1c, "Gateway SQLite - Architect". Settles the design in this
  document, then lets the Manager drive without gating it. Does not implement.
- **Manager** - one session, "Gateway SQLite - Manager", spawned once this document is approved.
  Owns execution and drives the phases. Reset per phase.
- **Codex reviewer** - session 7068ec90, "Gateway SQLite - Codex Reviewer", running the `codex`
  command line tool as a real DevThrottle session. Reviewed this document before work started, and
  reviews every code change before it is committed. Its concerns are addressed or explicitly
  answered - not ignored. Revision 2 of this document exists because it was right.
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
- **The model dimension - the likely first new question, and deliberately NOT built here.** Pull
  request #1637 landed drivers reporting the model each agent is currently using, and collecting
  that into statistics is a known open follow-up. That makes "which model do you actually drive?"
  the most likely next question asked of this schema, and it is tempting to add a `model` column to
  `stat_delta` now to get ahead of it. **Do not.** Checked against `origin/main` on 2026-07-15:
  `src/CcDirector.Gateway.Contracts/SessionDto.cs` carries no model field, so nothing the Gateway
  folds would populate that column. It would be a column nothing emits, and the test proving it
  would have to inject a value the product never produces - the exact shape of bug this repository
  keeps finding. The model dimension needs its producer first: a field on `SessionDto`, fed from the
  driver report, and only then a column and a migration.

  This is worth stating because it is a fair test of the mission's honesty. The mission does not
  claim that question becomes free - Decision 2 says plainly that a genuinely new dimension still
  costs a schema change. What the mission delivers is that the change is a `PRAGMA user_version`
  migration that keeps the owner's numbers, instead of a shape change that quarantines the file and
  loses them. That is the whole point, and the model dimension will be its first real exercise.
- The silent default-filling failure mode described in point 2 of "The why" affects every remaining
  hand-rolled JSON store in the Gateway, not only the ones this mission ports. The stores this
  mission moves are protected by `PRAGMA user_version`; the others are not. Worth an owner decision
  once this mission proves the pattern.
