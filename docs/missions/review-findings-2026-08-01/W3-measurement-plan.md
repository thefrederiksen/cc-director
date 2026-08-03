# W3 - the measurement plan, written BEFORE the rig runs

Work item W3 (the roster fold and the display-sweep overlap guard) exits on a measured number against the
31 July baseline at `devthrottle_internal/docs/load-test/runs/2026-07-31-local-baseline.md`, not on an
assertion. This document fixes, in advance: what will be run, what configuration will be recorded beside
the numbers, what the numbers are predicted to be, what would make a run unusable, and - the part that is
easiest to gloss over later - **which claim each stage can and cannot support.**

It is written now, while the machine belongs to work item W2, so that comparability is a decision taken in
daylight rather than a set of choices made at midnight in front of a running rig. Predictions written
before a run cannot be quietly adjusted to whatever the run produced.

---

## 1. Why the proof is a COUNT

The baseline's most valuable Stage 0 figure is not a latency. It is an exact count with no remainder:

> **1,032 snooze database reads for 30 roster polls plus 13 sweeps over 8 sessions = (30 + 13) x 8 x 3.**

A count means the same thing on a loaded machine as on an idle one. A latency does not. So the proof that
the fix works is a count, it needs no quiet machine and no k6, and it can be taken as soon as the machine
is free. The latency comparison is a separate and weaker claim - how much the fix is worth - and it needs
conditions this machine may not be able to give.

Note the direction of the contamination, because it decides how much care each threat deserves:

- **Machine noise makes the new run look WORSE.** It is honest to label and it shows up in the numbers.
- **Configuration drift makes the new run look BETTER, and cannot be seen afterwards** by anyone reading a
  before-and-after pair. That is why section 3 is a table and not a sentence, and why `run-stage0.ps1`
  now REFUSES to run until the configuration facts it cannot see from inside are stated on the command
  line and written into the artifact.

---

## 2. What each stage can prove, and what it cannot

| Claim | Proved by | NOT proved by |
|---|---|---|
| The fold takes one snooze read instead of three per session | **Stage 0** (an exact count) and the unit tests | - |
| The display sweep no longer overlaps itself | **Stage 1** (the baseline's 91 of 98 overlapping ticks) and the unit tests | **Stage 0** - see below |
| The ceiling has moved, and which resource gives first now | **Stage 1** | Stage 0 |
| How much faster the roster is | Stage 1, and only as a FLOOR if the machine is busy | Stage 0 |

**Stage 0 cannot prove the overlap guard, and it must not be presented as if it does.** The baseline's own
Stage 0 recorded `sweepOverlaps: 0` - with no guard at all - because one Director, eight sessions and one
viewer never produce a sweep slow enough to be overtaken. A zero after the fix is therefore identical to
the zero before it, and identical readings prove nothing. The overlap number only becomes evidence under
Stage 1's load, where the baseline measured 91 overlaps in 98 ticks with up to 36 passes in flight.

Expect `sweepSkipped: 0` at Stage 0 too, for the same reason: nothing overlaps, so nothing is skipped.

---

## 3. The configuration, decided now

Every row is either matched to the baseline or is a stated deviation. `run-stage0.ps1` writes the last
five into the artifact's provenance block and refuses to run without them.

| Item | 31 July baseline | This run | Match? |
|---|---|---|---|
| Rig | `LoadRig` booting the real `GatewayHost`, `CC_GATEWAY_HOSTED=1`, throwaway Postgres 16 in Docker (`dt-loadtest-pg`, 127.0.0.1:55442) | same | yes |
| Build configuration | **Debug** (stated in the baseline's own honesty notes) | **Debug** | yes |
| Log console mirror | **OFF** (`LOADTEST_MIRROR_CONSOLE` unset) | **OFF** | yes |
| Directors connected (Stage 0) | 1 | 1 | yes |
| Sessions per Director | 8 | 8 | yes |
| Viewers (Stage 0) | 1, polling `GET /sessions` every 2 s | same | yes |
| Polls (Stage 0) | 30 | 30 | yes |
| Tenants SEEDED in the rig | **not recorded for Stage 0** - see below | 1 | **stated deviation, and it changes nothing** |
| Machine | SOREN_NORTH (i7-13700, 16 cores / 24 logical, 64 GB) | same | yes |
| Machine state | "an otherwise idle fresh rig" | recorded, before and after | **stated: this machine has been the mission's binding constraint all day** |

**The seeded-tenant row, explained rather than waved at.** The committed `rig-provenance.json` records 20
tenants, but it was written at 15:50:05 UTC and the Stage 0 artifact was captured at 15:46:24 UTC - so it
describes a LATER rig boot, and the tenant count of the rig Stage 0 actually ran on is not recorded
anywhere. It does not matter, and the reason is mechanical rather than a judgement call: the display
sweep folds `PushedSessionStore.KnownTenants`, which is the set of tenants with a Director bound to the
tunnel, and the roster serves only the caller's own tenant. With ONE Director connected, exactly one
tenant is folded per sweep whether the rig seeded one tenant or twenty. The baseline's own numbers confirm
it - its fold count was 43, which is 30 polls plus 13 sweeps at one fold each. A rig seeded with one
tenant is therefore the same measurement with a smaller footprint on a machine that is short of room, and
it removes an ambiguity as a bonus: with one tenant the viewer key and the Director key cannot belong to
different tenants.

**A discrepancy worth knowing about:** `tools/loadtest/README.md` tells the operator to build `LoadRig`
with `-c Release`, while the baseline states it measured a Debug build. Whoever ran the baseline did not
follow that line. This run matches the BASELINE (Debug), because comparability beats the instruction, and
the README now says the choice must match whichever baseline a run is being compared against.

### What the baseline does NOT record, which bounds how precise any comparison can be

This is a limit on the comparison itself, not a caveat about this run, and it will bound the next
comparison too unless it is said out loud:

- **The tenant count of the rig Stage 0 actually ran on is not recorded anywhere.** The committed
  `rig-provenance.json` says 20 tenants, but it was written at 15:50:05 UTC and the Stage 0 artifact was
  captured at 15:46:24 UTC - three and a half minutes EARLIER - so it describes a later rig boot. It is
  reconstructable to the extent that matters (the fold count of 43 proves exactly one tenant was folded),
  but the seeded count itself is gone.
- **The k6 version that produced the baseline's client-side percentiles is not recorded.** This run uses
  **k6 2.1.0**, recorded here. Stage 1's client-side p50 and p95 therefore compare across an unknown
  version gap; the server-side figures from `/diag/loadmetrics` do not, and are the ones to lean on.

So a before-and-after pair from these two runs is honest about direction and about counts, and it should
not be presented as though the baseline was fully characterised. It was not, and saying so is what keeps
the next reader from over-reading a precise-looking number.

---

## 4. Stage 0 - the exact invocation

Run in this order, in three shells. Nothing here is improvised on the night.

```powershell
# 1. Throwaway database.
powershell -NoProfile -File tools/loadtest/scripts/start-postgres.ps1

# 2. The rig, DEBUG to match the baseline, console mirror left OFF.
dotnet build tools/loadtest/LoadRig/LoadRig.csproj -c Debug
$env:CC_GATEWAY_DB_CONNECTION = "Host=127.0.0.1;Port=55442;Database=gateway_loadtest;Username=loadtest;Password=loadtest"
$env:LOADTEST_TENANTS = "1"; $env:LOADTEST_DIRECTORS_PER_TENANT = "1"
$env:LOADTEST_OUT_DIR = "<scratchpad>\loadtest-out"
Remove-Item Env:LOADTEST_MIRROR_CONSOLE -ErrorAction SilentlyContinue
tools\loadtest\LoadRig\bin\Debug\net10.0\LoadRig.exe
# wait for: RIG READY url=http://127.0.0.1:7891

# 3. (second shell) One Director, eight sessions, silent.
dotnet build tools/loadtest/DirectorSim/DirectorSim.csproj -c Debug
$env:GATEWAY_URL = "http://127.0.0.1:7891"
$env:KEYS_FILE = "<scratchpad>\loadtest-out\directors.json"
$env:DIRECTORS = "1"; $env:SESSIONS_PER_DIRECTOR = "8"; $env:EVENTS_PER_SEC = "0"
tools\loadtest\DirectorSim\bin\Debug\net10.0\DirectorSim.exe

# 4. (third shell) Stage 0 itself - about a minute.
powershell -NoProfile -File tools/loadtest/scripts/run-stage0.ps1 `
    -GatewayUrl http://127.0.0.1:7891 -OutDir "<scratchpad>\loadtest-out" `
    -BuildConfiguration Debug -ConsoleMirror off `
    -Tenants 1 -DirectorsConnected 1 -SessionsPerDirector 8 `
    -Label "W3 after the batched fold read (roster-fold-batch)"

# 5. TEARDOWN, not optional - it removes the database and every synthetic tenant.
powershell -NoProfile -File tools/loadtest/scripts/stop-postgres.ps1
```

Before step 4, confirm from the rig's own output that the Director connected and that a roster poll
returns eight sessions. A viewer that sees zero sessions folds nothing, takes no read, and would produce a
beautiful zero that means the rig was mis-wired - the exact shape of a clean result that answers a
different question.

---

## 5. The prediction, written before the run

Let `p` be the roster polls (30) and `w` the sweep ticks the run happens to catch (the baseline caught 13
in its minute; this run will catch whatever it catches - it is a function of duration, not of the fix).

| Figure | Baseline | Predicted after the fix | Why |
|---|---|---|---|
| `counters.snoozeDbReads` | 1,032 | **`p + w`** (about 43) | one set-based read per fold |
| `foldDurationMs.count` | 43 | `p + w`, unchanged | the fix removes reads, not folds |
| **`snoozeDbReads / foldDurationMs.count`** | 24 | **exactly 1** | the identity that decides it |
| `snoozeDbReads / rosterRequests` | 34.4 | about 1.43 | `(p + w) / p` |
| `snoozeLockWaitMs.count` | 1,032 | `p + w` | one gate acquisition per fold |
| `counters.sweepOverlaps` | 0 | 0 | proves nothing at this stage - see section 2 |
| `counters.sweepSkipped` | (did not exist) | 0 | nothing overlaps, so nothing is skipped |
| Roster latency | p50 30 ms / p95 64 ms client, 33 ms mean server | no strong prediction | an idle floor is not where this fix pays |

**The identity is the verdict: `snoozeDbReads` equals `foldDurationMs.count`, exactly, with no remainder.**
A ratio of 3 means nothing changed. A ratio near 2 means only one of the two loops was fixed - the failure
this work item was specifically warned about, and the one that would read like success. Anything that is
not a whole small number means something is folding that I have not accounted for, and the run needs
explaining before it is reported.

The one legitimate way the identity can come out BELOW one is a fold over zero sessions, which takes no
read at all. That cannot happen in this configuration (one tenant, one bound Director, eight sessions),
and if it appears, the rig is not wired the way this document says it is.

---

## 5a. The gate, REWRITTEN after main split the test suites (a084c422c)

**The earlier version of this section is void, and so is the count comparison it rested on.** On 2 August
main split `CcDirector.Gateway.Tests` in two: about 2,750 pure tests moved to a new
`CcDirector.Gateway.UnitTests` that takes no machine-wide lock and runs in the default gate, and the
host-bound remainder stayed behind, PARKED along with `CcDirector.Core.Tests`. The default
`scripts\test-local.ps1` no longer runs either parked suite.

**Both of this work item's test files landed in the PARKED suite by default, which means the default gate
would have reported green without executing either of them.** That is this mission's own false-green shape,
arriving as the default behaviour rather than as an accident. So:

- **Every report from here states WHICH invocation was run.** `-Parked` (or `-Gateway`) or it proves
  nothing about this work.
- **A count comparison against `test-baseline.md` is no longer meaningful for the two split projects** -
  their totals moved by ~2,750 for reasons that have nothing to do with this branch. Comparing to those
  numbers would produce a frightening-looking fall that means nothing, which is worse than no comparison.
  The unchanged projects (Avalonia 353, Engine 63, HostedAgent 88, Launcher 110, Terminal 24) still compare
  and must still hold.
- **What replaces it, and it is stronger: the named facts must appear in the TRX as EXECUTED and passed.**
  A count catches a collapsed run; naming the facts catches a hollow one, and after a layout change it is
  the only claim that means what it says. Thirteen facts, by name:
  five in `CcDirector.Gateway.UnitTests/RosterFoldBatchedSnoozeReadTests`, four in
  `CcDirector.Gateway.Tests/RosterFoldSnoozeReadCountTests`, four in
  `CcDirector.Gateway.Tests/DisplayStateSweepOverlapGuardTests`.

### Where the tests were placed, and why - decided rather than defaulted

The split's rule is host-bound parked, fast unit tests not, with the locked project as the default home so
anything unclassified is serialized by construction.

| File | Placement | Why |
|---|---|---|
| `RosterFoldBatchedSnoozeReadTests` (5 correctness facts) | **fast suite, runs on EVERY gate** | Constructs no host, binds no port, needs only its own throwaway SQLite file. A regression guard that runs on every gate is worth more than one that runs before a release. |
| `RosterFoldSnoozeReadCountTests` (4 counter facts) | **parked** | Every fact asserts a DELTA on `LoadTestMetrics.snoozeDbReads`, which is process-global. The fast assembly runs four collections at once and holds a dozen fold tests that increment that same counter, so an exact delta there would silently absorb another test's reads - a flaky assertion on this item's headline proof. The locked suite disables parallelism, which is what makes the delta mean anything. Same rule the split applied to the 34 classes that mutate process-wide environment variables. |
| `DisplayStateSweepOverlapGuardTests` (4 facts) | **parked** | Boots a real `GatewayHost` and binds a port - host-bound by the split's own definition - and also asserts process-global sweep counters. |

The cost is stated rather than hidden: the read-COUNT proof runs on the release gate, not on every gate.
Two things reduce that cost. The correctness facts it depends on - that the batched read answers exactly
what the three per-session reads answered - run every time. And the count has a second, independent
instrument in the load test's Stage 0, which measured 42 reads for 42 folds against the baseline's 1,032.

## 6. What would make a run unusable

- The roster returns zero sessions, or the fold count is not `p + w`. The rig is mis-wired; fix and re-run.
- The rig was reused from an earlier stage. Every stage gets a fresh container - the baseline was itself
  bitten by this and had to re-run its Stage 2.
- A build or a Gateway suite was running on this machine during the run and was not recorded. Counts
  survive that; latencies do not, and the record must say so either way.
- The configuration facts were not passed to the script. It refuses, by design.

---

## 7. Stage 1, when the machine can give it

Prerequisite now met: **k6 2.1.0 is installed** (`winget install --id GrafanaLabs.k6` - the id previously
written in the README, `k6.k6`, matches nothing, which is why this looked like a ten-minute job that had
not been done). `k6 inspect` parses `stage1-roster.js` and resolves its ramping-viewer stages under that
version, so the script has not been broken by a k6 major release; that was checked without touching the
rig. Stage 1 is the knee run - 800 sessions from 100 Directors, viewers stepping 5, 10, 25, 50, 100 -
and it is where the overlap guard and the ceiling are actually measured. Its configuration must match the
same table in section 3, with 20 tenants x 5 Directors x 8 sessions.

The honest form of the report, decided now so it is not decided by whatever the numbers turn out to be:

- If the machine can be quiesced, the latency comparison is reported as a comparison.
- If it cannot, the run is still taken and reported with the contention stated, and its magnitude is
  called a **FLOOR** rather than a result. The direction is trustworthy; the size is not.
- A single before-and-after latency pair with no provenance note is never published. That is the
  flattering version: a number measured on a worse machine that still looks good, read as like-for-like.

The most valuable Stage 1 outcome is not a latency at all. It is **which resource gives first now**. The
baseline named `SnoozeRegistry._gate`. If something else gives first after this change, naming it is a
better result than any percentage.

---

## 8. The revert proofs, decided in advance

A test that has never been watched failing is decoration. Four reverts, each with the symptom it must
produce named BEFORE it is run, so the observation cannot be fitted to the expectation afterwards. The
work is already committed and pushed (`1a7364d5f`), so every fault below is injected on top of a saved
tree and restored with a checkout rather than by hand.

| # | What is reverted | Test that must go RED | The symptom it must report |
|---|---|---|---|
| 1 | The whole batched read - the fold calls `HoldStateFor` / `IsExpired` / `SnoozeUntilFor` per session again | `TheWholeFold_TakesExactlyOneSnoozeRead_HoweverManySessionsItStamps` | **12**, not 1: four sessions x three reads |
| 2 | ONLY the second loop - loop one keeps the snapshot, `s.SnoozeUntil` goes back to `snoozeRegistry.SnoozeUntilFor` | the same test | **5**, not 1: one batched read plus four per-session reads |
| 3 | The sweep's overlap guard - the `CompareExchange` removed, everything else kept | `ATickThatArrivesWhileAPassIsRunning_IsSkipped_AndIsCountedAsSkipped` | the second pass RUNS (1, not 0), `sweepSkipped` does not move, and `sweepOverlaps` moves by 1 |
| 4 | ONLY the counting - the guard stays, the `SweepSkipped()` call is deleted | the same test | the skip is invisible: `sweepSkipped` does not move although the tick was skipped |

Reverts 2 and 4 are the ones worth the extra minutes, and they are the ones a hurried run would drop.
Revert 1 proves the test notices the fix being gone; revert 2 proves it notices the fix being **half**
there, which is the failure this work item was explicitly warned about and the only one that would have
looked like success. Revert 4 is the same shape for the instrument: it proves the countability of a
skipped tick is pinned by something, rather than being a property nobody would miss.

The controls: in every one of the four, the correctness tests
(`EveryShapeOfRow_FoldsToTheSameAnswerItDidBefore`, `TheSnapshotAndThePerSessionReaders_CannotDisagree`,
`TheSetBasedRead_IsScopedToItsTenant`) must stay GREEN. They assert behaviour, and reverts 1 and 2 restore
a path that behaves identically - so a correctness test that reddens under them is a test that was
measuring the implementation rather than the answer.

---

## 9. The gate result - 3 August 2026, `-Parked` on `f41b08aa5`

**Invocation: `scripts\test-local.ps1 -Parked`** - the release gate, which is the only invocation that runs
this work item's parked facts. A default run would have executed five of the thirteen and reported green.

| Project | Outcome | Total | Executed | Failed |
|---|---|---|---|---|
| CcDirector.Gateway.Tests (parked) | **Completed** | 2484 | 2437 | **0** |
| CcDirector.Gateway.UnitTests | Failed | 2773 | 2765 | 1 |
| CcDirector.Core.Tests (parked) | Failed | 4238 | 4230 | 2 |
| Avalonia / Engine / HostedAgent / Launcher / Terminal | Completed | 346 / 63 / 88 / 110 / 24 | all | 0 |

**All thirteen of this work item's facts EXECUTED and PASSED**, in the projects they were deliberately
placed in - five in `Gateway.UnitTests` (every gate) and eight in `Gateway.Tests` (release gate). Named
individually in the two TRX files, which is the check that replaced the count comparison after the split.

**The three failures are not this branch's, and that is established by evidence rather than asserted:**

- `Gateway.UnitTests / TranscriptStoreTests.Append_NullCleaned_DefaultsToRaw` - "The collection has been
  marked as complete with regards to additions", the SQLite pool race of issue #2414, in a file this branch
  has never opened. The harness fix `4e396b801` reduced that race without removing it; the residue is
  recorded on the issue.
- `Core.Tests / GatewayOnlyTranscriptionGuardTests` and `Core.Tests / NoCrossMachineLoopbackGuardTests` -
  two path-keyed architecture guards. Between them they name **twelve** offending files, and **all twelve
  are in `src/CcDirector.Gateway.UnitTests/`**, the project main created when it split the suite. Not one
  is this branch's. A file move past a path-keyed allowlist is what broke them, so removing these commits
  could not change the outcome.

**Counts are recorded rather than compared, and here is why that is not an evasion.** The split moved about
2,750 tests between two projects and a later commit moved more, so `test-baseline.md`'s numbers describe a
layout that no longer exists; comparing against them would produce a 2,750-test "fall" that means nothing.
The named-facts check above is what carries the weight instead, and it is stronger for this purpose: a
count catches a collapsed run, but only naming the facts catches a hollow one.

## 10. What is NOT proven

- **The four revert proofs in section 8 have not been run.** Each needs its own acquisition of the
  machine-wide lock, and the Architect's instruction was to report the gate and stop rather than add work.
  They are written down, in order, with the symptom each must produce - but they are a plan, not evidence,
  and nothing here should be read as though a test has been watched failing.
- **Stage 1 has not been run.** It is the latency and ceiling measurement, it needs a quiet machine, and
  this one has not been quiet. The overlap guard therefore rests on its unit tests and not on a load
  measurement; the baseline's 91-overlaps-in-98-ticks figure has no measured counterpart yet.
