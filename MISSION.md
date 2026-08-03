# Mission: Remove the network port

Chartered by the owner, 2026-08-03. Architect: session bc291ea4 "Mission Remove Network Port".
Branch: `mission/remove-network-port`. Worktree: `D:\ReposFred\devthrottle-noport`.

This file is the mission's durable state. The Architect keeps its knowledge here, not in its own
conversation, so it can be reset at any boundary and rebuilt from this file alone.

---

## What the mission is for

**No listening TCP port on the Director or the launcher. Everything an agent does goes through the
Gateway - one door, always.**

The owner's reasoning, in his terms: two entry points confuse the agents. An agent that can reach the
fleet two different ways will use the wrong one, and no amount of documentation fixes that. A port is
also a thing that can be reached, scanned, guessed at and exhausted, for something that never leaves
the machine.

It also closes a live defect permanently. On first launch, Windows raises its "allow this app on
public and private networks?" question because the Director's port-picking code opens each candidate
port on every interface to test it. That dialog lands on top of the setup wizard and swallows the
clicks meant for it, so a new user sees a frozen window with no error. Phase 5 deletes that code, so
the popup cannot come back.

## The shape of the answer

**Session communication always goes through the Gateway.** No exceptions, no local fast path, no
second door. An agent reaches any session - this machine or another - the same way.

**Process lifecycle is not session communication.** The launcher supervising the Director, and the
updater making it exit so the exe can be swapped, must work when the Gateway is unreachable. They do
not need a port: the launcher already owns the process it started, and "exit now" is a named event.

## Accepted costs (the owner has ruled on these - do not re-open)

- No Gateway means no agent tooling. The self-hosted gateway is the answer and the error message must
  say exactly that.
- Every agent command becomes a Gateway round trip rather than a local call.
- A Director whose tunnel is down cannot be driven by its own agents.

## The finding that sizes the work

All 21 agent-facing Director routes ALREADY forward to a Gateway call that exists and runs in
production today (verified against origin/main, `ControlEndpoints.cs`, each route's
`gatewayClientProvider` relay). This is deleting a local middleman and repointing the command line
tools. There is no new Gateway surface to build for session work.

The launcher ALREADY holds an outbound connection to the Gateway's launcher hub
(`LauncherStreamClient` -> `LauncherHub`), carrying its machine identity. Seven of its nine routes are
the Gateway calling in, and can become pushes down the connection it already has.

## Architect rulings (made up front, so no phase has to guess)

1. **Credential scope: per-session keys.** Handing every agent the Director's own Gateway key would
   give every agent process the run of the whole account - a widening worse than the port being
   removed. The Gateway already does device enrollment, so the machinery exists. If Phase 1 finds
   this is a large build in its own right, that is a genuine scope discovery and goes to the owner.
2. **Order: the launcher goes early, not last.** It is the cheaper cut because its outbound
   connection already exists. The instinct to do the Director first because it is more familiar is
   the more expensive order.
3. **Phase 4 is scoped as its own channel, not as plumbing.** It has an availability requirement the
   rest of the mission does not: it must work exactly when the Gateway does not.
4. **No fallbacks.** A phase either moves a caller to the Gateway or it does not. Nothing may "try
   the Gateway and fall back to the port" - that is the second door this mission exists to remove.

## Phases

| # | Phase | State |
|---|-------|-------|
| 1 | Gateway parity, proven with a session credential | DONE - finding below |
| 1b | Session credentials on the Gateway (discovered in Phase 1) | not started |
| 2 | The command line tools talk to the Gateway | not started |
| 3 | Session hooks stop needing an API | not started |
| 4 | Lifecycle off HTTP | not started |
| 5 | Delete the Director's listener | not started |
| 6 | Delete the launcher's listener | not started |
| 7 | The guard test | not started |

Full phase detail, proofs, and the route inventory: `MISSION-PLAN.md` in this directory.

## Phase 1 finding - the mission's real shape

**The Director already has session-scoped credentials. The Gateway does not.**

- Director: a session is stamped with `v1.session-child.<sessionId>.<hmac>` (`tools/cc_shared/director_token.py`),
  and `ControlApiGuard` limits that credential to reading its OWN session plus a safe discovery set.
  So today an agent's credential is both session-BOUND and scope-LIMITED.
- Gateway: `AuthMiddleware` accepts a shared machine token, a browser cookie, or a per-device key
  from enrollment (`GatewayDeviceKeyStore` stores one install id and one key). None of these is
  bound to a session, and none is scope-limited.

**Consequence.** Repointing the tools at the Gateway with the credential that exists today means
handing every agent process the Director's own Gateway key - authority over the entire account, on
every machine. That is a strictly larger hole than the port this mission removes, so it is not an
option. Architect ruling 1 stands and now has a phase of its own.

**Phase 1b (new): session credentials on the Gateway.** Issue a session-bound key at session launch,
verify it in the Gateway's auth path, limit what it may call, revoke it when the session is reaped.
The pieces exist to build on - the Director already mints session-bound tokens, the Gateway already
has a key store, tenant resolution, and a Director connection to deliver a key over. What is new is
the session-key record, the verification branch, and the route guard.

This was not a surprise about WHAT the mission is for; it is the work the mission implies. Absorbed
rather than escalated. If detailed scoping shows it is a security build in its own right rather than
a phase, that is the point it goes to the owner.

## Running state

- 2026-08-03: mission chartered, worktree cut from origin/main at 214b15819, brief written.
- 2026-08-03: Phase 1 done. Gateway has no session-scoped credential; Phase 1b added.

## Carried over, not part of this mission

A two-line fix to the same port-picking code exists on branch `fix/port-probe-loopback` (worktree
`devthrottle-portprobe`): it stops the popup without removing the port. Full local gate green (9
projects, 10,192 tests) and reviewed twice by Codex. It is NOT merged. Phase 5 deletes the code it
touches, so it is superseded by this mission - it matters only if the owner wants the popup fixed
before the mission lands.
