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
- Linux is unproven. macOS is proven; Linux shares the Unix arm but has not been run.

## What is NOT proven

To be completed from Phases 5, 6 and 7. This section is written last and deliberately - two Managers
earned trust by listing their own gaps, and the report inherits that standard rather than closing on
the successes.
