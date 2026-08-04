# Remove the network port - QA report

DRAFT. Phases 1 to 4 are complete and proven; Phases 5, 6 and 7 are marked OUTSTANDING and will be
filled from their own reports. Nothing below is written from a phase's own claim alone - every line
either cites a run or says plainly that it does not.

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
| 5 | Delete the Director's listener | CODE DONE AND PUSHED - proof outstanding |
| 6 | Delete the launcher's listener, plus the guard | In progress |
| 7 | Guard against re-adding a listener | Folded into 6 |

## The headline claim, and what proves it

**OUTSTANDING** until Phase 5's proof lands. What will be accepted, decided BEFORE the evidence was
gathered so it could not be fitted to whatever turned up:

- A live connection scan with the OWNING PROCESS resolved - not a port-number check, because a free
  port proves nothing about whether our process would have listened.
- On a machine running MORE THAN ONE Director, because a single-instance scan cannot distinguish
  "does not listen" from "failed to start".
- Every command working from inside a real session, which is what proves the port was removed rather
  than the tooling broken into silence.
- The first-launch wizard on a CLEAN Windows machine with no security popup.

Explicitly NOT accepted: absence of a bind in the source, a green test suite, or a port scan without
process attribution.

**Already verified independently by the Architect on the branch:** `PortAllocator.cs` no longer exists;
no bind of any kind remains in the Control API project; `CC_DIRECTOR_API` is no longer stamped into
sessions (the only mentions left are comments explaining its removal).

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
