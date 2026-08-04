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
| 2b | Owner widening, the write paths, and the inspection fixes | DONE - see PHASE-2B-REPORT.md |
| I1 | Independent inspection of 1b + 2 (Codex) | DONE - 8 proved defects, 4 high |
| 3 | Session hooks stop needing an API | DONE - see PHASE-3-REPORT.md |
| 4 | Lifecycle off HTTP | DONE - see PHASE-4-REPORT.md |
| 5 | Delete the Director's listener | DONE AND PROVEN - see PHASE-5-REPORT.md |
| 6 | Delete the launcher's listener | DONE AND PROVEN - see PHASE-6-REPORT.md |
| 7 | The guard test | DONE - folded into 6; dependency guard proven red under indirect reintroduction |

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

**SUPERSEDED - 2026-08-04, verified by the successor Architect against origin/main:** the fix DID
land on main as `1677b1b0d` (pull request #2447) and shipped in v1.9.8 (verified by tag
containment, not prose). The flag above is therefore discharged: no release in the gap carries the
popup. The `devthrottle-portprobe` worktree and `fix/port-probe-loopback` branch are now merged
leftovers, removable at the next repo convergence.

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

## The landing criterion is COMPARATIVE, not absolute - 2026-08-03

A control run demanded by the Architect settled a "pre-existing contention" claim and found something
larger. The mission's PARENT commit, run three times unchanged, produced **0, then 4, then 2 failures
across six different tests, none repeating**.

So this repository's local gate is not a reliable pass-or-fail signal. A green run on an unchanged
commit is luck; so is a red one. That matters to this mission directly, because "the local gate is
green" is the criterion the whole fleet merges on - including whatever the Architect eventually lands.

**Ruling: a run on a mission commit counts only against a run on its PARENT. A failure is ours only if
it does not also appear on the parent. THE PARENT MUST BE RUN MORE THAN ONCE** - a refinement from the
Manager that is better than the Architect's original wording, and it is not a detail: parent run one
here was GREEN, so a single-run control would have convicted this mission of a regression it did not
cause. A one-run control on a flaky suite is itself a coin toss.

The evidence is ten failures across six runs on two commits, ten DISTINCT tests, zero repeats, spread
across stats, speech, wingman, morning-report, voice-upload and session-key-auth. The spread is what
rules out the comfortable version of this finding - that there is a known bad set to discount. Absolute greenness is not available here and pretending
otherwise would mean either shipping on noise or chasing it forever.

**This is the SECOND independent reliability defect this mission has found in the fleet's own gate.**
The first was stale assemblies on incremental builds, which serve the previous code while reporting
success and cost three consecutive wrong diagnoses; the fix is deleting `obj` and `bin` for any
project whose result is to be trusted. Both belong in the QA report as findings about the tooling
every mission depends on, not as footnotes about a flaky test.

## Mission record, and a product defect found while creating it - 2026-08-03

The owner asked for the Architect and Manager sessions to be combined into a Mission. The record is
created: **"Remove the network port", id 761ec285**, and `cc-devthrottle mission list` returns it.

**Attaching the two RUNNING sessions FAILED, and the failure is a product defect, not a mistake in the
attempt.** `POST /sessions/{id}/mission` answers `502` with
`unknown mission '761ec285...'. Create it first with POST /missions.` - for a mission the SAME Gateway
returns from `GET /missions` seconds earlier, over the same base URL, with the same credential. The
Director has no `/missions` route at all (404 on GET and POST), so the advice in the error names an
endpoint that does not exist on the machine it is given to.

So one Gateway route cannot see a record another Gateway route lists. The likely cause is a scoping
difference between the two paths - the list route answering across a scope the attach route resolves
within - but that is a hypothesis, not a finding, and it should be diagnosed rather than assumed.

**Consequence for this mission:** future seats are attached at spawn with `--mission`, which is the
supported path. The two already-running sessions stay unattached until this is fixed.

**Worth filing on its own.** The error is the dangerous kind: it is specific, confident and actionable,
and the action it names is impossible. A user following it looks for a route that is not there and
concludes their install is broken.

## Phase 3 accepted - 2026-08-03

**The number that matters most in this mission so far: the snapshot design, injected as a fault, left
48 of 52 tests GREEN.** The Architect's Phase 3 correction - that the preamble must be MAINTAINED, not
snapshotted at launch - was not a stylistic preference. A design that serves a user their old injected
text after they edit it, and hides newly published skills, would have passed almost the entire suite.
It never looks broken; it is just wrong.

**A real defect found outside the brief:** `FileSystemWatcher` silently drops roughly one notification
in five, with the file present and complete and no Error event raised. Fixed by making a 2-second
sweep the delivery guarantee and the watcher only a latency win - the correct shape, because the fast
path must never be the correctness path. This concerns a mechanism used well beyond this mission and
belongs in the QA report.

**The comparative criterion, applied properly for the first time end to end.** Mission arm 0/62/1/0/1,
parent arm 1/2/1. Every failure on both arms sits in a suite this phase adds nothing to, and
`SuggestionEmailComposerTests` fails on BOTH arms - which is what makes the conclusion safe rather than
merely convenient. Both parked suites green on the phase commit.

Architect rulings on the open items:
- **60-second self-healing preamble window: ACCEPTED.** Bounded, self-correcting, loudly logged. The
  alternative couples a rewrite to every store write, for a staleness no human can perceive between
  hook fires.
- **Preamble file isolation weaker on paper: ACCEPTED and must be stated as UNCHANGED, not improved.**
  `ControlApiHost` already documents that this was never an operating-system sandbox.
- **macOS and Linux unproven: a REAL hole, recorded OPEN.** It predates the mission and is not closed
  by it. The QA report must say the mission was proven on Windows only.

## The 62-failure outlier, answered - and the discipline in how

The Architect asked why one gate run produced 62 failures when the parent arm produced 1 or 2, noting
that "all carried the documented signature" explains the KIND but not the COUNT.

Measured from the result file rather than reasoned about: **all 62 failures start AND end inside 0.101
seconds across 9 classes, and 935 tests passed afterwards.** So it is ONE instantaneous assembly-wide
event, and the count is that instant's blast radius. Four of the nine classes lost every test with
start time equal to end time - the QUEUE behind the event, each failing on entry rather than by doing
anything - and with four parallel threads only a handful can genuinely be mid-query, which is what the
five partly-hit classes are. The parent runs caught the same event with 1 or 2 tests exposed; this one
caught it with 62. Same finding, different moment.

**And what it does NOT explain, stated by the Manager without being asked:** which class's disposal
fired at that instant, and why one blast radius is 62 and another is 1. A consistent observation is
not a diagnosis.

That last sentence is the standard this mission has been held to throughout, and it is worth more than
the answer it qualifies. Recorded so the next Manager does not re-derive the shape of this count.

## Phase 4 accepted - and the gate's flakiness is now ONE named defect

**The Architect's Phase 4 premise was corrected AGAIN, on evidence.** The brief said the version comes
from the exe on disk. Wrong, and consequentially: the exe is swapped BEFORE the new build starts, so a
disk read reports the new version whether or not anything came up, and the roll-back could never fire.
Observed - a hung 1.9.8 rolled back to 1.9.7 while disk said 1.9.8. Version now comes from the
registration the RUNNING process wrote, the only source that distinguishes started from installed.
That is the fourth Architect premise this mission has corrected, and the most consequential.

**The tie-break ruling, implemented and verified on the owner's own machine.** Ambiguous only when
claimants share an executable - the real defect, still refused - otherwise the installed application
wins. Before: `Ambiguous, status=none`. After: `Running, pid 34032, v1.9.7, 3 sessions`. The conflict
still travels on the answer and reaches the update display, and a test goes red if it is swallowed.

**Two things the Manager reported against itself**, both recorded because the habit matters more than
either item: detector validation found a HOLE rather than confirming coverage - removing a fail-open
guard reddened nothing, so it had no test at all - and it fault-injected an uncommitted tree, breaking
this mission's own law and destroying the work it was testing.

### The gate's unreliability is ONE fixable defect, not noise

Nine failures over seven runs on two commits, nine different tests, no repeats. **Eight share one
exception - `FileLogWriter.Enqueue` on a completed `BlockingCollection` - and the ninth is its sibling,
a disposed-object race on the test database.** `SuggestionEmailComposerTests` was an arbitrary victim
in Phase 3 and again here. **An arbitrary victim recurring by chance is exactly what one shared
teardown race looks like from a distance** - and the moving victim is precisely why it kept being
written off as flakiness.

This meets the Architect's own independent finding earlier in the mission, where the same exception and
stack explained a different suite's flake. Two lines of evidence, one cause.

**Ruling: NOT fixed here.** A race in the logging teardown deserves its own change with its own proof,
not smuggling in beside a port removal. Filed with the evidence. But it belongs in the QA report as a
fleet-wide finding, because every mission in this repository currently merges on a signal this defect
corrupts.

## macOS: the regression was REAL, and proving it caught it - 2026-08-03

The Architect refused to ship Phase 4 to macOS unproven, on the grounds that lifecycle WORKED there
over HTTP and now depended on code never executed on that platform. The owner made a Mac mini
available; a prover was seated on it. **It found a blocking defect within the hour.**

**Every launcher-to-Director signal is silently lost on macOS, in both directions.**
`LifecycleSignal.UnixRequestPath` derives the request-file path from each process's OWN redirected
`CC_DIRECTOR_ROOT`, while the name uses the SHARED root - so the Director polls its instance home while
the launcher writes to the shared root, and neither ever sees the other. Consequences on macOS:
every stop is a 20-second stall then a force-kill leaving a phantom crash journal; every update applies
by force-kill; and "install it now" REPORTS SUCCESS while nothing happens. Not shippable.

The rest of Phase 4 does work there: the locator, hold/apply/rollback, version-from-registration and
crash-restart all passed.

### Why this could not have been caught on Windows

**Windows uses kernel named events, so its two processes never have to agree on a FILE PATH at all.**
The Unix arm is the only place where agreeing on a path is load-bearing. So a shared test suite could
be entirely green while one platform's mechanism was completely inert. This is the strongest example
in the mission of a proof that does not transfer: the tests were not weak, they were asking a question
that only exists on one platform.

**The fix is one path expression. The important deliverable is the MISSING CROSS-PROCESS TEST** - no
test ever made two processes agree on where the signal lives, which is why this reached a prover rather
than a suite. A fix without that test merely removes today's instance and leaves the next path change
equally undetectable.

**Ruling: the prover fixes it.** It is a prover, not the independent inspector, so nothing is
compromised - and it is the only seat that can verify a defect invisible on the other platform.

## macOS regression CLOSED - 2026-08-03

Fixed and pushed (`f09d55ff`). `UnixRequestPath` now derives from the same shared root the signal NAME
is already scoped by, so the two processes finally agree on where the signal lives.

**The detector is three pieces, each honest in its own comment about what it proves on which
platform** - a cross-process test that runs a listener child and a raiser child with one end redirected
exactly as a real Director redirects (failing without the fix on macOS and Linux, proving only kernel
delivery on Windows, and SAYING so), plus a test pinning the path derivation as a VALUE so a regression
reddens EVERY platform including Windows. That second piece is what actually prevents recurrence: the
cross-process test can only fail where the mechanism is load-bearing. All three were shown red under an
injected revert before any was trusted.

All four macOS proofs re-ran green with no Gateway: launcher stop 385ms and clean, where it had been a
20-second stall then a force-kill; install-it-now delivered; update applied in 2s with a graceful stop
inside; a dead build rolled back; crash restart and right-Director-of-several both pass.

**CLOSED on observation, not argument.** Windows verification came back green from a clean worktree with both target frameworks run: 16 of 16 on the lifecycle signal filter including both cross-process tests and the derivation pin, 114 of 114 Launcher tests on each framework, 10 of 10 on the stopper. Before that arrived, the prover had labelled its own gap correctly and refused to close it: "Windows-unregressed is
an argument from CONSTRUCTION only." A fix that cannot plausibly touch the other platform is still
unproven there, and reasoning about why it is safe is not running it. A verifier is in flight.

**For the QA report:** this regression was found only because the owner made a Mac available and a seat
was sent to PROVE rather than to assume. Nothing in the shared test suite could have caught it -
Windows uses kernel events, so its processes never have to agree on a path at all. That is the argument
for doing the same again next time, and it belongs in the report as such.

### A side-finding worth as much as the fix

The same Windows run discriminated two OTHER open items: the coalescing pair and the backslash-names
test are green on Windows and red on macOS, which makes them **Unix-arm findings, not shared
flakiness**.

That matters beyond this mission. This repository's default explanation for an unexplained red has been
flakiness, and the mission has now twice caught that label hiding a specific cause - first the
log-writer teardown race behind most gate failures, now this. **A red that reproduces on one platform
and never on the other is not noise; it is a platform finding wearing noise as a disguise.**

## PHASE 5 COMPLETE - the Director is portless and proven - 2026-08-04

**Two Directors from this branch, alive and registered, own ZERO listening sockets** while holding 14
established OUTBOUND connections to their Gateway. Evidence: `docs/qa/phase5-noport/`.

**What makes it proof rather than an absence:** the same query, in the same instant, caught the owner's
own two Directors LISTENING on 7879 and 7881. The scan can see listeners. Zero therefore means zero,
not a broken query - the distinction this mission has been caught by four times.

Also proven: 17 of 17 `cc-*` commands green from a real keyed session whose own environment dump shows
`CC_DIRECTOR_API` and `CC_DIRECTOR_TOKEN` ABSENT. So the tooling works AND the address is genuinely
gone, not merely unused. The launcher's listener on 7900 is still present and the artifact says so
explicitly - that is Phase 6, and stating it is why the rest can be believed.

Two findings raised in their own right: a test that had been **vacuous for 22 days**, dated by running
it either side of the commit that broke it rather than described; and the rig hazard that the obvious
shim runs the INSTALLED command line rather than the branch, stated as a property of the setup because
two independent Managers hit it.

## ARCHITECT RESET - 2026-08-04

The founding Architect (`bc291ea4`) is standing down for context, not for cause. `ARCHITECT-HANDOVER.md`
in this directory is the successor's brief. Nothing important lives only in the old conversation.

## Successor Architect seated - 2026-08-04

Session `ae40057f` took the seat, read the four briefing files, and verified the live state against
the artifacts rather than the handover's wording:

- Phase 5 Manager `967c051d` is already gone from the fleet - nothing to retire.
- Phase 6 Manager `f5c7ebf9` is alive and working. Its branch `mission/remove-network-port-p6`
  carries the deletion commit (`f2c022e06` - launcher listener deleted, six verbs on the stream,
  registration file the only local surface, phase 7 dependency guard folded in and proven red
  against an INDIRECT reintroduction) plus a gate-flake fix (`f5d8c7791`). No phase report yet;
  the comparative gate run is presumed in flight. The branch is ahead 2, behind 13 of the mission
  branch, so the merge will not be a fast-forward.
- `QA-REPORT-DRAFT.md` is ALREADY committed on the mission branch in `d3bdb1b64`, byte-identical to
  the scratchpad copy the handover pointed at - that handover step was already done. Two lines in it
  are stale (Phase 5 marked outstanding; it is proven) and will be corrected in the finishing pass.

The founding Architect's sign-off added one instruction worth keeping verbatim: the QA report must
state POSITIVELY, with evidence, what an agent can still do - "a guard exists" is not an answer to
the owner's actual fear, which is agents ending up able to do less.

## Owner directive on Manager context - 2026-08-04

Long-context Managers are restarted, not run to exhaustion - context exhaustion kills a seat
quietly, mid-report. The standing practice, now explicit:

- Every Manager is retired at its phase boundary and the next phase gets a FRESH seat. Already the
  rule; the owner has re-affirmed it.
- When a Manager is mid-phase and what remains is more than mechanical, the Architect asks it for an
  honest headroom self-assessment and offers the handover. Applied to the Phase 6 Manager, whose
  answer was the model: comfortable headroom, about ten mechanical tool calls left, and an explicit
  commitment to STOP and write a compact handover if the remaining test result needs fresh
  investigation rather than digging with a depleted context.

## PHASE 6 ACCEPTED AND MERGED - the launcher is portless too - 2026-08-04

Merged as `44be0052d` (`mission/remove-network-port-p6` at `e8ece297d` into the mission branch).
`PHASE-6-REPORT.md` is on the branch. The whole product is now portless: no listener on the Director
(phase 5), none on the launcher (phase 6), and the phase 7 dependency guard stands - proven red
against an INDIRECT Kestrel reintroduction (helper in its own file, referenced by nothing), the
shape that defeated the previous source-text guard. The guard caught a real leftover on its first
run: ControlApi still carried the `Microsoft.AspNetCore.App` framework reference.

**Architect verification of the merge, not taken from the report:** the two sides carried code never
built together (phase 5's later test fixes against phase 6's audit updates), so the merged tree was
rebuilt from scratch (all obj/bin deleted) and the default gate run: build fully clean, 2 failures -
`SnoozeRegistryTests` and `SnoozeLandingObserverTests`, same instant, both carrying the documented
FileLog teardown-race signature, in code phase 6 never touches. Judged as the known fleet race under
the comparative criterion.

**The two "inherited" deterministic reds the Manager handed over are ALREADY FIXED on the merged
tree** - `GatewayDirectoryRegistrationTests.Register_rejects_missing_tailnet_endpoint` and the
`WingmanAskForwardingTests` 404 both fail on the Manager's parent (`4a2e6665e`) but pass 14/14 on
the merge, because phase 5's later commits (`fe14cc136`, `b7ca10ea6`) updated exactly those two test
files after the p6 branch forked. Settled by running them, not by adopting the Manager's hypothesis.

**Open items the landing must fold in (from PHASE-6-REPORT.md section 6):**
- `packages/client-core/src/api/schema.ts` is stale (still describes old `/launchers` shapes; no web
  client consumes them - verified by search). Regenerate against a running Gateway (`npm run gen:api`)
  before or at landing.
- Phase 6 not run on macOS/Linux; the surface is less platform-split than phase 4's (no kernel-object
  arm) but that is an argument, not a run. The QA report must state it.
- Mixed-version window (old launcher/new Gateway and the reverse) stated, not tested; the fleet ships
  in lockstep on this branch.
- The stopper's fail-closed strictness change (unreadable process list now refuses to certify a stop)
  is deliberate and flagged for review in the report.

**Incident recorded by the Manager against itself, kept visible:** its rig launcher overwrote the
owner's autostart Run key (caught and restored within minutes, self-healing on next installed-launcher
start). Standing lesson: a launcher rig must pass `--no-autostart`.

## INSPECTION 3 (phases 3-7) - FAIL, ten proved defects - 2026-08-04

Codex, different family, own detached worktree at the tip, told to attack the mission's own reports.
Full report committed as `INSPECTION-3-7.md`. **261 targeted tests passed, 0 failed, throughout.**
That is the third time this mission's green suites have sat on top of proved defects, and this time
the inspector says why in one line worth keeping: *several tests explicitly PRESERVE the defects, and
the phase 7 test cannot observe the defect in itself.*

**The two findings the Architect verified personally against the source before ruling** - because
each contradicts something the mission had already claimed:

- **Finding 10: the phase 7 guard does not guard.** Its premise - that a process cannot listen
  without ASP.NET hosting machinery - is FALSE. `IsListenSurface` matches only ASP.NET, hosting and
  Kestrel assembly names, while `TcpListener` and `HttpListener` are base class library types needing
  none of them. The inspector built a project with ordinary SDK references, bound `127.0.0.1:50578`,
  and its reference list contained zero ASP.NET assemblies. **This repository already uses both BCL
  listeners in `CcDirector.Core`, which the Director and launcher both reference.** So the mission's
  own headline guarantee - "nothing CAN listen again" - is not enforced by the guard that exists,
  which passes 4 of 4. The dependency-assertion SHAPE was the right call and the phase 6 proof
  against indirect Kestrel was real; the SURFACE it asserts over is simply incomplete.
- **Finding 8: the Gateway is actively serving agents instructions to use the deleted door.** The
  built-in `move-session` skill still tells agents the Director loopback serves `/healthz`, to select
  a target by PROBING Director ports, and to set `CC_DIRECTOR_API` for spawn and buffer. Phase 6's
  report said it fixed this skill; it fixed the LAUNCHER port references and left every DIRECTOR one
  (lines 52, 61, 68, 158, 162, 190, 241, 421). This is the mission's founding reason inverted: the
  owner's stated fear was agents using the wrong door, and the product is shipping a document that
  sends them to a dead one.

### Architect rulings on all ten

**MUST FIX before landing - mission scope, each with a test that fails without it:**

1. **[CRITICAL] The phase 3 pointer drop is authorized by its FILENAME.** `SessionPointerWatcher.Apply`
   derives the target session from the file name alone and then mutates that session's Claude id,
   transcript pointer and routing map. Any same-user agent can write `<victim-session-id>.json` into
   the shared drop directory and retarget another live session. **This is the mission's own law
   broken by the mission** - phase 3 replaced a session-BOUND credential with a name anyone can
   spell. The isolation test writes the attack body to the AUTHORIZED path, so it proves only that
   the body cannot override the name - the one attack that matters was never attempted. Fix must
   bind the drop to the session that owns it, and the test must be the sibling-write attack.
3. **[HIGH] A lone registration authorizes a force-kill of a process that is not the Director.**
   `DirectorInstanceLocator.Resolve` accepts a single claimant on pid liveness and a start-time window
   without ever checking the executable path, and `DirectorSupervisor.StopAsync` will force-kill what
   it returns. A test currently LOCKS THIS IN by name. The image check exists in the ambiguous path
   already; it must apply to the single-claimant path too.
6, 7. **[MEDIUM] The Codex hook is Windows-only and duplicates per named instance.** It writes a
   `powershell` command with no operating-system branch, while the Claude installer next to it
   correctly branches to `/bin/sh` - so on macOS and Linux the hook is not runnable. And because
   idempotence compares the whole command string while the script path is instance-scoped, each named
   instance appends another global hook: proven by a run returning two `SessionStart` entries, both
   reading the same variable, so a Codex launch gets its preamble twice.
8. **[MEDIUM, ranked higher by the Architect] The `move-session` skill's Director-port instructions.**
   Both copies (Gateway-served and `.claude/skills`).
10. **[MEDIUM, ranked CRITICAL by the Architect] Widen the guard to the real listen surface** -
   `System.Net.Sockets` `TcpListener`, `System.Net.HttpListener`, and any equivalent - and prove it
   red against a BCL listener, not only against Kestrel. **A guard that has only failed against the
   case its author imagined has not been shown to work.** Note the honest complication the fixer must
   solve rather than dodge: `CcDirector.Core` legitimately contains listeners
   (`LoopbackLoginListener`, the automation browser registry) and both portless projects reference
   it, so a naive closure walk over `System.Net.Sockets` will go red on innocent code. The assertion
   must distinguish the capability being PRESENT in a shared library from the portless components
   USING it - and if that cannot be done at assembly granularity, say so and assert at the type or
   member level rather than weakening the claim to fit the tool.
9. **[MEDIUM] Tunnel-only Directors emit no session-view origin.** Phase 5 deliberately empties
   `ControlEndpoint`, so the Gateway's enrichment derives an empty base and builds a relative legacy
   link, and `GatewayCronNotifier` cannot derive a Director link at all. The aggregation tests hide
   the seam by ASSIGNING a fake base URL. Per the standing law that the Gateway owns every ruling and
   the client is dumb, the link must be minted from the GATEWAY's own origin, not derived from a
   Director endpoint that no longer exists.

**ACCEPTED, with work that is not a bridge:**

2. **[HIGH] A pre-phase-6 launcher cannot be commanded by a new Gateway.** Real, and it matters more
   than the phase 6 report allowed, because the hosted Gateway deploys INDEPENDENTLY of the desktop
   application and normally moves first. **Refused: a compatibility arm.** An arm that dials an old
   launcher's port is precisely the second door this mission exists to delete, and it would have to
   be deleted again later. What is required instead: the refusal must NAME the cause - a launcher too
   old to accept stream commands is not the same condition as a launcher that crashed, and the 502
   currently says the same thing for both - and the release must ship the launcher update before or
   with the Gateway, recorded as a release-ordering constraint rather than left to chance.

**OUT OF MISSION SCOPE - verified pre-existing, recorded and filed, NOT fixed here:**

4, 5. **The session-number and dictionary fallbacks.** Both are genuine violations of the no-fallback
   law and both are worth fixing. Neither is this mission's: the Architect verified that
   `AssignOfflineNumber` is on `origin/main` today (introduced by the fleet-unique numbering change,
   pull request #1292) and that no mission commit touches `DictionaryResolver.cs`. The precedent is
   the one this mission already set for the dead `cc-history` commands: a pre-existing defect found in
   passing is recorded precisely and filed, not smuggled into a port removal. **They belong in the QA
   report** - the standing law has two live exceptions in the product, and the owner should learn that
   from this report rather than from an outage.

**The hypothesis** - that a `Hello` failure while SignalR stays connected leaves a launcher streamed
but never registered for commands - goes to the Manager to settle cheaply by injection. If it cannot
be settled cheaply, it is recorded as open, not argued away.

## THE DIRECTOR DOES BIND A LOOPBACK LISTENER, DURING SIGN-IN - 2026-08-04

Found by the fixing Manager while widening the phase 7 guard, and **verified independently by the
Architect before being accepted**, because it qualifies the mission's headline claim and the mission's
own standard is that a claim is checked against the artifact.

`CcDirector.Avalonia` - which IS the Director process - references the setup engine and calls
`GatewayAccountEnrollRunner.SignInAndEnrollHostedAsync` from both `FirstRunWizardDialog` and
`GatewayConnectionPanel`. That runner builds a `LoopbackLoginListener`, an `HttpListener` on
loopback, to receive the sign-in callback. So the Director process CAN and DOES bind a listening
socket - transiently, on loopback only, during interactive sign-in.

**Ruling: this is ACCEPTED, and it must be STATED, never rounded to zero.**

- It is accepted because it is not the thing the mission removes. The charter's target is a standing,
  agent-reachable command door - the second entrance that confuses agents and invites scanning. This
  is a browser handing back a credential to the process that asked for it, driven by a human click,
  gone when the exchange completes, and carrying no fleet surface an agent could call.
- It must be stated because the phase 5 proof observed STEADY STATE. "Two Directors owned zero
  listening sockets" was measured on running Directors, and it remains true as measured - but the
  honest claim is *the Director runs without a listening socket*, not *the Director never binds one*.
  This mission has been caught four times by claims that were true as measured and false as worded.

**The consequence nobody had connected, and it raises the stakes on the one un-run proof.** The
outstanding pre-declared criterion is the first-launch wizard on a CLEAN Windows machine with no
security popup - and this listener runs INSIDE that very wizard. The original defect was the port
probe binding every interface, which is deleted; a loopback `HttpListener` is a different mechanism
and would not normally raise the Windows prompt. **But "would not normally" is an argument, and this
mission has ruled repeatedly that an argument is not a run.** So the clean-machine wizard proof is no
longer a formality to tidy up after landing: it is the check on a specific, plausible way the
headline user-facing promise could still fail. It stays outstanding, named, and the QA report says so.

Second measurement from the same work, recorded because it went the other way: `LoopbackLoginListener`
is NOT reachable from the Director's portless Control API component, so the guard's allow list came
out STRICTER than planned rather than looser. The Manager reported both directions.

## MAIN IS MERGED INTO THE BRANCH - the landing is de-risked - 2026-08-04

Done ahead of the landing rather than at it, so no conflict is discovered under release pressure.
Merged at `7b13b4791`; `git log origin/main ^HEAD` returns 0, so main is fully contained.

Exactly the two conflicts the dry run predicted, both resolved on the mission's side and verified:

- `PortAllocator.cs` - deleted here, modified on main by the popup fix (#2447). **Stays deleted.**
  Main fixed the popup by narrowing what the file touched; this branch deletes the file, which is the
  same fix arrived at permanently.
- `NoCrossMachineLoopbackGuardTests.cs` - main's three allowlist entries name files this branch
  deleted or emptied, so they go with them. The resolution was VERIFIED by running that guard (2 of 2
  green), which is the test that would catch a wrong resolution - the merge was not trusted on the
  absence of conflict markers.

The branch also takes main's version, `1.9.8`, so the release bump starts from the right number.

**Order note for the fix branch:** checked before merging that `mission/remove-network-port-fix`
touches NEITHER conflicted file, so landing main first cannot disturb it.

## THE PARKED RUN CAUGHT A REAL REGRESSION THE DEFAULT GATE COULD NOT SEE - 2026-08-04

The fix branch's default gate was FULLY GREEN - 4,217 tests, nine projects, zero failures. Its parked
arm then failed in two projects, and **three of the four failures were a genuine regression the
Manager had introduced**, reported against itself before being asked.

The cause: fixing inspection finding 2 split the launcher refusal into three distinct answers, and in
doing so the too-old case stopped saying "not connected" - which three tests assert as a literal
substring. Nothing in the default gate touches those tests.

**This is the strongest single argument in the mission for the parked suites, and it belongs in the
QA report.** The default gate is not a subset of the truth - it is a different question. A green
default run said nothing at all about the behaviour this change actually altered, and had the
coverage-gap warning been treated as advisory, the regression would have landed with a green gate
behind it.

**Architect ruling on how it gets fixed - the fix goes in the MESSAGE, not the tests.** "Not
connected" is TRUE in both of the split cases: a launcher too old to open a command stream is not
connected either. The split adds *why*; it does not make the shared fact false. So every refusal
meaning no-stream keeps stating that shared truth and ADDS its distinguishing evidence, and the three
tests then pass untouched - which is the only outcome that proves the change did not quietly narrow
behaviour. If forcing the shared phrase genuinely worsens a message, the Manager brings the exact
wording to the Architect for a ruling on the record rather than editing assertions unilaterally. The
mission has exactly one deleted test (`CleanInstallCliAuthenticationTests`) and it is on the record
for precisely this reason: so it can never be read later as a test dropped to get green.

The fourth failure, `WingmanInstructionsStoreTests.Save_EmptyOrOversized_Throws`, is a
save-validation test nowhere near the change. The Manager correctly refused to call it flake before
the parent arm rules on it.

### The 54-failure storm, and why the fix branch was always going to see it

The parked re-run produced **54 failures in `Gateway.UnitTests` where the previous run of the same
code produced 1.** The Manager characterised it from the artifact rather than assuming: all 54 carry
the IDENTICAL exception (`FileLogWriter.Enqueue` on a completed `BlockingCollection`), all 54 start
and end inside 0.133 seconds, and they span three classes plus four path cases the branch does not
touch. One instantaneous assembly-wide event; the count is its blast radius. The same shape as this
mission's earlier 62-failure outlier at 0.101 seconds, and the moving-victim signature already named.

**The Architect then established, with `git merge-base` rather than by reasoning, the fact that
reframes it: main's logging fix `ab78c36b1` - the v1.9.8 change aimed at EXACTLY this exception - is
NOT an ancestor of the fix branch.** That branch was cut from `5dc4fef6a`, before main was merged
into the mission branch. So both of its arms run WITHOUT the fix for the race generating their noise.

Two consequences, both acted on:

1. **The Manager's arms remain internally valid** - both sides lack the fix equally, so the
   comparative conclusion stands. It was told to finish as planned and to spend NO effort reducing
   that noise, which is expected on its base and not its to fix.
2. **The Architect's release gate on the merged tip is the first real re-measurement of whether
   v1.9.8's fix actually works.** The QA draft records that issue as open pending re-measurement;
   this settles it as a by-product of the release gate. If the storm survives on a tree that contains
   the fix, the fix did not work, and that is a fleet-wide finding the owner needs - every merge in
   this repository rides on a signal this race corrupts.

### How that measurement must be read - the protocol, fixed BEFORE the run

Written down in advance so the result cannot be fitted to it afterwards. The Manager supplied both
rules and they are adopted:

- **The discriminator is the EXCEPTION plus the INSTANT, never the COUNT.** Two runs of one
  unchanged commit gave 1 failure and then 54, so a low number proves nothing whatsoever. The
  question is whether `the collection has been marked as complete with regards to additions` appears
  at all, with its failures clustered inside a fraction of a second.
- **One clean run is not evidence** on a defect whose blast radius varies fifty-fold.

**Two different bars, and the weaker evidence may not carry the stronger claim:**

| Claim | Bar |
|---|---|
| The release may proceed | ONE run of the merged tip whose failures are all EXPLAINED. A run carrying only the known signature still passes this. |
| v1.9.8 KILLED the race (a fleet-wide claim) | MORE THAN ONE run of the merged tip showing ZERO instances of that exception. A single instance on the first run answers "no" immediately, and no further runs are needed. |

This keeps the research question off the release's critical path without letting the release's
weaker evidence be reported as the stronger finding.

**And "EXPLAINED" has a definition, because that is the word that rots quietly.** A run counts as
explained only when the exception AND the instant were read out of the RESULT FILE for THAT run.
Never when the count merely looked familiar. The Manager's own two runs are the proof: 1 failure and
54 failures would both have read as "the usual thing" from a summary line, and only opening the
artifact showed a single exception type inside a 0.133-second window. **A run declared explained
without opening the artifact is the same failure as a green nobody looked at** - which is this
mission's oldest law, arriving in a new disguise.
