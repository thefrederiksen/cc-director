# Remove the network port - QA report

DRAFT. All phases are complete and proven; the third independent inspection (phases 3 to 7) is in
flight and its findings section will be filled from its report. Nothing below is written from a
phase's own claim alone - every line either cites a run or says plainly that it does not.

---

## What you asked for

No listening network port on the Director or the launcher. Everything an agent does goes through the
Gateway - one door, always.

## What is done

| Phase | What | State |
|---|---|---|
| 1 | Check the Gateway can serve a session's credential | Done - it could not; created 1b |
| 1b | Session-scoped credentials on the Gateway | Done and proven |
| 2 | The command line tools call only the Gateway | Done - pass mark met on a real run |
| 2b | Your settings widening, the write paths, 8 inspection fixes | Done and proven |
| 3 | Session hooks stop needing an API | Done and proven |
| 4 | Lifecycle off HTTP, Windows and macOS | Done and proven on both |
| 5 | Delete the Director's listener | Done and proven - see below |
| 6 | Delete the launcher's listener | Done and proven - see below |
| 7 | Guard against re-adding a listener | Done - a dependency guard, proven red under an indirect reintroduction |

## The headline claim, and what proves it

**PROVEN.** The acceptance criteria were decided BEFORE the evidence was gathered so they could not
be fitted to whatever turned up, and each has now been met by a run:

- **The Director owns no listening socket.** Two Directors built from this branch, alive and
  registered, own ZERO listening sockets while holding 14 established outbound Gateway connections.
  What makes zero mean zero: the SAME query in the SAME instant caught the owner's own two
  production Directors LISTENING on 7879 and 7881 - a positive control proving the scan can see
  listeners. Evidence: `docs/qa/phase5-noport/`. This also satisfies the more-than-one-Director
  requirement.
- **The launcher owns no listening socket.** A running, functional launcher - pid read from the
  registration the live process wrote, image resolved to this branch's build - owns zero LISTEN-state
  rows; its only sockets are the two outbound connections it opened to the Gateway. Director start,
  stop and restart through the Gateway all succeeded over that stream with observed process effects
  (new pids appearing, graceful exits), and the no-stream case refuses loudly with a 502 rather than
  dialling anything - there is no address left in the system to dial. Evidence: `PHASE-6-REPORT.md`
  section 4.
- **Every command works from inside a real session:** 17 of 17 `cc-*` commands green from a real
  keyed session whose own environment dump shows `CC_DIRECTOR_API` and `CC_DIRECTOR_TOKEN` ABSENT -
  the tooling works AND the address is genuinely gone, not merely unused.
- **Nothing CAN listen again silently:** a dependency guard asserts at project and assembly level
  (the whole reference closure, walked off DLL metadata) that neither the Control API nor the
  launcher references any hosting surface, with the SignalR CLIENT explicitly permitted - refusing
  the capability to listen while keeping the capability to connect out. Proven red against an
  INDIRECT reintroduction (a Kestrel host in an innocuously named helper in its own file, referenced
  by nothing) - the exact shape that defeated a previous source-text guard - and it caught a real
  leftover framework reference on its first run.

Explicitly NOT accepted as proof, and not relied on: absence of a bind in the source, a green test
suite, or a port scan without process attribution.

**Outstanding against the pre-declared criteria:** the first-launch wizard on a CLEAN Windows
machine. The popup's cause (the port-probe code) is deleted outright, and the two-line interim fix
for the same defect already shipped in v1.9.8 and was verified on its own branch - but the
clean-machine run against THIS branch has not been performed and this report will not claim it.

## What agents can still do - your question, answered with evidence

You were right to ask, and you were right once already: an early version of the permission list would
have silently refused commands your agents need.

**Unchanged:** see every session, repository, worktree, machine, Director and mission in your account;
read any session's scrollback and history; prompt, interrupt, park, rename, set role or mission,
compact, flag finished; message another agent or a whole team; spawn sessions; launch on another
machine; read and use shared skills and workflows; drive the automation browsers. **Settings and
handovers** were added on your ruling that agents should maintain and configure without the interface.

**Refused:** device enrolment, account identity, force-killing a Director. The principle recorded so it
need not be re-decided: an agent may change how the product BEHAVES, not WHO IS ALLOWED IN.

**Proven on the wire** with a real session key: 8 routes allowed, 9 refused.

## The qualification on the headline claim, stated rather than rounded away

**The Director does bind one listening socket - on loopback, during interactive sign-in.** The
desktop shell opens a local `HttpListener` to catch the sign-in callback from your browser, in the
first-run wizard and the gateway connection panel.

This was found while widening the guard, and verified in the source before being accepted rather
than taken on report. It is accepted because it is not the door this mission removes: it is
transient, loopback-only, opened because a human clicked sign-in, and it carries no fleet surface an
agent could call. But the honest wording is **the Director RUNS without a listening socket**, not
that it never binds one - the live proof measured two running Directors, which is steady state.

This mission has been caught four separate times by claims that were true as measured and false as
worded. This one is written the way it is so it does not become the fifth.

## What this mission FIXED that you did not ask for

**A live security hole.** Skill, workflow, schedule and mission commands each read your account-wide
Gateway token off disk and presented it - so every agent running one of those held your whole account,
on every machine. That is present in production today. The session key ends it: per session, hashed on
the Gateway, scope-limited, expiring, revoked on reap.

**The first-launch wizard defect**, shipped in v1.9.8 as a two-line fix while the mission continues.

## What this mission BROKE, and what happened about it

**macOS lifecycle.** Phase 4 replaced HTTP lifecycle with named signals, and the Unix arm had never
run. Every launcher-to-Director signal was silently lost in both directions: every stop a 20-second
stall then a force-kill, every update applied by force-kill, and "install it now" REPORTING SUCCESS
while nothing happened.

Found because you made a Mac available and a seat was sent to PROVE rather than assume. Nothing in the
shared test suite could have caught it - Windows uses kernel events, so its processes never have to
agree on a file path at all. Fixed, re-proven on macOS, and Windows verified unregressed across both
target frameworks.

## Three independent inspections, and why they were worth the delay

A different agent family, working in its own copy of the code at the branch tip, was told to attack
this mission's own reports rather than believe them. That happened three times. **Every time, it
found real defects that every green test suite had missed** - eighteen in total across the three.

The third inspection is the one that justifies the whole practice. It returned **FAIL: ten proved
defects while 261 tests passed and none failed.** Its own explanation of how that is possible is the
most valuable sentence produced on this mission: *several of the tests explicitly PRESERVE the
defects, and the guard test cannot observe the defect in itself.*

Three of those ten are worth your attention directly:

1. **The guard that was supposed to stop a port ever coming back did not work.** Its premise - that a
   process cannot listen without heavyweight web machinery - is simply false; the plain .NET library
   can open a listener with none of it. The inspector proved this by binding a real port from a
   project with zero of the assemblies the guard looks for, while all four of the guard's assertions
   stayed green. **The mission's central promise was resting on a test that proved nothing.** It has
   been rebuilt as a call-graph walk - what these components can actually REACH - and proven red
   against both a hidden listener dropped into the Director's component and one reached indirectly
   through a shared library, which is the shape the old guard could never have seen.
2. **The product was still shipping agents instructions to use the deleted door.** The built-in
   move-session skill told agents to find a Director by probing its ports and to set the address
   variable this mission removed. A phase report had claimed this was fixed; it had fixed the
   launcher half and left the Director half. This is your founding fear inverted - not agents using
   two doors, but agents being sent to a door that is gone.
3. **The session-hook change had replaced a credential with a filename.** Any agent could name
   another live session in a filename and retarget that session's transcript and routing. The test
   written to prove isolation attacked the wrong path, so the one attack that mattered was never
   attempted. **That is this mission breaking its own law** - the line you drew about what an agent
   may change. It now requires a per-session token the writer cannot guess, and the test is the
   sibling attack that was missing.

**Fixing those ten found three more that nobody had reported**, which is the argument for fixing
properly rather than patching what was named:

- **A launcher could be connected, registered nowhere, and look exactly like a launcher that is not
  running.** The inspector raised this as an unproved suspicion and refused to claim it. It was
  tested against the real connection and is real: one particular failure during the launcher's
  introduction leaves it connected but never registered, every command undeliverable, and from the
  Gateway's side indistinguishable from a machine that is switched off. The code's own comment said
  the connection would retry - it only retries after a disconnect, and this never disconnects. Fixed.
- **A second shipped skill carried the same stale instructions** as the one the inspector found -
  still telling agents to probe the Director's local port, inside a passage its own later text had
  already declared obsolete. The inspector reached one file; the new guard reaches every shipped
  skill.
- **Run-complete notifications on a current fleet carried no link at all.** Not a broken link
  somebody would report - no link, which reads as a notification that simply does not have one.

Two further findings were verified as PRE-EXISTING and are being filed rather than fixed here: the
Director falls back to numbering sessions locally when the Gateway fails, and the dictionary falls
back to a local file. Both genuinely break the no-fallback rule this mission holds itself to; neither
was introduced by it. You are being told because the rule has two live exceptions in the product, and
you should hear it here rather than from an outage.

## Findings about your tooling - not footnotes

Both share one shape: **the gate answers a question it cannot answer, and the answer looks
authoritative.**

1. **Incremental builds serve STALE assemblies while reporting success.** Cost three consecutive wrong
   diagnoses in one afternoon. Workaround: delete `obj` and `bin` for any project whose result you
   intend to trust.
2. **The suite is intermittently red with nothing changed** - the same commit gave 0, then 4, then 2
   failures. Now traced to ONE cause: a race in the log writer's teardown. Filed as issue #2445 with
   two independent lines of evidence. v1.9.8 contains a fix that removes the exception; the issue
   stays open pending re-measurement, because a single green run is the exact false signal it is about.

**Consequence for you:** every piece of work in this repository merges on "the tests are green", and
that signal has been corrupted. The mission's landing criterion is therefore COMPARATIVE - a run counts
only against a run of its parent, and the parent must be run more than once, because on this mission a
parent's first run came back clean and a single control would have convicted the work of a regression
it did not cause.

## Costs, stated plainly

- Genuinely-local reads are slower: a session reading its own terminal, 321ms to 828ms.
- Fleet-wide reads are FASTER: 870ms against 1023ms, because they were already a Gateway round trip
  plus a local hop. **The brief overstated this cost and the correction is in your favour.**
- No Gateway means no agent tooling. Your accepted trade; the error names the self-hosted gateway.
- A Director whose tunnel is down cannot be driven by its own agents.

## Open, and not closed by this mission

- Four pre-existing defects found in passing, including one where an update on a machine with named
  instances rolls back silently - which affects your machine.
- The two fallback paths named above (session numbering, dictionary). Filed, not fixed here.
- Linux is unproven. macOS is proven; Linux shares the Unix arm but has not been run.

## What is NOT proven

Written as plainly as the successes, because every phase of this mission listed its own gaps and that
habit is the reason its claims can be trusted.

- **The first-run wizard on a clean Windows machine has not been run.** This is the one pre-declared
  acceptance criterion still outstanding, and it became more important rather than less: the sign-in
  listener described above opens inside that very wizard, which is exactly where the firewall popup
  used to appear. The popup's original cause is deleted, and a loopback listener would not normally
  raise that prompt - but "would not normally" is an argument, and this mission does not ship
  arguments in place of runs. **Recommendation: run it against the built release before the mission
  is called closed.** Your own machine cannot answer it; it already carries firewall rules from
  earlier runs.
- **Phase 6 was not run on macOS or Linux.** Its pieces are less platform-split than the lifecycle
  work was, but phase 4 proved on this exact surface that a green shared suite can hide a completely
  inert platform mechanism.
- **"From another machine" was proven as a code path, not as two physical machines.**
- **A mixed-version fleet degrades.** A launcher older than this change cannot be commanded by an
  upgraded Gateway. No compatibility bridge was built, deliberately - an arm that dials an old
  launcher's port is the second door this mission exists to delete. Instead the refusal now names its
  cause, and **the release must ship the launcher update with or before the Gateway.**
- **The guard cannot see a call made only by reflection.** Stated in the guard's own file rather than
  implied away.
- **The guard covers the two components this mission emptied, not the desktop shell** - which is the
  component that binds the sign-in listener. The runtime scan is the evidence for the steady state;
  the guard is the evidence it cannot come back by refactor.
- **The inspection fixes were run on Windows only.** Two of them are specifically about behaviour on
  macOS and Linux - the agent hook that had been written to run only on Windows - and they are proven
  by checking what gets WRITTEN, not by running it on a Mac. That is the same distinction that caught
  the macOS defect earlier in this work, and it is not closed.
- **The old-launcher case is inferred, not executed.** The refusal is proven to tell a quiet launcher
  apart from a connected one; nothing ran a genuinely old launcher against a new Gateway.
- **The session drop box is no more isolated than what it replaced.** A process running as you, during
  the fraction of a second a real hand-off exists, could observe its token. That was equally true of
  the credential it replaces, and the code says so rather than implying a sandbox that never existed.

## What is NOT proven

To be completed from Phases 5, 6 and 7. This section is written last and deliberately - two Managers
earned trust by listing their own gaps, and the report inherits that standard rather than closing on
the successes.
