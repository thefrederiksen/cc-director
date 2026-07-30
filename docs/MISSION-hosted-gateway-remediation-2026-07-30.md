# Mission: get the hosted Gateway fixed, proven, and released

**Status: ACTIVE.** Chartered 2026-07-30 by the owner. Architect: session 112. Manager: session 100.
Mission branch: `pure-roster-read`, worktree `D:\ReposFred\dt-pure-roster`.

How this mission conducts itself is in `.claude/skills/mission/SKILL.md`. That file is the only place
the rules live. This document describes the WORK. It grants nothing.

The incident that caused this mission is `docs/incidents/gateway-roster-500-2026-07-30.md` - the full
record, twelve numbered issues, evidence status on each.

---

## The why

On 2026-07-30 the hosted Gateway answered HTTP 500 to every client for 32 minutes. The Cockpit, the
phone, and every command line verb that resolves a session by name were blind for the duration.

The cause was not the build. The Gateway keeps its "Your Throttle" statistics in a **SQLite file on
an Azure Files network share**, and `GET /sessions` **writes** to that file on every read. A slot swap
ran two containers against the same share for 144 seconds, that corrupted the database's indexes, the
write threw, and the exception left the roster handler unhandled. Rolling back restored service and
fixed nothing: the corrupt file and the shared-writer hazard are both still live.

The owner's words, which are the point of the whole mission:

> "We're not in a rush to get the gateway up and running. We want it done correctly."

and, on the second charter:

> "You really relentlessly need to drive this to a release as soon as we have a better version of the
> gateway, especially when the SQLite is removed. We need to get it deployed as soon as possible. Then
> we can keep working on the smaller things after."

Those two are not in tension and it matters that nobody reads them as though they were. **Correct
first, then fast.** The thing being hurried is the RELEASE of the corrected Gateway, not the
correction. Do not ship a partial fix to be quick, and do not sit on a finished one to be thorough.

**What is true when this is finished:** the hosted Gateway opens no SQLite database, a statistics
fault degrades the statistics page and nothing else, a deploy cannot put two writers on the same
state, a rollback is one command that verifies a non-empty roster before declaring success, and the
statistics history the owner asked to keep is in Postgres and reconciled.

---

## Rulings already made. Do not reopen these.

Marked **[owner]** where he said it, **[architect]** where I decided it. Nothing here is inferred and
presented as his - where I extended his intent, it says so.

### From the owner

1. **Fix everything important, properly.** He is explicitly not in a rush to deploy something
   partial. **[owner]**
2. **Removing SQLite from the hosted Gateway is his single biggest concern.** It is not optional.
   **[owner]**
3. **Preserve the statistics history** into Postgres. He chose preservation over starting fresh.
   **[owner]**
4. **Do not revert the release.** The build was not at fault; the statistics code is byte-identical
   between the working and broken images. **[owner]**
5. **Drive to a release as soon as the Gateway is better, especially once SQLite is gone.** Smaller
   items continue after that release, not before it. **[owner]**

### From the Architect

6. **Step 1 before Step 2.** One day against one to two weeks, and Step 1 is what makes a defect in
   the new Postgres store survivable rather than fleet-fatal. **[architect]**
7. **Serialising the deploy goes FIRST, before Step 1 ships.** Deploying Step 1 itself opens a
   two-writer window before Step 1's own protection exists. It is a few lines of workflow
   configuration and it gates everything that ships. **[architect]**
8. **Repairing the corrupt file is no longer blocking.** Once Step 1 lands, a corrupt store degrades
   the statistics page instead of the fleet; once Step 2 lands, the hosted Gateway never opens the
   file at all. This deletes the riskiest operation in the incident document - repairing a
   write-ahead-log database over a network share during a planned outage. **[architect]**
9. **A separate `GatewayStatsDbContext`**, with its own schema, migration history and connection
   pool - not folded into `GatewayDbContext`. Folding them couples statistics schema churn to the
   deploy gate and shares a connection pool with the roster, which is the Step 1 coupling moved one
   layer down where the containment cannot see it. Same physical Supabase server is fine.
   **[architect]**
10. **Entity Framework for the model, migrations and read projections; explicit `ON CONFLICT DO
    UPDATE` upserts for every high-water and per-hour-maximum write.** Change-tracked
    read-modify-write on those paths is a lost-update generator under concurrent Postgres that
    single-writer SQLite never exposed. **[architect]**
11. **Statistics observation happens at the push ingress, behind a bounded single-consumer queue.**
    The hub enqueues and never blocks. Drops are counted, never silent. **[architect]**
12. **Session-number `Adopt` stays INLINE, off the queue.** It is pure in-memory work
    (`ConcurrentDictionary`, per-tenant locks, no store), so it can neither hang nor throw a storage
    exception; queuing it would trade a real duplicate-session-number regression against issue 1292
    for protection from a failure mode that cannot occur. The general rule: **queue only what touches
    a store.** **[architect]**
13. **`gateway-concurrency-stats.json` is in Step 2's scope**, not Step 3's audit. It is written from
    the hottest path in the system and is corruptible by the same two-writer window.  **[architect]**

---

## The work, in the order it lands

Each step is one or more pull requests, sliced small, each landing on `origin/main` before the next
begins where the dependency requires it.

| Step | What | Gate |
|---|---|---|
| **0** | **Serialise deploy, rollback and cleanup behind one concurrency group.** Issue 3. A few lines of workflow configuration. Nothing else ships until this is on `main`. | Release gate |
| **1** | **Contain the blast radius.** `GET /sessions` becomes a genuinely pure read - including moving `PruneNotLive` off it. Statistics observed at the ingress behind the bounded queue. Statistics failures degrade the statistics surface only, with per-observer failure, drop, last-error and last-successful-write counters surfaced. | Release gate |
| **2** | **Remove SQLite from the hosted Gateway.** The statistics store moves behind `GatewayStatsDbContext`. Ends with a guard that fails loud if anything on the hosted path opens a SQLite connection - and the guard is proven by making it trip. Includes `gateway-concurrency-stats.json`. | Release gate |
| **R** | **RELEASE.** Cut and deploy the corrected hosted Gateway. This is the point of the mission. | - |
| **4** | **Deploy and rollback safety.** One-command rollback: preflight the recorded commit, reverse-swap, assert production reports it, then poll until three consecutive 200s each carry at least one session. Issue 8's non-empty assertion is part of this, not after it. | Release gate for the NEXT release |
| **3** | **The rest of the shared file state.** `missions.json`, `repo-history.jsonl`, `diagnostics-results.json`, `netdiag-rollup.json`, the state directories. Audit first - some may legitimately be per-instance cache. | After |
| **5** | **Remainder.** Archive the corrupt file, the offline data rescue and reconciliation, make a stale roster stop asserting confident verdicts (issue 9 - the thing the owner actually saw), answer the alerting question (issue 10), the runbook note on stale Kudu metadata (issue 11). | After |

**The release gate is Steps 0, 1 and 2.** Step 4 was in the original gate because the deploy mechanism
caused the outage; Step 0 now carries that job for this release, and Step 4 gates the next one. That
is a deliberate narrowing to get the SQLite removal deployed, and it is the only place this mission
trades thoroughness for speed - recorded here so it is a decision and not a drift.

**The data rescue is Step 5, deliberately.** Once Step 2 lands, the hosted Gateway never opens the
file, so the rescue becomes an offline job with no live service near it and no outage window.

---

## How Step 2 is proven

A provider-parametrised contract suite - one set of assertions, run twice, once against SQLite and
once against a **real** Postgres, not an in-memory provider. That is the backbone and it is not
sufficient on its own. It must cover:

1. Every one of the 16 tables, both write path and read projection.
2. **Interleaved writers on the high-water paths**, asserting no lost update. This is the assertion
   SQLite passed trivially and Postgres genuinely needs.
3. Idempotency - replaying one snapshot ten times equals replaying it once.
4. Tenant partitioning on the canonicalised identifier, on every table.
5. Boundaries - hour and day rollover in UTC, clock skew backwards, out-of-order timestamps, integer
   limits, decreasing counters, null and absent fields, non-ASCII text.
6. **Output parity** - one fixture through both stores must render identical `/stats/data` bodies.
   Storing the same rows and rendering the same page are two different claims.
7. **The suite proven to detect** - break the Postgres implementation on purpose, watch it go red.
8. The no-SQLite guard proven by making it trip on demand.

---

## The data preservation method

The rule for the whole operation: **nothing writes to the Azure Files share at any point.**

1. Copy `gateway-stats.db` and its `-wal` and `-shm` companions off the share. All three - a database
   whose write-ahead log is discarded is missing its most recent writes.
2. Verify the copy reproduces the same four index faults recorded in the incident. If it reports
   something different, the copy is not the thing that broke.
3. `REINDEX` the **copy**, on local disk. The file on the share is never written.
4. Prove the repair lost nothing: per-table row counts unchanged, `integrity_check` clean. If a count
   moves, the damage was not index-only and "preserve" needs revisiting before any backfill.
5. Backfill with an idempotent throwaway script - not shipped migration code - keyed so a re-run
   cannot double count.
6. **The reconciliation is the test that could fail:** per-tenant, per-table AGGREGATE VALUES from the
   repaired copy must equal those from Postgres after the backfill. Not row counts - the numbers the
   statistics page renders. Record them in the incident document.
7. Archive both files off the share; only then remove the live one.

---

## Out of scope - do not invent these

- **Warm standby / retaining the previous container.** Issue 7. It is the natural reaction to this
  outage and it would make things worse: production and staging share one file system, so a warm
  second container is a *permanent* second writer. Explicitly sequenced after Step 3 says the shared
  writers are gone.
- **Reverting the release.** Ruling 4.
- **Repairing `gateway-stats.db` in place, or from Kudu while the Gateway serves.** That is a second
  concurrent writer over SMB - the exact mechanism that caused the incident.
- **Touching the live hosted Gateway outside a deliberate release.** No restarts, no ad-hoc redeploys.
- **Any change to `GatewayDatabase`.** It branches correctly today and needs none. Proven in the
  incident document.
- **Colour and verdict logic.** The red dot was a downstream symptom of the roster read failing. No
  colour logic is at fault. Issue 9 is a stale-data presentation fix in Step 5, not a colour fix.

---

## Open questions for the owner

None outstanding. Anything genuinely undecidable goes to him one question at a time, with the context
needed to answer it, per law 1.
