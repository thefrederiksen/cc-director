# Hosted Gateway outage, 2026-07-30: the session roster returned 500 for 32 minutes

Working document. Every issue below carries its evidence status, because some of this is proven
first-hand and some is still a hypothesis, and the two must not be mixed when deciding what to fix.

- **PROVEN** - observed first-hand against the live system, with the observation named.
- **CONFIRMED** - established by a second party against the live system or the code.
- **HYPOTHESIS** - consistent with the evidence, not yet demonstrated.
- **OPEN** - not investigated; recorded so it is not lost.

## What happened, in one paragraph

A slot swap at 12:55:13 UTC put a new Gateway image into production. Roughly ninety seconds later
`GET /sessions` began returning HTTP 500 and kept doing so for 32 minutes, across the Cockpit, the
phone and the command line tools. The cause was not the new build. The Gateway keeps its "Your
Throttle" statistics in a **SQLite file on an Azure Files network share**, and `GET /sessions`
**writes** to that file on every read. The swap ran two containers against the same share at once,
which corrupted the database's indexes, and the resulting `SqliteException` propagated out of the
roster handler unhandled. Rolling back to the previous image restored service at 13:28. The corrupt
file is still on disk.

The visible symptom was a wrong colour: a session that was demonstrably working showed red. That was
a downstream effect - the roster read was failing, so the Cockpit kept rendering the last roster it
had successfully fetched. No colour logic is at fault.

## The exception

```
[GatewayHost] unhandled exception: GET /sessions:
Microsoft.Data.Sqlite.SqliteException (0x80004005): SQLite Error 11: 'database disk image is malformed'.
  at GatewayInputStatsAggregator.Execute         (GatewayInputStatsAggregator.cs:1310)
  at GatewayInputStatsAggregator.CommitLocked    (GatewayInputStatsAggregator.cs:717)
  at GatewayInputStatsAggregator.ObserveSnapshot (GatewayInputStatsAggregator.cs:335)
  at GatewayEndpoints.<Map>b__26                 (GatewayEndpoints.cs:1254)
```

500 occurrences, first at 12:56:36 UTC, in
`/home/gateway/cc-director/logs/director/director-2026-07-30-c807c0eff659.log`.

## Timeline (UTC)

| Time | Event |
|---|---|
| 12:53:50 | a container starts (the swap's warm-up) |
| 12:54:42 | a second container starts - both now mount the same share |
| 12:55:13 | slot swap completes; the new image is production |
| 12:56:14 | the first container stops. The two overlapped for 144 seconds |
| 12:56:36 | first `database disk image is malformed` - 22 seconds after the overlap ended |
| 12:57:50 | the owner sees a working session painted red and starts asking |
| 13:03:55 | the 500 is reproduced from the command line |
| 13:27:19 | staging started so it can be swapped back |
| 13:28:26 | reverse swap complete - 67 seconds for the swap itself |
| 13:29:30 | roster serving again, 16 sessions, colours correct |
| 13:52 | staging stopped - it was a second live writer on the shared state |

Total outage: about 32 minutes. Detection was a human noticing a wrong colour on screen.

## Not the cause - do not chase these

- **The new build is not at fault.** The statistics code is byte-identical between the working and
  the broken image: `git diff <good>..<bad> -- src/CcDirector.Gateway/Stats/` is empty. The damaged
  tables and indexes predate both. **CONFIRMED.** A revert of the release fixes nothing and the
  release can go back out once the issues below are addressed.
- **Not a pending database migration.** The live Postgres has `20260729173140_SkillPlacementState`
  applied, which is the newest migration in the tree at the deployed commit. **PROVEN** by querying
  the database directly.
- **Not a sticky slot setting.** Every application setting has `slotSetting=false`, so the swap did
  not hand production a staging value. **PROVEN.**
- **Not the main Gateway database.** `GatewayDatabase` branches correctly: Postgres when
  `CC_GATEWAY_DB_CONNECTION` is set, SQLite otherwise, with no fallback between them. Hosted runs on
  Supabase. This design is right and needs no change. **PROVEN.**
- **Not `uncommittedCount`.** During the incident, sessions with uncommitted changes failed and
  sessions without them served fine, which looked like a clean discriminator. It was a coincidence:
  busier sessions write more token rows, and the throw fires when a write touches a damaged index
  page. Recorded because it wasted investigation time and would waste it again.

---

# The issues

## Issue 1 - `GET /sessions` performs an analytics write, and lets it fail the read

**Status: PROVEN.** The stack trace above names the line.

`GatewayEndpoints.cs:1254` calls `inputStats?.ObserveSnapshot(all, ...)` inside the `GET /sessions`
handler. Two more side-effects sit beside it on the same read path: `concurrency?.Observe(all, ...)`
and `sessionNumbers.Adopt(...)`. None is guarded. A failure in any of them propagates out of the
handler as an unhandled exception, so the response is 500 and the caller gets no roster at all.

**Why this is the most important issue.** It is what converted a damaged auxiliary file into a total
outage. A corrupt statistics store should degrade the statistics page. Instead it blacked out the
primary read path on every surface - Cockpit, phone, and every command line verb that resolves a
session by name. It also took `/stats/data` down with it, so the two failures looked like two
separate defects for a while.

It also broke the tools' ability to say anything true: `cc-devthrottle session list` reported
"Cannot reach the Gateway: internal error", which is the honest message, but any verb that resolves
a target against the roster could name no session at all.

**Fix.** Make `GET /sessions` a pure read. Move the statistics observation to the push ingress -
observe only accepted `PushSnapshot` / `PushDelta` traffic, in a contained single-consumer worker -
and circuit-break statistics alone, answering 503 on that surface while the roster and the tunnels
keep serving.

## Issue 2 - the hosted Gateway runs a SQLite database on a network share, with WAL

**Status: PROVEN** for the code and the file; **CONFIRMED** for the shared mount.

`GatewayStatsDatabase` opens `Microsoft.Data.Sqlite` on a raw `SqliteConnection` with no Entity
Framework and, critically, **no hosted branch**. It creates a file unconditionally on hosted and
self-host alike. On the hosted Gateway that file is
`/home/gateway/cc-director/gateway-stats.db` - 2.8 MB, with a 4 MB write-ahead log that grows while
the service runs.

Three specifics make it worse than "SQLite in the wrong place":

1. It forces `PRAGMA journal_mode=WAL` at line 98. SQLite's documented constraint is that WAL relies
   on shared memory and does not work on a network filesystem. Azure Files is one.
2. The file's own header comment states the justification: *"the Gateway is a single process and
   therefore a single writer, so there is no cross-process locking."* On App Service that premise is
   false - slots mean two containers, and a swap runs both at once.
3. The multi-tenancy work already reached this file. A comment at line 524 discusses *"a hosted
   Gateway that folds several accounts' pushes into this one store."* So the store was known to run
   hosted and was partitioned by tenant, but was never moved to Postgres.

**Fix.** Move this store to Supabase/Postgres, the way `GatewayDatabase` already does. There should
be no SQLite on the hosted Gateway.

## Issue 3 - a deploy puts two writers on the same shared persistent state

**Status: PROVEN.**

Production and staging mount the **same** Azure Files share at `/home/gateway/cc-director`. I
verified this independently rather than taking it on report: after the reverse swap, the share held
two logs growing at once - `director-2026-07-30-7c0c7ccebf3b.log` written by a version 1.8.8
container and `director-2026-07-30-a24cb70b1cc7.log` written by a version 1.8.4 container. Two
different builds, one share, simultaneously.

So every deploy opens a window in which two processes write the same files. This one lasted 144
seconds. **Every previous deploy has been rolling the same dice.** This is the first time it lost,
which is why nothing looked wrong until now.

This is also why the obvious resilience idea has to be inverted - see Issue 7.

**Fix.** Some combination of: serialise deploy, rollback and cleanup behind a single concurrency
group so they can never overlap; give each slot its own state; or move the state off the share
entirely, which Issue 2 does for the database and Issue 4 for the rest.

## Issue 4 - the rest of the hosted state is file-backed on that same shared share

**Status: PROVEN** that the files exist and are actively written; **HYPOTHESIS** that they are
corruptible the same way.

Listing the hosted share turns up substantial persistent state that is not in Supabase:

```
gateway-concurrency-stats.json   53 KB, written seconds before I looked
missions.json
repo-history.jsonl  (+ .jsonl.bak)
carmode-diagnostics.json
diagnostics-results.json
netdiag-rollup.json
keyvault.json
plus directories: tenants/, gateway-turnbriefs/, transcripts/, transcription-history/,
                  prompt-log/, dictation/, microphone-quality/, wingman-training/
```

A JSON file rewritten by two containers at once is corrupted or truncated exactly as readily as a
SQLite database. It simply fails later, with a parse error instead of "disk image is malformed".

So removing SQLite fixes the specific thing that took us down today, but the general defect is
broader: **hosted persistent state lives on a shared network share that two processes can write
concurrently, and the code was written assuming one process.**

**Fix.** Audit this list. Move what is real state into Postgres; for anything that stays on disk,
make writes atomic and single-owner.

## Issue 5 - the corrupt database is still in production, and cannot be repaired safely while serving

**Status: PROVEN.** `pragma integrity_check` against the live file, after the rollback:

```
row 143 missing from index sqlite_autoindex_token_highwater_1
rows 6850-6853 missing from index ix_token_delta_tenant_hour
wrong # of entries in index ix_token_delta_tenant_hour
row 323 missing from index sqlite_autoindex_wingman_session_1
```

The damage is index-only, so `REINDEX` should repair it with no data loss. Two cautions:

- It is quiet, not fixed. The rolled-back build runs the same statistics code; it is simply not
  touching the damaged pages right now. The roster and `/stats/data` have served 200 on every probe
  since 13:29, but the landmine is live.
- **Do not repair it from the Kudu container while the Gateway is serving.** That would be a second
  process writing a WAL database over SMB - the exact mechanism that caused the damage. The repair
  needs the Gateway stopped, which means a planned outage, or it needs to be done from inside the
  Gateway.

**Fix.** Repair during the next deploy window, when the Gateway is down anyway. Or delete and let it
rebuild, accepting the loss of historical statistics, if that is cheaper than a careful repair.

## Issue 6 - there was no rollback runbook, so rollback took four minutes instead of 67 seconds

**Status: PROVEN** by doing it.

The mechanism was already there and it worked: the previous container lands in the staging slot, and
the reverse swap took **67 seconds**. What cost the other three minutes was that **staging was
stopped**, so it had to be started and pass a health check first - and that was discovered by reading
documentation while the fleet was down.

**Fix.** One command or button that: preflights the recorded commit of the image it is about to
promote, performs the reverse swap, asserts production reports that commit, verifies the roster (see
Issue 8), and only then stops the bad image. Keep the existing workflow as the single entry point
rather than adding a parallel path.

## Issue 7 - warm standby is unsafe today, and the instinct to add it must be resisted

**Status: CONFIRMED.**

The natural reaction to this outage is to keep the previous container running in staging for a few
hours so a rollback is instant. **That would make things worse.** Because production and staging share
one file system (Issue 3), a warm second container is a *permanent* second writer - the corruption
mechanism running continuously instead of for 144 seconds per deploy.

**Fix.** Sequence it. No warm retention until every shared writer is isolated or moved off Azure
Files. After that, retain the prior image for two hours, which is the right idea in the right order.

## Issue 8 - the post-swap health check would have reported success on an empty fleet

**Status: PROVEN.** The first `GET /sessions` after the reverse swap returned **200 with zero rows**,
because the Directors' tunnels had not reconnected yet. It filled in about a minute.

Any verification step that checks for HTTP 200 would have declared the rollback a success while the
roster was empty. Absent reads identical to empty - the same failure shape the roster completeness
work was built to fix, one layer up.

**Fix.** Verification must poll authenticated `/sessions?envelope=true` until **three consecutive 200
responses each contain at least one session**, and must treat zero rows as "still reconnecting", not
as success.

## Issue 9 - a stale roster renders last-known verdicts as though they were current

**Status: PROVEN** by the owner's screenshot.

The Cockpit behaved half-correctly. It did show a banner - "Roster stale - showing last-known
sessions" - which is honest and was the first real clue. But it also kept painting a definite red dot
and a "Needs you" label on a session that was working, because those were the last verdicts it had.
A confident colour on stale data reads as a fact about the world.

The practical cost is exactly what happened here: the first thirty minutes went into "why is the
colour wrong", when the colour was a symptom and the answer was one layer down.

**Fix.** A design decision, not a bug fix: when the roster is stale, the rows should visibly stop
asserting. De-emphasise or neutralise the dot and the label rather than re-rendering a verdict whose
inputs are known to be old.

## Issue 10 - whether anything alerted, and how long it would have taken

**Status: OPEN.** Not investigated.

Detection was the owner looking at a screen roughly three minutes in. Whether an alert also fired,
and if not why, is unverified - I have not read the alerting configuration, and I am not going to
assert either way. Worth answering, because 32 minutes of 500s on the primary read path should not
depend on somebody happening to look.

## Issue 11 - operational trap: the Kudu container serves stale file metadata for the share

**Status: PROVEN**, at the cost of a wrong finding.

`ls`, `stat` and even file reads issued through the Kudu API return **cached** views of the Azure
Files share. During the incident this showed a log file as 0 bytes when it was actually being written;
I concluded the Gateway had stopped logging entirely and reported that as a second defect. It was
false. The file turned out to be 2.2 MB and contained the exception all along.

**Fix.** Record it in the runbook: a size or timestamp read through Kudu is not evidence about the
current state of the share. Re-read before concluding anything from an absence.

---

# Priority

The organising question is: **what must be true before the next Gateway deploy goes out?**

## Before the next deploy - blocking

| # | Issue | Why it blocks |
|---|---|---|
| 1 | `GET /sessions` must not fail on an analytics write | Cheapest fix with by far the largest blast-radius reduction. Until this lands, any damage to the statistics store is a full outage rather than a degraded page. It also makes the remaining work safe to do incrementally. |
| 3 | Serialise deploy, rollback and cleanup | The deploy itself is what creates the two-writer window. Deploying again without this is re-running the experiment that caused the outage. |
| 5 | Repair `gateway-stats.db` | Do it in the deploy window while the Gateway is down. It cannot be done safely while serving, and the next deploy is the natural opportunity. |
| 6 | A rollback command, and staging left in a known state | If the next deploy fails too, four minutes of reading documentation is not an acceptable recovery path. |

## Immediately after - high

| # | Issue | Note |
|---|---|---|
| 2 | Move the statistics store to Supabase | The real fix for "no SQLite on the hosted Gateway". Larger than the blocking set, which is why Issue 1 goes first: it makes this non-urgent instead of load-bearing. |
| 8 | Roster verification must assert non-empty | Small, and it protects every future rollback from a false green. |
| 4 | Audit the remaining file-backed hosted state | Scoping work. Determines whether Issue 7 can ever be enabled. |

## Then - medium

| # | Issue | Note |
|---|---|---|
| 7 | Warm standby, two-hour retention | Only once Issue 4 says the shared writers are gone. Explicitly sequenced after, not before. |
| 9 | Stale rosters should stop asserting verdicts | This is the defect the owner actually saw. Worth fixing properly rather than as an afterthought. |
| 10 | Establish whether anything alerted | Answer the question before deciding whether there is work here. |
| 11 | Runbook note on stale Kudu metadata | Documentation only. |

## A note on what not to do

Do not revert the release. The build is not at fault, and reverting would spend a deploy - and
another two-writer window - achieving nothing.
