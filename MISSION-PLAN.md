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
The Director writes the agent's identity text to a file at session launch; the startup script reads
it. The report-back after a clear or compact becomes a file the Director watches.
**Proof:** a fresh session shows its identity block; a clear and a compact still re-discover the
transcript.

### 4 - Lifecycle off HTTP
Liveness from the process the launcher already owns; version from the exe on disk; session count from
the crash journal the Director already writes; shutdown and restart via a named event.
**Proof:** a self-update swaps the exe with live sessions running, and the launcher restarts a killed
Director - both with no Gateway reachable.

### 5 - Delete the Director's listener
Remove the web host, the port allocator (range, reservation files, excluded-range reader), and the
control endpoint from the instance registration.
**Proof:** nothing listening for cc-director in a live connection scan, AND the first-launch wizard
popup is gone on a clean machine. This phase is what removes that popup permanently.

### 6 - Delete the launcher's listener
Seven routes move onto the outbound connection that already exists; the web host goes.
**Proof:** nothing listening for the launcher; starting, stopping and restarting a Director from
another machine still works.

### 7 - The guard
A fitness test that fails if a listener is added back to either program.
**Proof:** add one on purpose and watch it go red. A guard that has never failed has not been shown
to work.

## Order

1b and 2 are the bulk. 3 is independent of 2 and can run beside it. 6 is the cheaper visible cut and
should NOT be left until last. 4 is the one that gets underestimated - it is a channel with its own
availability requirement, not plumbing. 5 can only follow 2, 3 and 4. 7 is last so it guards the
finished state.
