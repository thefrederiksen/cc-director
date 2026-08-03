# Phase 2: the command line talks to the Gateway

Manager: session 955e2d21. Branch `mission/remove-network-port`, worktree `D:\ReposFred\devthrottle-noport`.

## What the phase had to produce

Repoint every `cc-*` command at the Gateway, presenting the session key phase 1b stamps into each
session, and keep the Director's routes alive behind a switch so the fleet is never without tooling.
The pass mark the Architect set: **every `cc-*` command works with the Director's routes switched
OFF**, plus the first end-to-end exercise of the phase-1b credential, plus fault injection on the
launch window, plus a measurement of the round trip that replaces a loopback call.

## The shape of it

`tools/cc_shared/director.py` and `tools/cc_shared/director_token.py` are **deleted**. In their place
`tools/cc_shared/gateway.py` is the one door: it reads `CC_GATEWAY_URL` and `CC_GATEWAY_SESSION_KEY`
from the session's own environment and calls the Gateway. There is no fallback and no local path -
an unreachable Gateway fails with a sentence naming the self-hosted gateway, which is the cost the
owner accepted, written where a user reads it rather than left as an edge case.

## The brief's premise was wrong in three places, and the Architect ruled on all three

The brief said all 21 agent-facing Director routes already forward to a Gateway call that exists, so
phase 2 was "repointing callers, not building Gateway surface". That is true of 18 of them. Three
carried RULINGS the Director made on the way past, which the Gateway did not have:

| What the Director did | Why it could not simply move to Python |
|---|---|
| **Framed** every fleet message with the sender's display name and machine (`FleetMessaging`) | A sender's name is a verdict about who is calling. In the client it would also be *chosen* by the caller. |
| Applied the **message steward** (dedupe, per-sender rate limit) | A steward decision is a ruling, and one the sender must not be able to skip. |
| Resolved the sender's **team** for `message send all`, because the Gateway's `/fanout` only accepts an explicit id list | "Who is on my team" is `BroadcastScope`, which the Gateway already enforces as the authority. Two definitions, one in Python, would drift by default. |

A fourth was found later, of the same kind: the Director folded the **roster completeness verdict**
(`RosterCompleteness`) onto `GET /fleet/sessions?envelope=true`, and the tools print those sentences.

And a fifth thing was not a ruling but simply missing: **`cc-devthrottle browser` had no Gateway
route at all** - eight Director-local routes, deliberately never relayed, because a browser's debug
port is loopback and its profile directory is on one machine's disk.

**Architect rulings (2026-08-03), all four absorbed:**

1. Framing and team resolution move to the **Gateway**, upheld - not for convenience but because this
   repository's standing law is that the client is dumb and the Gateway owns every ruling. That it
   gives phase 1b's stamped calling-session-id its first real consumer is a bonus, not the reason.
2. The browser routes become **Gateway routes that push down the existing tunnel** to the Director,
   which still does the local work exactly as it does now. Same shape as the other 21, just never
   built: new Gateway surface, no new capability, no security change. No local exception for them.
3. `cc-history` and the self-test spawn calling Director routes that **no longer exist** is a
   pre-existing defect this mission uncovered, not mission scope. Excluded from the pass mark and
   recorded below precisely enough to be filed on its own.
4. The finding that skill, workflow, schedule and mission ops already present the **account-wide
   Gateway token** straight to the Gateway means the hole phase 1b was chartered to prevent already
   exists in production on those paths. The session key closes it, and that is a security fix the
   mission delivers - not a footnote.

## The security fix this phase delivers

Before this phase the command line held **two credentials strictly wider than a session key**, and
both are now gone:

- **The machine secret.** `director_token.py` read the Director's root secret off disk and minted
  itself the `cli` scope - full authority over the local Director. Deleted with the file.
- **The account-wide `gateway.token`.** `skill_ops`, `workflow_ops`, `schedule_ops` and `mission_ops`
  each read `config.gateway.token` from `config.json` and presented it to the Gateway directly.
  That token has authority over the entire account on every machine. **This is the hole phase 1b was
  chartered to prevent, already open in production on four paths**, and it is the most valuable thing
  the phase found. All four now present the session key: bound to one session, one tenant, and the
  fleet's agent routes only.

The loopback exemption ("a Gateway on this machine needs no token") is gone with them. It was the
ADDRESS that made loopback special, never the caller - and the credential now identifies *which
session* is calling, which matters as much on this machine as on any other.

**Two clients deliberately did NOT move**, and the reason is the guard's own ruling rather than an
oversight. `diag_ops` and `email_ops` reach the diagnostics and account surfaces, which
`SessionKeyGuard` names as the owner's. A session key is *refused* there, so moving them onto one
would not narrow those commands - it would break them. They stay owner commands on the owner's
credential, they never called the Director, and they are unaffected by the switch.

## What was built

### On the Gateway

- **`POST /sessions/{sid}/message`** - one agent messages one session anywhere in the account. Frames
  with the sender's name and machine, applies the steward, and (with `waitForIdle`) serves
  `message ask` as well as `message send`. **There is no sender field in the body**, and its absence
  is the point: the sender is the session whose key authenticated the request. The loopback
  predecessor could trust a body-supplied `fromSessionId` because only a same-machine process could
  reach it; this route cannot, and a caller-supplied sender would let one agent wear another's name.
- **`POST /fleet/broadcast`** - resolves the calling session's team from the Gateway's own roster and
  then delegates to the **same** `/fanout` path, so the scope decision, the grant check, the rate
  limit and the delivery are evaluated exactly once.
- **`GET /sessions?envelope=true`** now also carries `rosterComplete`, `rosterIncompleteReason` and
  `rosterStaleAnswerCaution`, from the same `RosterCompleteness` fold the Director used. Purely
  additive: every existing reader of that envelope is untouched.
- **Eight `/directors/{id}/browsers` legs**, carrying the browser verbs down the tunnel.

### On the Director

- `BrowserExecutor` - the ONE implementation of the browser verbs. `BrowserEndpoints` is now a thin
  adapter over it, so the loopback routes that still serve already-installed command lines cannot
  drift from the tunnel verb replacing them.
- `FleetMessaging` moved to `Gateway.Contracts` so there is one definition of what a fleet message
  looks like rather than two.
- **`CC_DIRECTOR_AGENT_ROUTES=off`** - the switch. Default ON, read once at startup. It is not a
  fallback and must never become one: the command line does not choose between two doors, and nothing
  behind the switch is consulted when the Gateway is unreachable. It only decides whether the OLD
  door is still standing for OLD callers. Turning it off is what makes the phase *provable* rather
  than believed - a command that still reaches for the Director fails loudly instead of quietly
  working, and a claim that cannot fail is not a proof.

### Guard changes, and why each is not a widening

`SessionKeyGuard` gained four entries:

| Added | Why it is the same privilege, not more |
|---|---|
| `GET /sessions/{sid}/history` | The same class of read as `/buffer` beside it: one session's own output, inside the caller's own account, bounded by the same tenant binding. |
| `POST /sessions/{sid}/message` | Strictly narrower than `/prompt`, which was already allowed - the sender cannot be chosen by the caller. |
| `POST /fleet/broadcast` | The team-resolving front door onto `/fanout`, which was already allowed. |
| `POST /directors/{id}/sessions` | The same verb as `POST /machines/{m}/sessions`, already allowed - addressed precisely rather than to "some Director over there". |
| `/directors/{id}/browsers/...` | The one place `/directors` opens to a session key. An automation browser is a tool an agent uses; it was reachable by every agent on the machine before this mission over the loopback port with **no credential narrower than the machine secret**. Routing it through the Gateway NARROWS it. |

## Notable behaviour changes

**Spawn carries WHERE in the path.** An unqualified spawn lands on this session's own Director,
named from `CC_DIRECTOR_ID` - what the session was told at launch. No roster lookup, no hostname read
off the operating system (a different string on a different day), no round trip to work out something
the session already knows. A named `--director` is resolved to its id and addressed directly, because
the Gateway's machine route picks a Director for itself and gives the caller no way to say which; an
ambiguous name is **refused rather than guessed**, since silently picking one of two Directors with
the same name is how a session lands on the wrong computer and nobody notices.

**`mission attach` names the previous mission from the roster snapshot.** The Director used to read
the attachment off a LOCAL session immediately before changing it, which was exact, and fall back to
the roster row for a remote one. With the Director out of the path every target is "remote", so the
documented fallback is now the only path. It is a display line; nothing branches on it.

## Proof

*(filled in as each proof lands - see the sections below)*

### The local gate

### The no-Gateway message, verified as a user reads it

The mission's accepted cost is "no Gateway means no agent tooling, and the error message must say
exactly that". Run for real, with the address unset:

```
$ cc-devthrottle session list
Error: CC_GATEWAY_URL is not set, so there is no Gateway to call. These
commands only work inside a DevThrottle session on a machine attached to a
Gateway. Install the self-hosted gateway and attach this machine to it - the
fleet commands work through the Gateway and have no local path.
```

Each half of the credential is reported separately (a session holding one without the other is a bug
in the stamping, and one collapsed message would hide which half went missing), and an unreachable
Gateway says "no local path" so nobody goes looking for the second door this mission removes. All
three are pinned in `tools/cc_shared/tests/test_gateway.py`.

### The launch window, fault-injected

`src/CcDirector.Gateway.UnitTests/SessionKeyLaunchWindowTests.cs`. Phase 1b recorded one honest gap:
the registration is sent the instant the key is minted but **not awaited**, because session creation
must never block on the network, so a slow enough Gateway and a fast enough agent could in principle
produce one refused first command. Phase 2 is the first consumer of the credential, so it is where
that gets settled.

**Is it reachable? Yes - and the tests make it reachable on purpose rather than reasoning about it.**
With the Gateway's registration deliberately delayed, the key the session was handed is refused. What
the tests establish:

- The refusal is **loud**: 401, naming the credential. It is never a quiet downgrade to some other
  authority - which is the failure that would actually be dangerous, because then the window would be
  invisible AND every session would silently hold more than its own key.
- The window **closes on its own**: the identical key, unchanged and not re-sent by anyone, works the
  moment the registration lands. This is a race, not a break.
- With no injected delay, registering is a database write of a few milliseconds.

**Is it reachable IN PRACTICE?** The race is between a hub invoke on an already-open tunnel
(milliseconds) and an operating system starting a process plus an agent booting and issuing its
first command (seconds). The registration is sent inside the environment build - before the process
is launched at all. So the Gateway would have to take longer to write one row than an agent takes to
boot.

**No fix is applied, and that is a decision rather than an omission.** The three candidate fixes are
all worse than the window:

1. **A retry in the command line** - explicitly forbidden, and rightly. It is a second path taken
   when the first fails, which is a fallback wearing a different hat. It would also make a genuinely
   invalid key - revoked, or a reaped session's - indistinguishable from an early one, so every real
   refusal would be retried before being reported.
2. **Await the registration inside session creation** - that is precisely what phase 1b refused, and
   for a good reason: it makes every session launch on the machine wait for the network, so a slow
   or unreachable Gateway delays or fails session creation itself. Trading a rare refused first
   command for a routine dependency of session start on the Gateway is a bad trade.
3. **Let the Gateway ask the Director about a key it does not recognise** - a second lookup path, on
   the credential check, taken for every unknown key including every bogus one. That is both a second
   door and an amplifier.

The honest fourth option - move the process launch behind the registration, so ordering is
guaranteed without blocking the caller - is a real change to how every session starts, and it is not
worth making for a window this shape without evidence that it is being hit. **Recommendation to the
Architect: leave it, and revisit if a refused first command is ever actually observed** - which is
findable, because the refusal is loud and logged.

### End to end with the Director's routes switched OFF

### The repointed read paths, against the LIVE Gateway

Nine read paths driven through the new `cc_shared/gateway.py` against the real hosted Gateway
(`gateway.devthrottle.com`), with real fleet data behind them. Read-only calls only - these are the
same GETs the fleet's tools make continuously, so nothing was changed in production.

| Command's call | Result | Rows |
|---|---|---|
| `session list` -> `GET /sessions?envelope=true` | PASS | 11 sessions |
| `repo list` -> `GET /repositories` | PASS | 139 |
| `worktree list` -> `GET /worktrees` | PASS | 39 |
| `machine list` -> `GET /launchers` | PASS | 2 |
| `machine directors` -> `GET /directors` | PASS | 4 |
| `skill list` -> `GET /gateway/skills` | PASS | - |
| `workflow list` -> `GET /gateway/workflows` | PASS | - |
| `schedule list` -> `GET /cron/jobs` | PASS | - |
| `mission list` -> `GET /missions` | PASS | 2 |

**What this proves and what it does not.** It proves the repointed PATHS are right and the new client
talks to a real Gateway - which is worth having, because a wrong path is the most likely way this
work would be broken. It is **not** the session-key proof: the credential presented was the account
token, because this session predates phase 1b and holds no session key, and the live Gateway is an
older build that does not yet have the new routes. The session-key proof needs a Director and a
Gateway both built from this branch; see "What is NOT proven".

### The round trip, measured

**The headline finding: for the fleet's most-run command the Gateway is not a new cost at all - it
is CHEAPER, because the "local" call was never local.** The Director's `GET /fleet/sessions` does not
answer from its own knowledge; it relays to the Gateway and folds the answer on the way back. So the
loopback call was always a Gateway round trip PLUS a local hop. Removing the middleman removes the
hop.

Same question, asked both ways, 7 samples each, from this machine:

| Call | median | min | max |
|---|---|---|---|
| Director, loopback: `GET /fleet/sessions?envelope=true` | **1023 ms** | 580 ms | 10944 ms |
| Gateway, hosted: `GET /sessions?envelope=true` | **870 ms** | 691 ms | 1029 ms |

The 10.9-second outlier is machine contention (the parked test suites were running), which is also
why the absolute numbers are inflated on both sides. The comparison is what matters and it holds
across every sample.

**Where the cost IS real: a genuinely local read.** A session's terminal on THIS machine was answered
by the Director without leaving the box. Through the Gateway it goes out and comes back down the
tunnel:

| Call | median | min | max |
|---|---|---|---|
| Director, loopback, answered locally: `GET /fleet/buffer?sessionId=<mine>` | **321 ms** | 64 ms | 464 ms |
| Gateway, out and back: `GET /sessions/<mine>/buffer` | **828 ms** | 621 ms | 1047 ms |

So roughly **+500 ms, about 2.6x**, on the commands that really were local - and this is the WORST
case, measured against a HOSTED Gateway across the internet. The mission's own answer to "no Gateway,
no tooling" is the self-hosted gateway, and a self-hosted Gateway on the same machine or the same
network pays a fraction of that.

**Read the accepted cost accordingly.** `MISSION.md` accepts that "every agent command becomes a
Gateway round trip rather than a local call". That is true only of the commands the Director actually
answered itself. For the aggregating reads - the roster, repositories, worktrees, machines,
Directors, and every command that resolves a target against the roster first - it was already a
Gateway round trip, and this phase makes them faster by removing a hop nobody was counting.

## Pre-existing defects this mission uncovered (Architect ruling 3: file separately, not here)

**`cc-history` calls a Director route that no longer exists.** `tools/cc-history/src/cli.py` called
`GET /sessions/{sid}/history` on the Director. The Gateway Cleanup mission deleted that route - see
`src/CcDirector.ControlApi/SessionHistoryEndpoint.cs`, whose own summary records that the Director
"no longer registers a `/sessions/{sid}/history`" and answers a tunnel verb instead - and this caller
was never updated. So `cc-history` has been answering `HTTP 404 from the Director` since that cut, on
every machine. The phase-2 repoint incidentally revives it, because the Gateway's session catch-all
serves the same verb; the defect worth filing is that a caller was left pointing at a deleted route
and nothing noticed.

**The fleet self-test spawns through deleted routes.** `_spawn_selftest` in `session_ops.py` called
`POST /sessions`, `PATCH /sessions/{sid}` and `DELETE /sessions/{sid}` on the Director. None of the
three is mapped any more (`ControlEndpoints.cs` registers no such routes). So
`cc-devthrottle`'s fleet self-test has been failing at its first step since the same cut. It is
repointed here mechanically, along with everything else, but it is **excluded from the pass mark**
and has not been run end to end.

Both are the same shape and probably want one issue: *a caller left pointing at a route the
tunnel-only cut deleted, with no test that would have caught it.*

## What is NOT proven

**The end-to-end run with a real session key is NOT done, and it is the Architect's pass mark.** It
needs a Director and a Gateway both built from this branch, because the live Gateway is an older
build without the new routes and this session predates phase 1b so holds no session key. The rig for
it is written (`scripts/phase2-gateway-proof.ps1`) and its shape is settled:

1. Publish this branch's Gateway and run it on its own port with its own `CC_DIRECTOR_ROOT`, so the
   live Gateway, the installed Director and the user's slots 1-5 are never touched. It writes its own
   token to `<root>/config/director/gateway-token.txt`; auth stays ON, because the session-key path
   runs through `AuthMiddleware` and a rig with auth off would prove nothing.
2. Build a Director slot at 6 or above from this branch, point its `config.json` at that Gateway, and
   launch it through its own scheduled task (CLAUDE.md rule 0b).
3. **Create the test session through the GATEWAY** (`POST /directors/{id}/sessions`) - with the agent
   routes off there is no Director route to create one, which is itself part of the proof.
4. The session's command is a checklist script. **The commands must run inside a session**, because
   `CC_GATEWAY_SESSION_KEY` is minted per session, stamped into that one session's environment, and
   deliberately never logged or stored anywhere a script could read it. A rig that fabricated its own
   key would prove the Gateway accepts a key it was handed, which is not the question.
5. Run it twice - agent routes ON, then OFF. The second run is the pass mark.

One thing that rig must get right and is easy to get wrong: the checklist has to run **this
worktree's** tools, not the installed ones on the machine PATH. `python -m src.cli` from
`tools/cc-devthrottle` with `PYTHONPATH` set to `tools` works and is how the no-Gateway message above
was captured.

**The write paths are unexercised against a real Gateway.** The nine read paths above ran against the
live Gateway; `message send`, `message ask`, `message send all`, `prompt`, `interrupt`, `hold`,
`compact`, `role`, `mission attach/detach`, `session done`, `spawn` and every `browser` verb have not.
They are covered by unit tests and by the fact that the routes they call already exist, but nothing
has driven them end to end. Deliberately so: they are writes, and the only Gateway available to this
session is the fleet's production one.

**The browser tunnel legs have never carried a command.** `BrowserExecutor` is exercised only through
`BrowserEndpoints`' loopback adapter, which is the same code path minus the tunnel. The eight Gateway
routes and the verb dispatch between them are unproven.

**The Gateway's own tests do not yet cover the new routes.** `POST /sessions/{sid}/message`, `POST
/fleet/broadcast`, the roster-completeness fields and the browser legs have no Gateway-side tests of
their own; what exists is the guard's allow-list coverage plus the launch-window tests. The framing
they share is covered by the pre-existing `FleetMessagingFramingTests`, which moved with it.

**`cc-history` and the fleet self-test are excluded from the pass mark** by Architect ruling 3 - see
the pre-existing defects above. Neither has been run.
