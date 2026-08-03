# What the QA report must PROVE

Written by the Architect at Phase 3, before the phases that produce the evidence, so the proofs are
BUILT rather than assembled afterwards from whatever happens to exist. The owner asked for one thing:
a report showing the port is removed. This is what would actually show that, and what would only
appear to.

## The headline claim

**No listening TCP port on the Director or the launcher, and everything an agent does goes through
the Gateway.**

### Proof that counts

1. **A live connection scan on a machine running the built product**, showing nothing listening for
   `cc-director` or the launcher. `Get-NetTCPConnection` with the owning process resolved - not a
   port-number check, because a port number free is not the same as our process not listening.
2. **On a machine running MORE THAN ONE Director**, because a single-instance scan cannot distinguish
   "this Director does not listen" from "this Director failed to start". The owner runs several.
3. **The first-launch wizard on a CLEAN Windows machine, with no Windows security popup.** This is the
   defect that started the whole thing. A clean machine, because an already-installed machine has
   whatever firewall rules earlier runs created.
4. **Every `cc-*` command working**, from inside a real session, holding a real session key - which is
   what proves the removal did not simply break the tooling into silence.

### Proof that would NOT count, and why

- **The absence of a bind in the source.** Phase 2 already found a test passing while pinning a route
  that exists in neither client nor server. Source says what should happen.
- **A green test suite.** This mission has established that the local gate returns 0, 4 and 2 failures
  on the same unchanged commit. Green here is not a signal.
- **A port scan alone, with no process attribution.** A free port proves nothing about whether our
  process would have listened; it may simply not have started.
- **Any claim of the form "X is now impossible" without the attempt.** Show the refusal, not the
  design intent.

## The second claim, which the owner has not asked for but should hear

**The mission closes a live security hole that exists in production today.** Skill, workflow, schedule
and mission operations each read the account-wide Gateway token off disk and present it, so every
agent running one of those holds the whole account. This is not a risk the mission introduces and
mitigates - it is present now, and the session key ends it.

**Proof:** the before state on `origin/main`, named by file, and the after state showing a session key
in its place, plus a demonstration that a session key is REFUSED on the surfaces it should not reach.

## What the report must NOT do

- **Claim what was never run.** Two phases so far have listed their own unproven items, and that
  honesty is why their work is trusted. The report inherits that standard: a section of what is not
  proven, written as plainly as the successes.
- **Report a capability change as a security improvement.** The owner's fear was agents ending up able
  to do less. The report must state, positively and with evidence, what an agent can still do - not
  merely that a guard exists.
- **Bury the tooling findings.** Two independent reliability defects were found in the gate the whole
  fleet merges on: stale assemblies that serve the previous code while reporting success, and a suite
  that is intermittently red with nothing changed. They share one shape - the gate answers a question
  it cannot answer, and the answer looks authoritative. That belongs at the top of its section.

## The cost, stated where the owner will see it

- Genuinely-local reads are slower (a session's own terminal, 321ms to 828ms against a hosted
  Gateway). Fleet reads are FASTER (870ms against 1023ms) because they were already a round trip plus
  a local hop.
- No Gateway means no agent tooling. That is the owner's accepted trade, and the error message must
  say so in words a user can act on.
- A Director whose tunnel is down cannot be driven by its own agents.
