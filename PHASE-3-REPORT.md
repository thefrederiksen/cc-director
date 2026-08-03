# Phase 3 - Session hooks stop needing an API

Manager: session 71673d6b. Branch `mission/remove-network-port`, worktree `D:\ReposFred\devthrottle-noport`.
Base: `e5b1d3447`. Commits: `03394e673` (the change) and `881d19d9b` (a defect the change's own
end-to-end test found). Both pushed. **Not merged to main** - that is the Architect's.

---

## What the phase was for, and what it did

The Director had three routes on its Control API that existed only so a coding agent's own SessionStart
hook could call them:

| Route | What it did | Who called it |
|---|---|---|
| `GET /sessions/{sid}/fleet-preamble` | the preamble as plain text | the Windows Claude hook, the Codex hook |
| `GET /sessions/{sid}/fleet-preamble-hook-output` | the same, pre-wrapped as hook output JSON | the macOS/Linux Claude hook |
| `POST /sessions/{sid}/claude-hook` | the session's current Claude id + transcript path | both Claude hooks |

**All three are deleted.** Both jobs are now files whose exact paths the Director stamps into the
session's environment, so a hook holds no address, presents no credential, and needs nothing listening.
That was the phase's whole assignment: make Phase 5 able to delete the listener.

## The design correction was kept, and it is the whole design

The brief said "write it at launch" was wrong. It is, and the reason is worth restating because it is the
part that would have been easy to get away with.

`FleetPreamble.BuildForSession` renders from **three live Gateway-owned stores** - the user's own injected
text (`InjectedTextStore`, editable in Settings while sessions run), the workflow index and the skill index
- plus the session's own display name and workflow seat. The SessionStart hook fires again on **every
resume, clear and compact**, possibly hours after launch. A file written once at launch would serve a user
their OLD text after they had edited it and would hide skills published since, and **nothing would look
broken**. It would simply be wrong.

So the Director **maintains** the file:

- **The three shared stores** - `RefreshInjectedTextAsync` already re-downloads all three on a 60-second
  interval, precisely so an injected-text change needs no restart. That refresh gained a second job:
  `SessionPreambleMaintainer.RewriteAll()` at the end of each cycle.
- **The per-session inputs** - the display name (`OnSessionRenamed`), the explicit role and the workflow
  seat (a new `Session.OnPreambleInputsChanged`, raised by `SetExplicitRole` and `SeatOnWorkflow`).
  Those rewrite the one affected file the moment they change.
- A rewrite that renders identical content does not touch the file, so a quiet Director does no disk work.

**The file is exactly as fresh as the routes it replaced - not merely close.** This is worth stating
precisely, because "a file must be staler than an on-demand render" is the obvious objection and it is
false here. The injected text is **Gateway-owned**: there is no local Settings write. The deleted routes
rendered on demand, but they rendered from the Director's *cache*, which only moves when that same refresh
writes it. Rewriting at the end of the refresh means the file and the deleted route would always have
produced the same bytes. There was never anything fresher for a route to read.

## Three code paths for the preamble collapsed into one

The two preamble routes existed because the POSIX shell hook cannot escape arbitrary text into JSON, so it
needed a pre-wrapped envelope, while PowerShell built its own. Codex built a third. The Director now writes
**the finished envelope** to one file per session and all three hooks print it verbatim.

That removes a whole class of defect rather than an endpoint. Issue #1357 was exactly this shape: the
Windows route resolved the signed-in user and the macOS/Linux route silently did not, so the same text
built two ways was wrong on one platform for as long as both existed. One file cannot have that defect.
Two more went with the HTTP call: a hook answered 401 in silence (the scripts swallow everything and exit
0), and a server error body printed to stdout arriving in the agent's context **dressed as the preamble**.

## What was built

| File | Role |
|---|---|
| `Core/Sessions/SessionHookFiles.cs` | the one place naming both files and both environment variables |
| `Core/Sessions/SessionPreambleFile.cs` | renders one session's finished hook output; empty means inject nothing |
| `Core/Sessions/SessionPreambleMaintainer.cs` | keeps every live session's file current |
| `Core/Sessions/SessionPointerWatcher.cs` | the drop box the pointer report is written into |
| `Core/Claude/ClaudeHookEventParser.cs` | moved from `ControlApi` (the route it parsed for is gone) |

Changed: both hook installers (new script bodies), `SessionManager` (writes the file and stamps the paths
before the process starts), `ControlApiHost` (starts both halves in `StartSessionStateServices`, so they run
even when the port fails to bind, and calls `RewriteAll` after each refresh), `Session` (the new event),
`CcStorage` (two directories), `ControlEndpoints` (the deletions), `ControlApiGuard` (see below).

Environment variables stamped per session: `CC_SESSION_PREAMBLE_FILE` (Claude and Codex - the two families
with a SessionStart hook) and `CC_SESSION_POINTER_FILE` (Claude alone - it is the only agent that mints a
new session id on clear and compact). The paths are **handed over rather than computed**, because working
out the storage root per platform and per named instance inside two shell dialects would be a second copy
of `CcStorage` that could drift from it silently.

## The allow list lost three entries too

`ControlApiGuard.CheckSessionChild` granted a session-child credential the three `/sessions/{sid}` hook
routes. Those entries are **deleted**, not left harmlessly matching nothing. This list is prose the next
reader trusts; one that names routes which do not exist teaches whoever reads it next that a credential
reaches a surface it cannot, and reads as permission to re-add the route. Nothing under `/sessions/{sid}` is
open to a child now - its remaining own-session read is `/fleet/buffer`.

---

# Proof

## The three items the brief demanded

**1. A fresh session shows its identity block.** `HookScriptRoundTripTests` runs the REAL
`report-session.ps1` under the REAL `powershell.exe`, with only the two environment variables set, and
parses its stdout: the SessionStart envelope, carrying `cc-devthrottle`, the session id, and
`The user of this session is Starlord (star@example.com).` Also asserted at the file level in
`SessionPreambleFileTests`.

**2. A clear and a compact still re-discover the transcript.**
`A_clear_then_a_compact_each_move_the_pointer_again` fires the real script twice with `source=clear` then
`source=compact`, and requires the Director's **live watcher** to move `ClaudeSessionId`,
`ClaudeTranscriptPath` and the manager's claude-id routing map each time - and the preamble to be
re-injected on both fires, which is what makes an agent still know the fleet after its context was cleared.

**3. Editing the injected text changes what the NEXT hook fire delivers to an ALREADY-RUNNING session.**
This is the one that would have caught the wrong design, and it is proved twice.

- `A_rewritten_preamble_reaches_the_next_fire_of_the_same_running_session` - **on a real running session,
  not by unit test alone**, as the brief required. A session is adopted, the real hook script is executed
  and its stdout carries the launch-time text; the injected-text store is then changed and the maintainer
  rewrites; the SAME script is executed again and its stdout carries the NEW text and no longer the old.
- `SessionPreambleMaintainerTests` proves the same for an edit, for switching back to the DevThrottle
  text mid-session, for text that becomes unreadable mid-session (the file empties rather than serving the
  stale copy), for taking a workflow seat, and for a rename.

## The wrong design was injected on purpose, and here is what it would have passed

`RewriteAll()` was made a no-op - the launch-snapshot design - on a clean rebuild.

| | red | green |
|---|---|---|
| unit (parallel half) | **3** | 43 |
| round trip (real script) | **1** | 5 (later 6) |

The three reds are the three "a mid-session change reaches the next fire" assertions; the fourth is the
real-script version of the same. **Everything else stayed green** - launch, the seat, the rename, the whole
drop box, the entire script contract. So a launch-time snapshot would have passed 48 of 53 tests, and the
five that catch it are the five the brief asked for. That is the measurement behind the correction, not an
argument for it.

Two more faults, both on clean rebuilds:

- **The Windows hook stops landing its drop** (writes to a name nothing watches): exactly the **3**
  pointer-dependent round-trip tests go red, the 5 preamble-only ones stay green. Both delivery paths red,
  so neither is quietly standing in for a broken hook.
- **One deleted route put back**: `SessionHookRoutesAreGoneTests.The_two_fleet_preamble_routes_answer_404`
  goes red and nothing else does. The deletion detector can see a route.

## The deletion is proved against the running Director, not against the source

`SessionHookRoutesAreGoneTests` starts a real `ControlApiHost` on a real port, adopts a real session, and
asks. Two things make the answer believable, and the mission learned both the hard way:

- **The probe holds a credential the Director ACCEPTS** (admin). Phase 2 found that a probe with an invalid
  credential answers 401 for everything *including routes that still exist* - an authentication refusal
  standing in for absence. With an accepted credential, 404 is the router saying there is no such path.
- **Positive controls**: `update/status`, `settings` and `fleet/sessions` answer non-404 through the same
  client on the same host, and the control asserts the probe's own credential was not refused. Without
  them, a host that mapped nothing at all would produce a clean sweep of 404s and read as success.

It also asserts the replacement is up on that same host - the Director maintained a preamble file for the
session it adopted, with no route and without being asked - and that a POST to the dead `claude-hook` path
leaves the pointer untouched, which matters more than the status code.

## A defect the phase's own test found

The end-to-end test failed **about one run in five**. It was chased rather than re-run.

A first theory - that the watcher's `File.ReadAllText` was locking the hook out of replacing the file - was
**refuted by its own test**: on Windows a replace over an open handle fails even when the reader grants
`FileShare.Delete`, so no share mode could have fixed it, and it was not the cause anyway. Backed out
entirely. It is recorded because it cost time and because a plausible mechanism that survives unexamined is
how a wrong fix ships.

Reproducing with diagnostics named the real cause. The failure message now prints the drop box's contents,
and it said: the drop file was **present, complete, 183 bytes, correctly named, valid JSON**; the session's
pointer had **not** moved; and **no `FileSystemWatcher` Error event had been raised**. So the notification
was simply lost, and the documented buffer-overflow signal - which the watcher already answered - does not
cover it.

**The fix: the sweep is the delivery guarantee and the watcher only makes it fast.** A two-second timer
reads the box; the watcher still applies a drop in milliseconds when its notification arrives. An applied
drop is deleted, so the box is empty in the steady state and sweeping it costs one enumeration of an empty
directory.

This is **not** the fallback the coding standard forbids. There is no degraded second implementation hiding
a broken first one: both paths run the same `Apply`, the sweep is always correct on its own, and neither can
hide a fault in the other because they do the same thing. A lost drop would have cost a stale transcript
pointer - which takes session history and the Gateway voice mode above it down with nothing turning red -
and that is too much to rest on an operating-system facility that demonstrably drops events.

Proof of the fix: **25 consecutive round-trip runs green**, against 1-in-5 to 1-in-10 red before. Plus a
test that **suppresses the watcher entirely** and requires the sweep alone to deliver a real hook drop -
which has to be tested separately, because with both paths running the watcher wins the race nearly every
time and would mask a sweep that did not work at all.

## Tests

| Suite | New / changed |
|---|---|
| `Core.UnitTests` (default gate) | `SessionPreambleFileTests` 11, `SessionPreambleMaintainerTests` 10, `SessionPointerDropTests` 13, `HookScriptContractTests` 14 - **48 cases** |
| `Core.Tests` (parked) | `HookScriptRoundTripTests` 8 - real script, real interpreter, live watcher |
| `Gateway.Tests` (parked) | `SessionHookRoutesAreGoneTests` 7 new; `ControlApiHostileAccessTests`, `ControlApiAuthReapplyTests`, `ControlApiGuardTests`, `WorkflowSeatTests` repointed |

The default gate gained **48 tests** (Core.UnitTests 89 -> 137), and the phase's heaviest proof - the real
script under the real interpreter - now runs **outside** the Gateway suite's machine-wide lock, because it
no longer needs an HTTP host. That is a side benefit of the change itself.

**`HookCredentialTests` was replaced, not dropped, and the distinction matters.** It asserted that each
script presents an `Authorization` header - correct while the hooks called authenticated routes. A hook
that still presented a credential would now be evidence the change had not happened. So
`HookScriptContractTests` asserts the inverse - no credential, no address, no `curl`, no
`Invoke-RestMethod`, no `http://`, no route name, in any of the three scripts - for the identical reason the
original existed: these scripts swallow every error and exit 0, so a broken one is indistinguishable from a
working one and the wiring has to be pinned in text.

`FleetPreambleEndpointTests` and `ClaudeHookShellScriptIntegrationTests` were deleted: both drove routes
that no longer exist. Their coverage moved to `SessionPreambleFileTests` and `HookScriptRoundTripTests`
respectively, and the latter is stronger - the old one skipped entirely on Windows.

---

# The landing criterion, applied comparatively with a repeated control

Run on `881d19d9b` and on its base `e5b1d3447`, the latter in its own worktree, **three times** as the
Architect required after the previous phase's single green control nearly convicted it wrongly.

| Arm | Run 1 | Run 2 | Run 3 | Run 4 |
|---|---|---|---|---|
| **Mine** `881d19d9b` | 0 | **62** | 1 | 0 |
| **Parent** `e5b1d3447` | **1** | **2** | **1** | - |

**No failure is mine.** The evidence, not the assertion:

- **Every failure on both arms is in `CcDirector.Gateway.UnitTests`** - a suite this phase adds nothing to
  (total 2972 on both arms, identical).
- **The parent fails in three runs out of three.** Its four failures are four distinct tests with zero
  repeats: `SuggestionEmailComposerTests`, `GatewayInputStatsAggregatorTests` (two), and
  `MorningReportSettingsResolverTests`.
- **`SuggestionEmailComposerTests` fails on BOTH arms** - the same class, on the parent and on mine.
- **Every failure on both arms carries one of two messages**:
  `InvalidOperationException: The collection has been marked as complete with regards to additions` or
  `ObjectDisposedException`. That is the signature of a defect **already documented in this repository**:
  `docs/missions/review-findings-2026-08-01/W3-the-default-gate-is-flaky-and-it-is-not-this-branch.md`
  names the mechanism - `GatewayDatabase.Dispose` calls `SqliteConnection.ClearAllPools()`, which is
  **process-wide**, while 79 classes in that assembly share `GatewayDbTestHarness` across four concurrent
  collections. It is issue #2414.
- **`Core.UnitTests`, which is where all 48 new tests live, was `Completed` with 137 of 137 in all four
  runs on my arm** (89 on the parent - the difference is exactly the new tests).

**On the 62.** It is much larger than the parent's 1 or 2 and should not be waved past, so: the count is a
property of the named mechanism, not of severity. One process-wide pool clear fails every test in flight at
that instant, so 62 is one unlucky moment in a busy assembly. The same commit produced 0, 62, 1 and 0. The
classes involved (`AuthMiddlewareTests`, `BrowserSignInGateTests`, `NetDiagDeviceStoreTests`,
`SharedWorkflowLibraryTests`, the Gateway stores) are untouched by this phase and unreachable from it.

**Both standing hazards were respected.** Every result reported here comes from a build with `obj` and
`bin` deleted for the projects under test - before the first gate run, and before each of the three fault
injections. And no red is called pre-existing anywhere in this report without the control run beside it.

## The parked suites

The default gate flagged the coverage gap itself: this change touches code covered by `Core.Tests` and
`Gateway.Tests`, and neither runs by default. Both were run.

*(See "Parked run" below - filled in from the actual run.)*

Targeted runs on my commit, all green, on clean rebuilds:

- `Core.UnitTests` new classes: 48 of 48.
- `Core.Tests` `HookScriptRoundTripTests`: 8 of 8, and 25 consecutive whole-suite runs green.
- `Core.Tests` installer, storage, pointer and preamble classes: 67 of 67.
- `Gateway.UnitTests` parser and guard classes: 10 of 10.
- `Gateway.Tests` affected classes (`ControlApiGuardTests`, `SessionHookRoutesAreGoneTests`,
  `ControlApiHostileAccessTests`, `ControlApiAuthReapplyTests`, `WorkflowSeatTests`): 142 of 142.

---

# What this phase did NOT prove, and one thing it changed on purpose

**Nothing here ran on macOS or Linux.** The POSIX hook script is exercised only by its text
(`HookScriptContractTests`); `HookScriptRoundTripTests` runs whichever script belongs to the current
platform, which on this machine is PowerShell. The POSIX script is *simpler* than the one it replaced - no
`curl`, no authentication branch, no pre-wrapped envelope - but "simpler" is not "run". The route it
replaced had the same gap: the old shell test returned early on Windows and therefore never ran here
either. This is not a regression in coverage; it is an unchanged hole, now stated.

**Per-session isolation of the preamble is weaker on paper, and unchanged in practice.** The deleted route
required a session-bound credential, so session A could not read session B's preamble over HTTP. The file
sits under the user's own storage root, so any process running as this user can read it. That is not a new
boundary being crossed: `ControlApiHost` already states in code that this was never an operating-system
sandbox - "a process running as this user can read the secret off disk and mint itself full authority" - so
session A could always have minted B's token. And the content is a session's name, repo and the same
indexes and user email A already has in its own preamble; the roster is an *allowed* discovery call. The
pointer direction is bound by the drop box's shape: the session comes from the FILE NAME, never from the
body, which `A_drop_is_applied_to_the_session_its_FILE_names_not_one_named_inside_it` pins with a body that
names another session in every field it could.

**One bounded window is accepted rather than closed.** The maintainer's rewrite is a replace over the
preamble file; on Windows that fails if a hook is reading it at that instant. The consequence is small and
self-healing: the write is caught and **logged loudly**, the file keeps its previous content (valid, one
refresh old), and the next cycle fixes it. Closing it would mean a stream-and-share dance inside a
PowerShell script that swallows all errors - where a mistake costs an agent its preamble silently. The loud,
self-healing 60-second window is the better trade. The severe direction - the pointer drop, where a loss was
silent and permanent - is the one that was fixed.

**The seat and role rewrite has no cross-machine trigger.** A seat stamped by the Gateway on this
Director's session raises the new event locally. If a seat could ever change without passing through
`SetExplicitRole` or `SeatOnWorkflow`, the 60-second refresh is the floor that catches it.

## For Phase 5

Nothing in this phase removed `CC_DIRECTOR_API` - the two blockers `MISSION-PLAN.md` records for Phase 5
are untouched and still apply. What Phase 5 gains is that no session hook, on any platform, for either hook
family, calls the Control API any more; and `ControlApiGuard`'s session-child allow list no longer names
anything under `/sessions/{sid}`.
