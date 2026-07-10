# Fleet Session Lifecycle - the one-true spawn/stop recipe

Status: DRAFT reference. Date: 2026-07-09. Owner: f33d (lifecycle / Thread 1).
Purpose: so ANY agent starts and stops a session the SAME fast way, with zero fumbling. This is
the current (works-today) contract; the `SessionRole` field is a near-term addition from the
streaming/fold tree and is noted where it will land.

## The two calls

You reach only your OWN Director, at the base URL in env `CC_DIRECTOR_API` (loopback). No Gateway
address, no token. Cross-machine is the Gateway's job, not the caller's.

MULTI-COMPUTER START/STOP (SETTLED design - Soren 2026-07-09; authority = the Architect's
`mission-as-first-class-unit-of-work.md` "Multi-computer start/stop (design)" section; this recipe
folds in the operational mechanics):
- (a) TARGET COMPUTER is an OPTIONAL param, DEFAULT = LOCAL (the machine the requesting agent is
  on). No computer needed for the common case.
- (b) LOCAL start = direct POST to your own Director. REMOTE start = via the Gateway to a Director
  on the target machine (first available there), cron-style machine targeting.
- (c) Address the target by MACHINE NAME.
- (d) If the target machine has NO Director running, the Gateway auto-launches one via the
  cc-launcher (requires the launcher-persistent-JOIN - my priority 2; until that lands, remote
  start to a Director-less machine cannot work).
- (e) STOP takes NO computer param - it follows the session's home machine.
- (f) Cross-machine pods work under the existing fleet-wide attention model.
- (g) OFFLINE / unreachable target => FAIL FAST + FAIL LOUD immediately ("computer X is
  off/unreachable"), no queue, no silent local fallback.
Design is settled; the remote / auto-launch path is gated on the launcher-persistent-JOIN
(priority 2). (More additions from Soren may still follow.)

### Spawn (one call)

`POST {CC_DIRECTOR_API}/sessions`  ->  body is `NewSessionRequest`:

```
{
  "repoPath": "D:\\ReposFred\\devthrottle",                      // REQUIRED, must exist on disk
  "name": "short-name",                                          // recommended; weak names rejected
  "agent": "ClaudeCode",                                         // default ClaudeCode
  "args": "--dangerously-skip-permissions --model opus[1m]",     // see THE TRAP below
  "controllerSessionId": "<your session id>",                    // set when spawning a WORKER (you are its manager)
  "purpose": "one line of why",
  "prePrompt": "the worker's objective + output format + boundaries",  // see manager discipline
  "wingmanEnabled": false
}
```

Returns `201` + the `SessionDto` immediately. The process is already up; any `prePrompt` is
dispatched asynchronously after the agent TUI settles (up to `prePromptWaitMs`, default 30000).

Roles (near-term, landing on `feat/director-gateway-stream-1a`): most roles are AUTO-DERIVED, so
you usually pass NOTHING - a session spawned by another session auto-becomes a Worker (its
`controllerSessionId` is auto-set to the spawner), and Manager vs Standalone is derived from
whether it controls live sessions. The ONE role you set EXPLICITLY is ARCHITECT (or any explicit
override): pass `"role": "Architect"` in `NewSessionRequest.Role` at spawn, or call the post-spawn
set-role verb to make (or self-declare) a session an Architect. The CLI carries a minimal `--role`
passthrough (added by the roles worker on the branch); its spec (values
Standalone/Manager/Worker/Architect, forwarded verbatim to `NewSessionRequest.Role`) and the
become-architect UX are owned by this lane.

### Stop (one call)

`DELETE {CC_DIRECTOR_API}/sessions/{sid}`  ->  `{ "killed": true, "removed": true }`.
Best-effort kill then always removes the row (no orphan). `404` if the id is unknown.

To stop a whole TEST Director you launched: `POST {its}/shutdown` (graceful - it kills its own
sessions and deletes its crash journal, avoiding a phantom "interrupted" fleet entry). Never
force-kill unless it will not exit.

## THE TRAP (why agents fumble today)

`POST /sessions` passes `args` LITERALLY and applies NO default model and NO default permission
preset.

- MODEL (a real gotcha): omit `--model <id>` and the session runs on the wrong (200K) default
  instead of the model you meant. Always pass the model you intend.
- PERMISSIONS (the user's choice, NOT a framework rule): the framework does not force or police
  permission posture (Soren, 2026-07-09). If you want an autonomous worker that never stalls on a
  prompt, pass `--dangerously-skip-permissions`; if you want a guarded session, do not. That is the
  setup choice of whoever starts the session, per session.

(Reference: memory `session-launch-model-not-applied` for the no-default-model mechanics.)

## Manager discipline at spawn (from the 2026-07-09 harness survey)

- Every worker's `prePrompt` MUST carry an explicit objective, the expected output format, and
  hard task boundaries. The documented multi-agent failure is two workers duplicating the same
  investigation because the boundaries were vague.
- Only spawn workers for high-value tasks - multi-agent runs cost roughly 15x the tokens of a
  single chat. Do not spawn a fleet for something one session can do.

## The CLI shortcut (and its current gap)

`cc-devthrottle session spawn <repo>` exists but (a) is LOCAL Director only and (b) does not
currently pass model / permission / role. So today a manager still hand-builds the POST for a
correct worker.

THE THREAD 1 DELIVERABLE: extend `cc-devthrottle session spawn` to carry `--role`,
`--manager <id>` (controllerSessionId), `--model`, and `--skip-permissions`, so a manager spawns
a correct worker in ONE line and cannot fall into the trap. That is the "stop fumbling" fix.

## Speed notes (what to benchmark on the slot-5 test Director)

- Spawn returns as soon as the PTY launches - already fast; do not confuse it with agent-ready.
- `prePrompt` adds up to `prePromptWaitMs` (30s default) before the first message lands - that is
  not spawn latency.
- Fleet-wide VISIBILITY of a new session can lag up to the 15s Gateway heartbeat/pull cycle. The
  streaming inversion (fold/streaming tree) replaces that pull with a push and is the real fix for
  cross-machine "why isn't my new session showing yet".
- Benchmark four numbers before optimizing anything: time to `201`, time to agent-ready, time
  from `DELETE` to gone, and time to fleet-visible.
