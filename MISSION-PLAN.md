# Remove the network port - phase detail and proofs

Companion to `MISSION.md`, which holds the charter and the rulings. This file holds the phases, what
each one changes, and what proves it.

## Route inventory (read from origin/main)

**Director - 29 routes.**

| Group | Count | Fate |
|-------|-------|------|
| Agent-facing fleet routes | 21 | Already forward to a Gateway call that exists in production. Delete the local wrapper; the tools call the Gateway. |
| Session-hook routes | 3 | Become files the Director writes and watches. |
| Lifecycle (health, shutdown, update check, update status) | 4 | Process handle and a named event. Never the Gateway - they must work when it is down. |
| Diagnostics (prompt delivery failures) | 1 | Dies. The log file is the durable record. |

The 21, each with the Gateway call it already relays to: sessions, repositories, worktrees, machines,
machines/apps, machines/files, machines/launch, directors, buffer, send, ask, prompt, spawn, rename,
interrupt, compact, hold, role, mission, done, broadcast.

**Launcher - 9 routes.** It already holds an outbound connection to the Gateway's launcher hub
(`LauncherStreamClient` -> `LauncherHub`). Seven are the Gateway calling in and become pushes down
that connection: healthz, status, apps, files, launch, director/start, director/stop. Two are
lifecycle: director/restart (also called locally by the updater) and shutdown.

## Phases

### 1 - Gateway parity (DONE)
Confirm the Gateway routes answer a caller holding a SESSION credential.
**Finding:** it has none. See `MISSION.md`. Created phase 1b.

### 1b - Session credentials on the Gateway (DONE)
A session-bound, expiring, revocable key the Gateway verifies and scope-limits.
**Proof:** unit and integration tests plus fault injection on both detectors; both parked suites
green. See `PHASE-1B-REPORT.md`, including three gaps it records rather than hides.

### 2 - The command line tools talk to the Gateway
Repoint `tools/cc_shared/director.py` and `director_token.py` at the Gateway, presenting the phase-1b
key. The Director's routes stay alive behind a switch so the fleet is never without tooling.
**Proof:** every `cc-*` command works with the Director's routes switched OFF. Also the first
end-to-end exercise of the phase-1b credential, and the place the launch-window race gets tested
under a deliberately slow Gateway.

### 3 - Session hooks stop needing an API

**Correction to the brief: "write it at launch" is WRONG and would ship stale text.**
`FleetPreamble.BuildForSession` renders from three LIVE stores at the moment it is called - the
user's own injected text (`InjectedTextStore.ActiveTemplate()`, editable in Settings while sessions
run), the workflow index, and the skill index (both refreshed from the Gateway). All three change
during a session's life, and the hook fires again on every resume, clear and compact - possibly hours
after launch. A file written once at launch would serve a user their old text after they had edited
it, and would hide newly published skills and workflows.

**The correct design: the Director MAINTAINS a current file per session**, rewriting it when its
inputs change. The machinery already exists - the Control API host already polls injected text on an
interval precisely so a change needs no restart. That poll gains a second job: rewrite each live
session's preamble file. The hook then reads a file that is current, not a snapshot.

The report-back after a clear or compact becomes a file the Director watches.

**Proof:** a fresh session shows its identity block; a clear and a compact still re-discover the
transcript; AND editing the injected text, or publishing a skill, changes what the NEXT hook fire
delivers to an already-running session. That last one is the test that would have caught the
snapshot design.

### 4 - Lifecycle off HTTP

**Correction to the brief, found by checking rather than by assuming.** The claim that "the launcher
owns the process it started" is FALSE in the way that matters. `DirectorSupervisor` does not retain a
handle: it starts the Director with `using var proc = Process.Start(...)`, disposing the handle
immediately, and afterwards finds it again by NAME - `Process.GetProcessesByName("cc-director")`.

That is fine for "is a Director running" and needs no network call, so the phase's direction holds.
But it CANNOT answer "is THIS Director running" on a machine with several - which is exactly this
owner's setup, with named instances and development slots. A name scan returns all of them. Whatever
replaces the health route must identify a SPECIFIC Director (process id from the instance
registration, or the parent chain - never the name alone, and never the exe path, which is shared
across instances).

**Confirmed, not assumed:** the crash journal really does carry the session roster
(`DirectorCrashJournal.Sessions`, a `List<DirectorCrashJournalSession>`, refreshed on every change),
so the session count that decides whether an update would interrupt live work can be read from a file.

Version comes from the exe on disk. Shutdown and restart become a named event.

**Proof:** a self-update swaps the exe with live sessions running, and the launcher restarts a killed
Director - both with no Gateway reachable - AND liveness resolves the right Director on a machine
running more than one.

### 5 - Delete the Director's listener
Remove the web host, the port allocator (range, reservation files, excluded-range reader), and the
control endpoint from the instance registration.
**Proof:** nothing listening for cc-director in a live connection scan, AND the first-launch wizard
popup is gone on a clean machine. This phase is what removes that popup permanently.

### 6 - Delete the launcher's listener
**Smaller than briefed, and verified before briefing it.** The launcher does not merely hold an
outbound connection - it already registers a downward COMMAND handler on it
(`LauncherStreamClient.cs`: `_connection.On<LauncherCommand, LauncherCommandResult>("Command", ...)`)
and already dispatches six verbs: `director/start`, `director/stop`, `director/restart`, `launch`,
`apps`, `files`. Six of the nine routes therefore have a working non-HTTP path today.

What is actually left:
- `healthz` and `status` - the Gateway is already told the launcher's port and version in the hub
  Hello, and liveness IS the connection being up. No verb needed; read what the hub already knows.
- `shutdown` - lifecycle, handled by Phase 4's named event, not by the hub.
- Confirm the Gateway's machine endpoints dispatch over the hub rather than still dialling the
  launcher's HTTP port, then delete the web host.

**Proof:** nothing listening for the launcher in a live connection scan; starting, stopping and
restarting a Director from another machine still works.

### 7 - The guard
A fitness test that fails if a listener is added back to either program.
**Proof:** add one on purpose and watch it go red. A guard that has never failed has not been shown
to work.

## Order

1b and 2 are the bulk. 3 is independent of 2 and can run beside it. 6 is the cheaper visible cut and
should NOT be left until last. 4 is the one that gets underestimated - it is a channel with its own
availability requirement, not plumbing. 5 can only follow 2, 3 and 4. 7 is last so it guards the
finished state.
