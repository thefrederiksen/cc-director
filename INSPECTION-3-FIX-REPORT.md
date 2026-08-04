# Inspection 3 fixes - the Manager's report

**Branch:** `mission/remove-network-port-fix`, cut from the mission branch at `5dc4fef6a`.
**Seven commits, one per ruling.** Every fix carries a test that was watched failing without it, with
the reported symptom, and the change restored afterwards.

---

## What was asked for, and what happened to each

| Ruling | Outcome |
|---|---|
| 1 [CRITICAL] the pointer drop is authorized by its filename | FIXED - `4929c914f` |
| 10 [CRITICAL by the Architect] the guard does not guard | FIXED - `325f52539` |
| 3 [HIGH] a lone registration authorizes a force-kill | FIXED - `8415d595e` |
| 6, 7 [MEDIUM] the Codex hook is Windows-only and duplicates | FIXED - `35647fdd5` |
| 8 [MEDIUM] the skill sends agents to the deleted door | FIXED - `67f378178` |
| 9 [MEDIUM] tunnel-only Directors emit no session-view origin | FIXED - `b85647021` |
| 2 [HIGH] pre-phase-6 launcher, accepted without a bridge | REFUSAL NOW NAMES ITS CAUSE - `c341cf7a3` |
| The `Hello` hypothesis | PROVED REAL, then fixed - `c341cf7a3` |
| 4, 5 the two fallbacks | Out of scope by the Architect's ruling; untouched, and they belong in the QA report |

---

## The three things the Architect and the owner should read even if nothing else

**The measurements changed two rulings, in both directions.** `LoopbackLoginListener` was written into
the new guard's allow list on the reasonable assumption that a Director reaches it. The walk says it does
not, so the entry was REMOVED and the guard is stricter than planned - wiring the sign-in listener into
the Director later now turns it red rather than finding a waiting permission. In the other direction, the
first version of that guard's boundary check asserted a magic number of types visited and went red at
forty-five on a walk that had crossed the assembly boundary perfectly well; it now names a type each
component provably uses. An allow list written from expectation would have granted the first silently,
and a threshold written from expectation would have failed the second loudly for no reason.

**Finding 8 was bigger than the inspection found.** A SECOND shipped skill carried the same defect. The
`dev-throttle` skill's changelog still instructed agents to probe the Director's loopback floor, inside a
paragraph its own later entry had already declared obsolete. The inspector reached `move-session`; the
guard reached both, which is the argument for the guard covering every shipped skill rather than the one
file that was reported.

**The unproved hypothesis is proved.** A protocol-level `Hello` failure against the real hub leaves the
connection Connected, the machine registered nowhere, and every command undeliverable - and from the
Gateway that is indistinguishable from a launcher that is not running. It is fixed, but the finding
matters on its own: the code's own comment said auto-reconnect would retry, and auto-reconnect only
retries on reconnect.

**And the parked gate caught a regression I had introduced, in my own change.** It is recorded here
because the *shape* of it is this mission's recurring theme, twice over. See "The regression this branch
caused" below.

---

## Fix by fix

### 1. A pointer drop must prove it owns the session (CRITICAL)

`SessionPointerWatcher.Apply` derived its target session from the drop file's NAME alone, then mutated
that session's Claude id, transcript pointer and routing map. The drop box is one shared same-user
directory, so any agent process could derive it from its own environment and write
`<victim-session-id>.json`. Phase 3 had replaced a session-bound credential with a name anyone can spell.

The drop name is now `id.token`: 32 hex characters from a cryptographic generator, minted per session,
never persisted, handed to the session in the environment variable that already carried the path. The
watcher refuses any drop whose token does not match, in fixed time, and leaves the file in place rather
than deleting evidence of an attempt. The old bare `id.json` shape - exactly what a writer that only
knows an id can spell - is refused rather than grandfathered.

The test is the attack the old isolation test never attempted: a sibling write naming the victim's id,
both with no token and with an attacker-minted one, refused through `Apply` and through the two-second
sweep. Both halves went red under injection with their own symptoms.

**Stated, not glossed:** a same-user process that enumerates the box during a real drop's sub-second
window can observe a token in a file name. That isolation level is UNCHANGED from the route this
replaced - a sibling could equally read the old credential out of a process environment - and the code
says so rather than implying a sandbox that never existed.

### 10. Guard the listen surface that actually exists (CRITICAL by the Architect's ranking)

The phase 7 guard rested on a false premise: that a process cannot listen without ASP.NET hosting
machinery. `TcpListener`, `HttpListener` and `Socket.Bind` need no framework reference at all.

The complication the Architect named is not dodged. `CcDirector.Core` legitimately contains listeners and
both portless projects reference it, so an assembly-level assertion would go red on innocent code. The
new guard is a call-graph walk over instruction operands: what the Director's ControlApi and the launcher
can actually REACH. It conservatively treats every method of a reached type as reachable, so dispatch
through an interface is covered, and it says plainly that it cannot see a call made only by reflection.

Proven red against both shapes, with the OLD guard green throughout - the inspector's finding
demonstrated rather than accepted: an unreferenced `TcpListener` helper in ControlApi (the indirection
that defeated the previous source-text guard), and an `HttpListener` helper in Core called from the
launcher (the shape an assembly-level allow of Core would miss entirely). In each case the untouched
component stayed green.

**The gap, stated:** what is walked is the two projects phases 5 and 6 emptied. The Director's desktop
shell is not walked, and it does bind loopback during interactive first-run sign-in. The Architect has
ruled that accepted and stated rather than rounded to zero. **The honest claim is that the Director RUNS
without a listening socket, not that it never binds one.** Phase 5's runtime scan is the evidence for the
steady state; this guard is the evidence that a listener cannot come back by refactor.

**The consequence the Architect asked be carried here:** that sign-in listener runs inside the FIRST-RUN
WIZARD, which is exactly where the Windows firewall prompt this mission exists to kill used to appear. A
loopback `HttpListener` would not normally raise that prompt - but "normally" is an argument, and this
mission does not ship arguments. **That is the specific reason the outstanding clean-machine wizard proof
still matters**, and it is not something this branch tried to settle.

### 3. A lone registration is not a licence to kill a process (HIGH)

`DirectorInstanceLocator.Resolve` accepted a single claimant on liveness and a start-time window without
checking its executable, and `DirectorSupervisor.StopAsync` force-kills what it returns.

Two gates, because they answer different questions and one can be unanswerable. The locator refuses a
lone claimant whose image is not the installed application, under a new outcome `NotSupervised` - distinct
from `Ambiguous`, because Ambiguous means the launcher cannot tell WHICH claimant is its Director while
this means it can tell perfectly well and the answer is none of them. Where there is no installed path to
compare against, the claimant resolves but is marked NOT CERTIFIED, and the kill path refuses to
force-kill anything uncertified.

The rule the class exists for is untouched: WHICH Director this is still comes from the registration,
never the image, which is why two named instances of one install are still refused. The image answers a
different question - may this launcher END this process.

The test that asserted the defect by name is replaced, with the reasoning for the reversal written into
it. The new kill-gate test drives the real `StopAsync` against a real live helper process registered as
if it were a Director, and asserts it is still alive - deliberately a foreign process, because a detector
whose failure mode is killing the test run destroys its own evidence.

### 6 and 7. The Codex hook runs off Windows, and stops multiplying (MEDIUM)

No operating-system branch: a `powershell` command on every platform, while the Claude installer beside it
has always branched to `/bin/sh`. Every Codex session off Windows silently got no preamble, and nothing
reported it because a hook that cannot run and a hook that prints nothing look identical from inside the
session. The platform is now a PARAMETER rather than a check, which is what lets the macOS and Linux form
be proven from a Windows run - a platform-conditional test would have skipped exactly where the defect
lived.

And the hooks file is global while the script directory is instance-scoped, so comparing whole command
strings made every instance look like a new hook. Our entries are now identified by a marker carried in
the script file name, and the merge REMOVES every entry of ours before adding one, so it converges rather
than skipping. Entries naming the old script are recognised as ours too - otherwise every machine that
already has one would keep it beside the new one and get the preamble twice, reintroducing the defect by
the act of fixing it.

### 8. Stop shipping instructions to the door this mission deleted (MEDIUM)

Both copies of `move-session` corrected, and the correct mechanism NAMED rather than merely removed:
`--director <id-or-name>` from `cc-devthrottle director list`, which names ONE Director where a port never
could. The second offending skill is described above.

The guard matches executable shapes - a variable assigned an address, a loopback health route, an
instruction to probe - rather than banning the vocabulary, so a skill SAYING the door is gone stays legal.
That distinction forced a rewrite of my own corrective prose, which is a better document for it: a page
that forbids something in the same phrasing it used to require it gets skimmed wrong.

### 9. A session link comes from the Gateway (MEDIUM)

The link was rooted on the Director's own base URL. Phase 5 registers an empty endpoint, so it emitted a
relative `/sessions/{id}/view?gw=...` - a path with no origin pointing at a route that exists nowhere. The
cron notifier had the same root and returned empty, so every run-complete notification on a current fleet
silently carried NO link: not a broken link somebody would report, but no link, which reads as a
notification that simply does not have one.

Minted from the Gateway's own origin at all three sites. A Director-supplied link is now IGNORED rather
than preferred - keeping "use it when present" would have preserved exactly the case that breaks, since a
Director old enough to supply one supplies a link to its own port. Where there is no origin the answer is
an empty string, not a relative path: a path with no origin renders as a working link and resolves
against whatever page is showing it.

### 2. The refusal names its cause (accepted, not bridged)

Three answers now, split structurally: never registered; registered but gone quiet; and registered, still
heartbeating, holding no stream. The third carries the registered version and the seconds since the last
heartbeat, so it shows its evidence instead of asserting a cause - a launcher that reached this Gateway
seconds ago and cannot be reached by it is what too-old looks like from here, and a reader can check that.

**The release-ordering constraint, which is not code and needs to be honoured by a person:** the launcher
update must ship BEFORE or WITH the Gateway. The hosted Gateway deploys independently of the desktop
application and normally moves first, which is the wrong order for this change.

---

## The regression this branch caused, and the two lessons in it

The parked gate arm failed on the fix tip with three failures that were **mine**, and the default gate
could never have seen them: `SelfHostMachineControlTests` (two cases) and
`LauncherRegistryEndpointTests.Relay_RegisteredButNotConnected_Returns502ThatSaysSo` all assert that an
undeliverable command answers with the substring `not connected`, and the finding 2 split had dropped
that phrase from BOTH branches in favour of new, more specific wording.

**Lesson one: adding a reason must not remove the fact.** The Architect's ruling settled it and the
reasoning is better than the fix I was about to make. `not connected` is TRUE IN BOTH cases - a launcher
too old to stream holds no stream, so it is not connected either. The split adds WHY; it does not make
the shared fact false. So the correction belonged in the MESSAGE, not in the assertions, and the three
tests now pass UNTOUCHED. That is the only outcome that proves the change did not quietly narrow what
the message promises: editing the tests to match new wording would have produced an identical green and
meant the opposite. This mission has one precedent for a test removed on the record, and that precedent
exists so this kind of edit is never mistaken for it.

**Lesson two, smaller and sharper: the first correction READ as an improvement and meant nothing.** It
wrote `NOT CONNECTED` in capitals, for emphasis. The assertion is case-sensitive, so it failed in exactly
the same way as the original defect while looking like the fix. That is this mission's recurring theme in
miniature - the snapshot design that left 48 of 52 tests green, the guard that passed 4 of 4 while its
premise was false, a test that was vacuous for 22 days. **A change that looks like an improvement and
changes nothing is the failure mode this repository produces most reliably**, and the only thing that
catches it is running the thing rather than reading it.

**Lesson three, about the gate itself: `-Parked` was mandatory here, not optional.** The default run was
fully green - 4,217 tests, all nine projects Completed - and it was green while three tests of the exact
surface I had changed sat unrun in a parked suite. The gate flagged the coverage gap itself and naming
that gap is what made the difference between a real gate and a ritual.

## What is NOT proven

- **Windows only.** Nothing here was run on macOS or Linux. Two of these fixes are specifically about
  non-Windows behaviour - the Codex shell hook and its command form - and both are proven by asserting
  the generated script and command from a Windows run, which is a proof about what is WRITTEN, not that
  it executes correctly on a Mac. That distinction is the same one that caught phase 4's macOS
  regression, and it is not closed here.
- **The Director's desktop shell is outside the new guard**, and it does bind loopback during first-run
  sign-in. See above - this is the reason the clean-machine wizard proof still matters.
- **The pre-phase-6 launcher condition is inferred, not executed.** The refusal is proven to distinguish
  a heartbeating launcher with no stream from a silent one; nothing here ran a genuinely old launcher
  binary against a new Gateway.
- **The mixed-version window generally** remains stated rather than tested, as phase 6 said.
