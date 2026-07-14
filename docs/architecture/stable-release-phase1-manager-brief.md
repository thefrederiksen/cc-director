# Phase 1 Manager brief - Stable Release (DevThrottle v1.3.0)

Mission id: `ac6883bb-09e2-4b5a-96bf-df3eae8d9f63`
You are the **Manager** for Phase 1 only. The Architect is session `2eef41a3` ("Stable Release - Architect").

Read the mission brief first: `docs/architecture/stable-release-mission-2026-07-14.html`
It holds the WHY. Do not re-derive it. Do not re-open a Tier 3 decision.

## Your scope: Tier 1 item 1 only

**A dropped command must explain itself.** Nothing else. Items 2 to 4 belong to later Managers.

## What the Architect verified against origin/main (do not re-derive; build on it)

The whole Gateway-to-Director command path funnels through ONE chokepoint:

- `src/CcDirector.Gateway/Api/DirectorCommandRouter.cs` -> `TrySendAsync(...)`
  Every command verb routes through here. It calls the injected `sendCommand` delegate with only the
  caller's cancellation token and no timeout of its own.
- That delegate is `GatewayHost.SendCommandAsync` (`src/CcDirector.Gateway/GatewayHost.cs:1758`), which
  ends in:
  `await hub.Clients.Client(connectionId).InvokeAsync<DirectorCommandResult>("Command", command, ct)`

Two proven failure modes, both live today:

1. **Indefinite hang.** If the Director stays tunnel-connected but never answers the command, that
   `InvokeAsync` never completes. There is no timeout anywhere on the path. On a weak mobile link this is
   a silent forever-wait.
2. **Raw HTTP 500.** When the tunnel drops mid-command `InvokeAsync` throws. No caller on the path
   catches it - `SessionVerbClient` and the command endpoints have no try/catch - so it escapes as an
   unhandled exception and the caller sees a raw 500 with no explanation.

Today `TrySendAsync` has exactly one non-success outcome: it returns null when the Director is not
tunnel-connected, and the endpoint surfaces that as a 502. Timeout and mid-command drop are NOT
distinguishable from each other or from anything else.

**Stale comment, fix it while you are there:** `GatewayHost.SendCommandAsync`'s documentation comment says a
null return means the caller "falls back to the HTTP command path". That path was deleted in the cut. The
comment is one of the lies this release exists to remove. `DirectorCommandRouter`'s own comment already has
it right.

## The design (settled by the Architect - implement it, do not redesign it)

- **The timeout goes in `DirectorCommandRouter.TrySendAsync`, and nowhere else.** It is the one chokepoint;
  putting it there means it cannot diverge across verbs. Do not scatter timeouts into individual callers.
- **Three outcomes must be distinguishable to the caller**, each with a plain-English message that names
  what actually happened:
  1. Director not tunnel-connected (unroutable) - the existing null/502. Unchanged.
  2. **Command timed out** - the Director is connected but did not answer in time.
  3. **Tunnel dropped mid-command** - `InvokeAsync` threw. Catch it at the chokepoint.
- **Default timeout: 30 seconds**, as a named constant, with an optional per-call override parameter on
  `TrySendAsync` defaulting to that constant. Rationale: it must bound the hang without breaking
  legitimately slow verbs. If you find a verb that genuinely needs longer (session create is the likely
  candidate), give that call site an explicit override - do not raise the global default. If evidence says
  30 seconds is wrong, bring it to the Architect rather than changing it silently.
- **No fallback and no retry.** A timeout fails loudly with a message that says so. Do not retry, do not
  degrade, do not swallow.
- **The message is for a human in a moving car.** "The Director on SOREN_NORTH did not answer within 30
  seconds" - not a status code, not a stack trace, not jargon.
- Use a linked cancellation token so the caller's own token still cancels promptly.

## The rules that are not negotiable

1. **Verify before you claim.** Read shipped code with `git show origin/main:<path>` or
   `git grep <pattern> origin/main`. Never a commit message, never a memory, never a tree that may be
   behind.
2. **Work in a worktree cut from origin/main.** Never `git checkout -b` in the shared checkout - other
   sessions are working in it. `git worktree add ../devthrottle-<task> -b <branch> origin/main`
3. **Probe the route, never the version.** `/healthz` cannot tell a fixed build from a broken one. Probe
   the real route against a deliberately fake one. **Never probe `POST /fleet/spawn`** - it returns 201
   and creates a real session.
4. **Proof is a running build, not a green test.** Demonstrate the timeout and each typed error on a
   running slot Director before calling it done. A green test alone is not proof.
5. **No fallbacks.** Fix the root cause or fail loudly with a clear message.
6. **Plain English, ASCII only.** No abbreviations, no jargon, no emoji, anywhere - code, comments, logs,
   messages, pull request text.
7. **Do not commit without the owner asking.** Do not merge. Do not tag.
8. Do not message the owner. Report to the Architect.

## Testing

There are **seven** test projects, not two. Core plus Gateway alone is a false green - `HostedAgent` has
its own fake backend. Run the full set.

## The fleet

| Director | Port | Use |
|---|---|---|
| slot 6 | 7883 | **Testing. Yours.** Throwaway proof runs. |
| slot 2 | 7884 | Permanent. The Architect runs here. Do not disturb. |
| slot 1 | 7881 | Older build, hosts another session. Leave it alone. |
| installed app | 7879 | v1.1.0, the broken build. Never test against it. |

Each slot needs its OWN scheduled task (`cc-director-launch-slot<n>`). A shared task kills whichever
Director it launched last - this already killed slot 1 once today.

Build a slot: `powershell -ExecutionPolicy Bypass -File scripts\local-build-avalonia.ps1 -Slot 6`

Never kill a `cc-director*.exe` you did not launch. Shut your own test Director down cleanly with
`POST http://127.0.0.1:<port>/shutdown` - a force-kill leaves a phantom session in the fleet.

## How you work

- Spawn Workers on **disjoint files**; they report to you.
- **Ping the Architect at every milestone** - design question settled, worker landed, proof captured,
  blocked. Never stall silently.
- Fleet messages are ONE line - `send` truncates at the first newline.
- You do not wait on the Architect for permission to proceed. Bring design questions, not approvals.

## Definition of done for Phase 1

1. A command that hangs is bounded by the timeout and returns a typed error naming the timeout.
2. A command interrupted by a tunnel drop returns a typed error naming the drop - never a raw 500.
3. The Director-not-connected case still behaves as it does today.
4. All three demonstrated on a running slot Director, with the evidence captured.
5. The stale "falls back to the HTTP command path" comment is gone.
6. A pull request is open and green. You do not merge it.
