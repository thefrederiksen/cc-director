# Phase 1 Plan: Foundation plus the input store

**Built against brief revision 6 (`6327fcd7`).** This plan was written against revision 3. All four
findings it raised were accepted by the Architect and are now in the brief itself: revision 4
(`a4e74b76`) imports all eight sections and fixes the parity hole, revision 5 (`c89b9171`) fixes the
one-row-per-fold defect, and revision 6 (`6327fcd7`) adds Decision 6, the membership mirror. Where
this plan and the brief now differ, **the brief wins** - it is the authority and it has absorbed
everything below.

Written 2026-07-15 by the Manager session ("Gateway SQLite - Manager", session f3599eba, machine
SOREN_NORTH). This is the Manager's execution plan for Phase 1 of the mission recorded in
`docs/architecture/gateway-sqlite-mission-2026-07-15.md`. It is written to a file rather than held
in the Manager's head because the Manager is reset before Phase 2.

The brief is the authority. Where this plan elaborates the brief it says so and explains why; where
it disagrees with the brief it does not proceed until the Architect and the Codex reviewer have
settled it. Three such points are raised below and are the reason this plan exists before any code.

## Freshness check, done first

`origin/main` was at `1c1fa17c` when the brief was written. It is at `6848bf69` now - **seven
commits have landed underneath this branch since the brief was approved**, which is the same thing
that happened to the brief during its own review. Re-verified, file by file, on 2026-07-15:

- `src/CcDirector.Gateway/Stats/GatewayInputStatsAggregator.cs` - **byte-identical** to
  `origin/main`. Every line citation in the brief that points into this file still resolves. This is
  the file Phase 1 rewrites, so this is the citation that mattered most.
- `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:842` is `inputStats?.ObserveSnapshot(all, DateTime.UtcNow)`
  and `:849` is `concurrency?.Observe(all, DateTime.UtcNow)`. Both still resolve.
- `src/CcDirector.Core/CcDirector.Core.csproj:18` still references `Microsoft.Data.Sqlite` 9.0.2.
- `src/CcDirector.Core/Storage/CcStorage.cs` **did change** (+5 lines) on `origin/main`. The brief
  cites `:28-38` for `Root()`. `Root()` is now line 28 and `Base()` spans 30-38, so the citation
  still resolves, but it moved and the next reader should re-check rather than trust this line.

None of the seven commits touch the input store. Phase 1's ground is unchanged.

## What the live data actually looks like

Read from `%LOCALAPPDATA%\cc-director\gateway-input-stats.json` on SOREN_NORTH, 2026-07-15. This is
the fixture Phase 1 must import without losing a number:

| Section | Content |
|---|---|
| `Totals` | 7 buckets: typed/cockpit, typed/desktop, typed/phone, typed/unknown, voice/cockpit, voice/desktop, voice/phone |
| `HighWater` | **116 live sessions** |
| `Hourly` | 71 hour buckets |
| `WingmanTurns` | 517 |
| `WingmanSessions` | 80 distinct ids |
| `Repos` | 20 keys |
| `Agents` | 2 keys (`ClaudeCode`, `Codex`) |
| `AgentsSinceUtc` | `2026-07-15T16:03:53.6255973Z` |

All-time totals when first read: 1401 turns, 568,580 characters. Re-read a few minutes later: 1404
turns, 569,144 characters, wingman turns 517 -> 520, wingman sessions 80 -> 81, high-water 116 ->
115. See Finding 4 - the store is live and moving, including because this mission's own sessions are
generating turns into it.

A verified copy is held at
`<scratchpad>\phase1-before\gateway-input-stats.snapshot.json` (61,307 bytes, parses clean, all
eight sections present). The concurrency store is copied beside it for Phase 2's benefit. This is
taken now because the import runs once against data that has no other backup.

### Finding 4 - The store is a moving target, so acceptance criterion 1 cannot be read literally

Criterion 1 says `GET /stats/data` "returns the same numbers after the port as before it ...
Captured before, captured after, compared field by field". Against a **running** Gateway those two
captures will legitimately differ, because real turns land in between - the numbers above moved
three turns in the minutes it took to read them twice. A field-by-field comparison would fail on
data that is perfectly correct, and the obvious way to "fix" a failing comparison is to loosen it
until it passes, which would throw away the only real proof this phase has.

This does not weaken the criterion; it pins down how to execute it:

- **The import's own parity check is self-consistent and unaffected.** Decision 5 step 2 compares
  what SQLite reports against what the JSON store reported *from the same load* - one in-memory
  document, not two captures separated by time. That is exact regardless of what the live Gateway is
  doing.
- **Criterion 1's before-and-after capture is done with the Gateway stopped**, against the frozen
  snapshot above, so the only thing that changes between the two captures is the port itself. A
  before-and-after taken across a live Gateway proves nothing and must not be offered as evidence.

## Three findings that change Phase 1

### Finding 1 - The import list omits the high-water map, and the parity check cannot catch it

**This is the most dangerous thing found so far and it is a genuine hole in the brief.**

Decision 5 lists what the import covers: "baseline totals by modality and surface, baseline hourly
buckets, baseline per-repository tallies, baseline per-agent tallies, baseline wingman turns, and
the distinct-session identifier tables". **The high-water map is not on that list.** Decision 3
mentions `session_highwater` as the table the fold logic moves to, but nothing says the existing
`HighWater` section is imported into it.

Why that is severe. `HighWater` holds 116 live sessions right now. The high-water fold
(`GatewayInputStatsAggregator.cs:336-354`) adds only the *increase* over the last-seen count, and
treats a reported count *lower* than last-seen as a fresh tally from zero (`:353`). If
`session_highwater` starts empty, then on the first `GET /sessions` after cutover every live session
has no previous high-water, so its **entire** current count is folded in as new activity - on top of
the baseline that already contains it. The all-time totals roughly double. This is the exact
double-count the high-water logic exists to prevent, re-introduced by an import that forgot it.

Why the brief's parity check does not catch it. Step 2 of Decision 5 compares "every affected
endpoint field" read back out of SQLite against what the JSON reported. `/stats/data`
(`Stats/StatsPageEndpoint.cs:40-71`) exposes buckets, hourlyTurns, wingman, repos, agents, and
agentsSinceUtc. **It does not expose the high-water map.** At import time `stat_delta` is empty, so
every endpoint field equals its baseline and parity **passes**. The import is marked done, the JSON
is renamed aside, and the damage happens on the next roster poll - after the source of truth has
been moved aside. Parity green, data destroyed.

**Proposed resolution.** Two changes, both inside Phase 1:

1. The import imports **all eight** sections of `StoreFile`
   (`GatewayInputStatsAggregator.cs:448-457`), not six. `HighWater` imports into `session_highwater`
   and `AgentsSinceUtc` into a baseline scalar. Nothing in that document is left behind.
2. Parity gains a leg the endpoint fields cannot express, because the endpoint cannot see this:
   **after the import, re-observe the exact roster the JSON store last saw and assert every all-time
   number is unchanged.** That is the idempotency property the high-water fold exists to provide,
   and it is the only check that actually fails when high-water is missing. Watch it fail first:
   with the high-water import removed, this test must go red with a doubled total.

This is proposed as an addition to acceptance criterion 6, not a change to the design.

### Finding 2 - Case folding: the .NET dictionaries and SQLite do not group the same way

`_repos` (`:55`) and `_agents` (`:61`) are `Dictionary<string, _>(StringComparer.OrdinalIgnoreCase)`.
SQLite's default text comparison is `BINARY` - case-sensitive. A plain `GROUP BY repo` therefore
**splits** what the current code **merges**. Two further wrinkles in the same area:

- `_totals` (`:32`) is keyed by a `(string, string)` tuple with the **default** comparer, which is
  ordinal and case-**sensitive**. So totals are case-sensitive while repos and agents are not. The
  two must not be given the same treatment.
- The voice test at `:366` is
  `string.Equals(key.Item1, "voice", StringComparison.OrdinalIgnoreCase)` - case-**insensitive**. So
  a modality of `Voice` is its own totals bucket but still counts as voice in the hourly, repository,
  and agent splits. That asymmetry is current behaviour and must survive.

**This is the sneaky one.** Checked against the owner's live store: there are **no** case-variant
collisions today (20 repo keys, 2 agent keys, all distinct case-insensitively). So the import parity
check **passes** either way, and the divergence appears only later, the first time a repository path
is reported with different casing. Parity would have signed off on a defect that had not happened
yet. It is worth noting the keys are already not normalised in other ways - `D:/ReposFred/devthrottle`
and `D:\ReposFred\devthrottle` are separate buckets today, and that is faithful and stays.

**Proposed resolution: do the folding in C#, never in SQL.** SQLite's `NOCASE` collation folds ASCII
only, while .NET's `OrdinalIgnoreCase` folds the full Unicode range, so leaning on the collation
would be *nearly* right - which is not a standard this mission accepts.

- `stat_delta` carries `repo_folded` and `agent_folded` as its grouping keys, computed in C# with
  the same `StringComparer.OrdinalIgnoreCase` semantics the dictionaries use today.
- `repo_identity(repo_folded PRIMARY KEY, repo_display)` and `agent_identity(...)` map the folded key
  to the **first-seen** spelling, extended with `INSERT OR IGNORE`. First-seen-wins is exactly what
  a .NET `Dictionary` does with an `OrdinalIgnoreCase` comparer, so the displayed spelling is
  unchanged. These are the same shape as the distinct-id tables Decision 2 already requires.
- `stat_delta` carries `is_voice` as an explicit column, computed in C# by the same
  `OrdinalIgnoreCase` test at `:366`. The voice/typed split becomes `SUM(CASE WHEN is_voice = 1 ...)`,
  with no dependence on any SQL collation. (It is derivable as `LOWER(modality) = 'voice'`, which for
  this one ASCII literal happens to be exactly equivalent - but storing it removes the argument.)
- `modality` and `surface` stay as plain columns grouped with the default `BINARY` collation, which
  is exactly the case-sensitive ordinal grouping `_totals` uses today.

This elaborates the brief's row schema. The brief states
`stat_delta(hour_utc, session_id, modality, surface, repo, agent, wingman, turns, chars)`. **The
Architect and the reviewer should confirm** that `repo`/`agent` becoming folded grouping keys plus an
identity table, and the addition of `is_voice`, is a faithful elaboration rather than a deviation.
The intent it serves is the brief's own: the numbers do not change.

### Finding 3 - The hot path must not query SQLite when nothing changed

`ObserveSnapshot` folds the **entire** roster on **every** `GET /sessions`
(`GatewayEndpoints.cs:842`) - 116 live sessions per poll. Today an unchanged poll costs pure
in-memory dictionary reads and **zero** input/output: `Save()` is called only `if (changed)`
(`:123`). A naive port that reads `session_highwater` from SQLite per session per poll would make
the common case *slower* than the JSON it replaces, which would be a real regression dressed up as
a refactor.

**Proposed resolution:** the bounded operational state - the high-water map, the wingman session set,
the repository and agent identity maps, and the distinct-id sets - keeps an in-memory mirror loaded
once at startup, and writes through to SQLite only when it actually changes. The aggregates -
totals, hourly, repos, agents - are **not** cached; they become real queries, because that is the
point of the mission and because they are read on `/stats/data` (a dashboard refresh), never on the
hot roster path.

This is not fallback programming: SQLite is the single source of truth on disk, the mirror is a
write-through cache of bounded operational state loaded from it, and there is no second code path
and nothing to silently fall back to. It is also what makes acceptance criterion 3 land exactly as
written - an unchanged poll issues **zero** statements, and a changed one issues the bounded mix.

## The schema

```sql
PRAGMA user_version = 1;   -- real migrations from day one; never quarantine-and-lose

-- Post-cutover rows only. Never synthesized for history (see "What the historical data
-- cannot tell us"): the cross-product of hour x repo x agent was never written down.
CREATE TABLE IF NOT EXISTS stat_delta (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,
  hour_utc     TEXT    NOT NULL,   -- 'yyyy-MM-ddTHH', or the archive marker
  session_id   TEXT    NOT NULL,   -- or the archive marker
  modality     TEXT    NOT NULL,   -- BINARY grouping, matching case-sensitive _totals
  surface      TEXT    NOT NULL,
  is_voice     INTEGER NOT NULL,   -- computed in C#; never LOWER() in SQL
  repo_folded  TEXT    NOT NULL,   -- OrdinalIgnoreCase-folded in C#; display via repo_identity
  agent_folded TEXT    NOT NULL,
  wingman      INTEGER NOT NULL,   -- SessionDto.VoiceMode at fold time; NOT modality='voice'
  turns        INTEGER NOT NULL,
  chars        INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_stat_delta_hour ON stat_delta(hour_utc);

-- Operational state for live sessions. The idempotent fold, semantics unchanged. Cleared by Forget.
CREATE TABLE IF NOT EXISTS session_highwater (
  session_id TEXT NOT NULL, modality TEXT NOT NULL, surface TEXT NOT NULL,
  turns INTEGER NOT NULL, chars INTEGER NOT NULL,
  PRIMARY KEY (session_id, modality, surface)
);

-- The past, imported as it stands. Every reported number is baseline + rows since.
CREATE TABLE IF NOT EXISTS baseline_total (modality TEXT NOT NULL, surface TEXT NOT NULL,
  turns INTEGER NOT NULL, chars INTEGER NOT NULL, PRIMARY KEY (modality, surface));
CREATE TABLE IF NOT EXISTS baseline_hour (hour_utc TEXT PRIMARY KEY,
  voice_turns INTEGER NOT NULL, typed_turns INTEGER NOT NULL, chars INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS baseline_repo (repo_folded TEXT PRIMARY KEY,
  voice_turns INTEGER NOT NULL, typed_turns INTEGER NOT NULL, chars INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS baseline_agent (agent_folded TEXT PRIMARY KEY,
  voice_turns INTEGER NOT NULL, typed_turns INTEGER NOT NULL, chars INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS baseline_scalar (name TEXT PRIMARY KEY, value TEXT NOT NULL);
  -- wingman_turns, agents_since_utc

-- All-time distinct sets. Deliberately never pruned (:45-61). NOT COUNT(DISTINCT) over stat_delta.
CREATE TABLE IF NOT EXISTS wingman_session (session_id TEXT PRIMARY KEY);
CREATE TABLE IF NOT EXISTS repo_session (repo_folded TEXT NOT NULL, session_id TEXT NOT NULL,
  PRIMARY KEY (repo_folded, session_id));
CREATE TABLE IF NOT EXISTS agent_session (agent_folded TEXT NOT NULL, session_id TEXT NOT NULL,
  PRIMARY KEY (agent_folded, session_id));

-- Folded key -> first-seen display spelling (what a .NET OrdinalIgnoreCase Dictionary does).
CREATE TABLE IF NOT EXISTS repo_identity (repo_folded TEXT PRIMARY KEY, repo_display TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS agent_identity (agent_folded TEXT PRIMARY KEY, agent_display TEXT NOT NULL);

-- Import marker, stamped in the same transaction as the import.
CREATE TABLE IF NOT EXISTS meta (name TEXT PRIMARY KEY, value TEXT NOT NULL);
```

The archive marker is the literal `ARCHIVE`, which cannot collide with a real `yyyy-MM-ddTHH` key.
Per Decision 4: all-time aggregates include archive rows; hourly and working-day queries carry
`WHERE hour_utc <> 'ARCHIVE'`. Pruning collapses **only** `hour_utc` and `session_id` and preserves
`modality`, `surface`, `is_voice`, `repo_folded`, `agent_folded`, and `wingman`.

## Semantics that must survive, each pinned by a test

Read out of the current code, and each one is a way this port could silently change the owner's
numbers:

1. **A typed turn while voice mode is on is a wingman turn** (`:425-427` folds the session's whole
   turn delta when `s.VoiceMode`). Acceptance criterion 8.
2. **A voice-mode session with no input still counts as a wingman session.** `:330` adds to
   `_wingmanSessions` *before* the `:333` early return on empty buckets. So `wingman_session` is
   written even when no delta row is. This asymmetry is easy to lose.
3. **A repository or agent session id is only recorded when there is a real delta** (`:398`, `:416`,
   inside the `if (deltaTurns > 0 || deltaChars > 0)` block) - unlike the wingman set. The two are
   deliberately different.
4. **A counter reset means the whole current count is fresh activity** (`:353-354`), not a negative
   delta.
5. **A delta with zero turns but non-zero characters still folds** (`:356`).
6. **`_agentsSinceUtc` is stamped once, from either entry point, and then never moves** (`:146-151`).
7. **An unrecognised agent token is shown verbatim; an empty one is `(unknown)`** (`:284-290`) -
   never dropped, never guessed.

## Order of work

Each step builds, keeps all seven test projects green, and goes to the Codex reviewer as a diff
before it is committed. Nothing is pushed and nothing is merged to `origin/main` (Constraints).

1. **Settle the three findings above** with the Architect and the Codex reviewer. No code first.
2. **Capture the before picture.** `GET /stats/data` from the live Gateway, saved to a file, plus
   screenshots of `/stats`, the cockpit Your Throttle, and the mobile Your Throttle. This is
   acceptance criteria 1 and 2 and it is unrecoverable once the store is touched - so it happens
   before anything else. A copy of the live `gateway-input-stats.json` becomes the test fixture.
3. **The database helper.** `Microsoft.Data.Sqlite` as an explicit `PackageReference` on
   `CcDirector.Gateway.csproj` (Decision 1 - not leaning on the transitive one through Core), a
   connection and migration helper with `PRAGMA user_version`, write-ahead logging, and the schema
   above. Modelled on `DatabaseService.cs:47-60`. Tests: fresh file, re-open, version stamp.
4. **The fold onto SQLite.** `GatewayInputStatsAggregator` rewritten with the high-water fold
   **semantically unchanged** (Decision 3 - a worker who simplifies it has broken the mission), the
   in-memory write-through mirror from Finding 3, and the aggregate reads as queries. The seven
   semantics above get their tests here, each watched failing first.
5. **The baseline import.** All eight `StoreFile` sections, one transaction, marker plus
   `PRAGMA user_version` stamped inside it, full field-by-field parity read back out of SQLite, the
   idempotent-re-observe leg from Finding 1, rename the JSON aside only on a complete match, and
   fail loudly on any mismatch with the JSON left as the source of truth. The refusal leg is proven
   by watching it fail on an induced mismatch, not asserted.
6. **Pruning and the archive.** Both legs of acceptance criterion 7: all-time answers identical
   across a prune, and no phantom bucket in the working-day series.
7. **The after picture, compared field by field** against step 2. Plus acceptance criterion 4: a new
   question answered by a query with no schema change. Candidate the owner has not asked for:
   "which surface do you drive each repository from?" - `GROUP BY repo_folded, surface` over
   `stat_delta`, which no dictionary in the current code can answer at all.
8. **The phase report**, written to
   `docs/architecture/gateway-sqlite-phase1-report-2026-07-15.md` - in a file, because the Manager is
   reset before Phase 2.

## Risks the Manager is holding

- **The import runs once against real, irreplaceable data.** The owner's 1401 turns and 568,580
  characters have no backup other than the renamed-aside file. The import is developed and proven
  against a **copy** of the live store as a fixture; the real cutover happens only after the fixture
  round-trips exactly.
- **Phase 1 does not prove Phase 2 or Phase 4.** Concurrency-peak restoration and the Phase 4 store
  choices are their own phases and must not be waved through on this phase's evidence (the brief is
  explicit, and round-two finding 9 approved that bound).
- **`GatewayInputStatsAggregator`'s constructor signature changes** from a JSON path to a database
  path. Three call sites move with it: `GatewayHost.cs:478`, `DirectorHubTests.cs:32`, and
  `GatewayInputStatsAggregatorTests.cs`. `StatsPageEndpointTests` also constructs one.

## Reviewer verdict and the guardrails it attached

The Codex reviewer reviewed this plan on 2026-07-15 and returned **APPROVED**, recorded in full at
`docs/architecture/gateway-sqlite-review-codex-phase1-plan-2026-07-15.md`. It confirmed Finding 1
("If `session_highwater` starts empty after importing baselines, the first post-cutover
`GET /sessions` can refold every live session's whole current tally on top of the baseline") and
Finding 2 ("The live store having no current case collisions does not make the design safe"), and
confirmed that importing all eight sections, the folded identity keys, and the explicit `is_voice`
column are **faithful elaborations of the brief, not deviations**. Finding 3 is not fallback
programming so long as SQLite stays the single durable source of truth.

Three guardrails came attached. They are binding on the implementation and are restated here so the
code is written against them rather than against a review file nobody re-reads:

- **Guardrail A (review finding 5) - the folding must be exact, not approximate.** Do **not**
  implement identity folding as `ToLowerInvariant`, `ToUpperInvariant`, SQLite `NOCASE`, or any
  SQL-side collation that is only approximately `StringComparer.OrdinalIgnoreCase`. It resolves
  through C# using `StringComparer.OrdinalIgnoreCase` itself, or an equivalently exact mechanism.
  Tests must include case-variant repository and agent keys and must prove the **first-seen** display
  spelling wins.
- **Guardrail B (review finding 7) - the statement-count test must not overfit to one bucket.** One
  `stat_delta` insert and one high-water upsert is the expected mix only when exactly one
  `(modality, surface)` bucket changes. A session with several changed buckets legitimately produces
  one delta row and one high-water upsert **per changed bucket**. The invariant to pin is therefore
  "bounded by changed buckets and distinct-id first sightings, **not** by stored history size" -
  which is the claim acceptance criterion 3 actually makes.
- **Guardrail C (review finding 9) - do not call the distinct-id sets bounded.** Per-fold *work* is
  bounded. The lifetime cardinality of the all-time distinct identifier sets is **not** - they are
  deliberately never pruned (`:45-61`) and the implementation must never treat them as prunable. The
  wording in Finding 3 above is corrected by this guardrail: the mirror is of operational state whose
  per-fold cost is bounded, not of state whose size is bounded.

Finding 4 (the moving target) was raised after this review was requested and is not covered by it.
It changes no design, only how criterion 1 is executed.

## Open questions - all four settled

1. **Finding 1** (import all eight sections including `HighWater`, plus the idempotent-re-observe
   parity leg) - **SETTLED.** Codex reviewer findings 1, 2, and 3: approved, and confirmed as a real
   hole the endpoint-field parity check cannot detect.
2. **Finding 2** (`repo_folded` / `agent_folded` plus identity tables, and the explicit `is_voice`
   column) - **SETTLED.** Codex reviewer findings 4, 5, and 6: approved as a faithful elaboration,
   subject to Guardrail A.
3. **Finding 3** (the in-memory write-through mirror) - **SETTLED.** Codex reviewer finding 8: not
   fallback programming, subject to Guardrail C's correction on the word "bounded".
4. **Finding 4** (the moving target, and criterion 1 executed against a stopped Gateway) -
   **SETTLED.** Architect accepted; criterion 1 now says so in the brief, exactly and with no
   tolerance: "If this criterion is ever reported as passing with a tolerance, it did not pass."

All four were accepted by the Architect with none disputed, and each is now in the brief rather than
only in this plan. The Architect measured Finding 1's cost on the live store and it was worse than
the Manager estimated: `HighWater` holds 842 turns across 115 sessions against all-time totals of
1404, so the first poll after a high-water-less import would have taken the owner's totals to 2246 -
a silent 60 per cent inflation of every number this mission promised to preserve.

**Decision 6 settled the one thing this plan deliberately refused to guess at.** The Architect's
first statement of the mirror rule said "high-water only", which would have made every idle poll
write one `INSERT OR IGNORE` per voice-mode session forever, because `:330` registers the wingman
session *before* the `:333` empty-bucket early return. The Manager put the tension up rather than
resolving it quietly, and the Architect adopted the recommendation verbatim: **the mirror holds
identity and membership - never a tally.** The test to apply to any future case is the one that
settled this one: *the count stays a query, so no aggregate is cached.* New acceptance criterion 3a
pins it - an idle poll writes nothing, including for a voice-mode session with no input, proven by
removing the mirror and watching the writes appear.

Phase 1 proceeds to code from here.
