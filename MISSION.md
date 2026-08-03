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
| 1b | Session credentials on the Gateway (discovered in Phase 1) | DONE - see PHASE-1B-REPORT.md |
| 2 | The command line tools talk to the Gateway | DONE - pass mark MET, see PHASE-2-REPORT.md |
| 2b | Owner widening, the write paths, and the inspection fixes | in progress |
| I1 | Independent inspection of 1b + 2 (Codex) | DONE - 8 proved defects, 4 high |
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
- 2026-08-03: Phase 1b done and pushed. Architect rulings on it:
  - **Spawn and machine-launch stay in the allowed set.** UPHELD. This mission removes a door; it
    does not change what an agent may do. Narrowing capability here would break fleet workflows and
    would be scope the owner did not charter.
  - **The launch window is accepted for now, and Phase 2 must test it.** The key is registered but not
    awaited, so a slow Gateway and a fast agent could in principle produce one refused first call. It
    is refused loudly, never silently downgraded, which is the behaviour we want. Phase 2 exercises
    this credential end to end for the first time and must fault-inject a slow Gateway. If a refused
    first command is reachable in practice, it is fixed there - not papered over with a retry that
    would reintroduce a second path.
  - The other two recorded gaps - no end-to-end run, and nothing consuming the calling session id -
    are correct for a phase that builds a credential before anything uses it. Both close in Phase 2.
  - `MISSION-PLAN.md` written; the dangling reference was the Architect's error, not the Manager's.

- 2026-08-03: Phase 2 scoping corrected the Architect's premise. "No Gateway surface is needed" was
  wrong in three places, found by the Manager, not by me:
  - `fleet/send` and `fleet/ask` FRAME the message with the sender's name and machine and run the
    message steward on the Director; `fleet/broadcast` resolves the sender's TEAM on the Director
    before calling the Gateway's fanout, which only takes an explicit id list.
    **Ruling: all of it moves to the GATEWAY, not into Python.** The standing law here is that the
    client is dumb and the Gateway owns every ruling - a sender name, a steward decision and a team
    roster are verdicts. Giving Phase 1b's stamped session id its first consumer is a bonus, not the
    reason.
  - `cc-devthrottle browser` has EIGHT Director-local routes and no Gateway equivalent at all.
    **Ruling: build them as Gateway routes that push down the existing tunnel**, so the Director still
    does the local work. New surface, no new capability, no security change. No local exception.
  - `cc-history` and the selftest spawn already call Director routes that no longer exist, so two
    commands are dead in production today. **Ruling: pre-existing defect, not mission scope.** Excluded
    from the phase pass mark, recorded precisely enough to be filed separately.
- 2026-08-03: **Security finding worth the QA report, not a footnote.** Skill, workflow, schedule and
  mission operations ALREADY bypass the Director and present the account-wide gateway token straight to
  the Gateway. The hole Phase 1b was chartered to prevent therefore already exists in production on
  those paths, and the session key closes it. The mission delivers a real security fix, not only a
  removed port.

## Carried over, not part of this mission

A two-line fix to the same port-picking code exists on branch `fix/port-probe-loopback` (worktree
`devthrottle-portprobe`): it stops the popup without removing the port. Full local gate green (9
projects, 10,192 tests) and reviewed twice by Codex. It is NOT merged. Phase 5 deletes the code it
touches, so it is superseded by this mission - it matters only if the owner wants the popup fixed
before the mission lands.

## Owner ruling, 2026-08-03: agents configure, agents do not enrol

The owner's words: the point of having agents is not to have to use the interface for most things.
Once the agents are set up, you maintain and configure through them.

**The principle, so it does not have to be re-decided per phase: an agent may change how the product
BEHAVES. It may not change WHO IS ALLOWED IN.**

Allowed to a session key:
- Director and application settings. This is the owner's ruling and it reverses the Phase 1b guard,
  which refused the whole `/directors/{id}` surface except two sub-paths.
- Handovers - content agents produce, and moving a session needs it.

Refused to a session key:
- Device registration and enrolment. The owner named this one himself. A credential that can enrol a
  device can admit a NEW device, which is not configuration - it is the boundary itself.
- Account-level identity: billing, ownership, who the account belongs to.
- Force-killing a Director. Agents already have a clean way to end sessions
  (`request-deletion`). Flagged to the owner as a line he may want moved.

Consequence for the build: `SessionKeyGuard` widens. The guard's own comment - that the rest of the
`/directors` surface "is the owner's ... and stays refused" - is now wrong and must be rewritten, not
merely edited around. An allow list whose stated reasoning contradicts its contents is worse than a
wrong entry, because the next reader trusts the prose.

## Phase 2 accepted, 2026-08-03 - Architect rulings

**Pass mark met.** 18 commands green from inside a real session holding a real session key, with the
Director's agent routes switched off, on an isolated Director and Gateway both built from this branch.
The switch was probed with a credential the Director ACCEPTS, so a removed route answers 404; an
earlier probe with an invalid credential returned 401 for everything including routes that still
existed, which is an auth refusal standing in for absence and proves nothing. That distinction is why
the result is believable.

1. **The launch window is SETTLED, not deferred.** No fix. All three candidates are worse than the
   window: a command-line retry is a second path wearing a different hat; awaiting registration inside
   session creation makes every launch wait on the network; and having the Gateway ask the Director
   about an unknown key puts a second lookup on the credential check, the one path that must stay
   cheap and must not depend on the Director being reachable. A race that fails LOUDLY, heals itself,
   and whose other side is an operating system starting a process is the right trade.
2. **Deleting `CleanInstallCliAuthenticationTests` is backed by the Architect**, on the record, so it
   is never read later as a test dropped to get green. It drove the command line authenticating
   against a Director by resolving the machine secret - precisely the mechanism this phase removes. No
   version of it could pass without reinstating what was deleted. A test that can only pass by undoing
   the change is not coverage, it is a fossil.
3. **The accepted cost was overstated to the owner and is corrected.** The brief said every agent
   command becomes a round trip and gets slower. Measurement shows the fleet reads were ALREADY a
   round trip plus a local hop, so most commands get FASTER (870ms against 1023ms) and only
   genuinely-local reads pay (a session's own terminal, 321ms to 828ms, against a hosted Gateway
   across the internet). The correction has been passed to the owner.

Still unproven and reassigned to Phase 2b: the seven browser WRITE verbs, a single-target message into
a real agent, and prompt/interrupt/compact/mission-attach/session-done end to end.

## The carried-over popup fix: independently approved, still parked

2026-08-03. A third review pass on `fix/port-probe-loopback` returned APPROVE, and the method is why
it counts: bin and obj were force-removed for both affected projects before EVERY matrix run and again
before each isolated rerun, so no result rode a stale assembly - the hazard that produced three wrong
diagnoses earlier in this work. Baseline 24/24 green; the scan line reverted turns exactly one test
red; the bind reverted to `IPAddress.Any` turns exactly one different test red; reverted to
`IPAddress.IPv6Any` the same; restored 24/24 green. Each intentional failure also failed alone after a
clean, so none was suite contention. Fault-injected detector validation in both directions.

**Disposition: parked, NOT merged.** The Architect's authority on this mission covers landing the
MISSION's work on main. This branch is explicitly not mission work, so merging it is the owner's call
and has not been given - he declined twice and then said the fix is unnecessary once the port goes.

**The one thing that changes this:** Phase 5 is several phases away. If any release ships before Phase
5 lands, that release carries the first-launch popup - a new user's setup wizard appearing frozen -
and this approved two-line change is the only thing that would have prevented it. Flag at release
time, not before.

## Independent inspection of Phases 1b and 2 - 2026-08-03

Codex, different family, own detached worktree, told to attack the mission's own reports rather than
believe them. **Eight proved defects, four high.** Full report: `INSPECTION-1B-2.md`. The mission's own
suites - 330 Python, 98 session-key, 14 reachability - all passed throughout. That is the point.

The four high findings, all of which the mission's own testing missed:

1. **The guard and the real routes disagree, so agents silently lose abilities.** Creating or updating
   a skill or workflow, and nearly every schedule command, return 403. This is precisely the owner's
   stated fear, present in the code. Its test passed because it pins a route that does not exist
   (`POST /gateway/skills/move-session/draft`) instead of the `PUT` the client sends.
2. **Registration is globally keyed on a bare session id.** A Director can register a session id owned
   by another Director or another TENANT and commandeer the row - identity takeover within a tenant,
   registry corruption across them. The hub derives the tenant correctly from the tunnel and then
   never checks the session belongs to the bound Director.
3. **Reap revocation is lossy.** The local hash is forgotten BEFORE the revoke is sent; the revoke
   returns silently when the tunnel is down; the send is unawaited and swallowed on failure; and the
   reconnect replay reads a set the entry was already removed from. A reaped session's key keeps
   working until expiry - contradicting Phase 1b's own claim. Expiry is also taken from the Director's
   clock with no Gateway-side maximum.
4. **The Python transport forwards the session bearer across a cross-origin redirect**, proved at
   runtime with two loopback servers. A redirect can disclose the credential to another origin.

**The lesson, recorded because it will recur.** All four share one root: every suite validated its own
piece in isolation and nothing crossed the boundary between them. The Python tests mock HTTP; the
guard tests drive the guard directly; neither crosses the method-and-path matrix where the defect
lives. The inspector's sharpest observation is that deleting the production revoke call leaves all 98
session-key tests green - which says what those tests were worth.

**Architect ruling:** all eight go to the Phase 2b Manager, guard widening folded into finding 1 since
it is the same file, and **every fix must carry a test that fails without it**. No finding is closed
by argument.
