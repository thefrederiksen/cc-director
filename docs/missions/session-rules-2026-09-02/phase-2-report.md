# Session Rules - phase 2 report

Phase 2: the THIN VERTICAL SLICE straight to the owner's demonstration. Words go onto a real terminal
screen, the session goes idle on its own, a rule fires on its own, something is typed, and the screen
after shows it.

Branch `mission/session-rules-p2`, worktree `D:\ReposFred\devthrottle-session-rules-p2`, cut from
`origin/mission/session-rules`.

The demonstration itself, with the screens quoted, is in `qa-report.md` - it was written into that
file the moment each part worked, not collected up afterwards. This file is the account of the BUILD:
what was written, what red was watched before each green, and what the run found out.

---

## What was built

| Piece | What it is |
| --- | --- |
| `RuleCandidateFilter` | The FREE CHECKS. Pure code, no model: session idle, screen changed, rule in scope, under cooldown, under the daily cap, trigger words present. Every rule it turns away leaves with a stated reason. |
| `RuleAgentContract` | THE ONE AGENT CALL (ruling A5): one question per screen covering every candidate rule, and a reply whose every part is validated against what was offered. The checks it advertises are read off the derived registry, so the question cannot name a check that does not exist. |
| `RuleCheckRunner` | Runs the checks the agent named, and records what each one answered. A check whose answer is a plain yes-or-no and that answers NO abandons the act, as does a check that could not be run at all. |
| `RuleEvaluator` | The orchestration: free checks, one agent call, the checks, the RE-READ immediately before the keystroke, then dry run or the send - with a firing recorded at every outcome. |
| `GatewayRuleEnvironment` | The production wiring: the tunnel screen read, the pushed roster row, the model, the prompt verb, and the phase 1 store. THE ONLY TYPE IN THE FEATURE THAT CAN TYPE. |
| `SessionRuleEndpoints` | Four routes under `/gateway/rules` - read, write, promote, delete, and the firing record - so a real rule can be put into the real store from outside the process. Not a user interface; that is phase 5. |
| Turn-end wiring | The evaluator is armed beside the session supervisor and hangs off the SAME Working-to-idle boundary, so a working session is out of a rule's reach by construction. |

---

## Red, then green

Every feature was written as a test that was run against unwritten code and WATCHED FAILING, with the
red recorded before the code existed.

| What | Red | Green |
| --- | --- | --- |
| The free checks, the one agent call and the evaluator | `62133c497` - **Failed: 47, Passed: 55, exit code 1** | `558f2698a` - **Passed: 102, Failed: 0, exit code 0** |
| The tightened types-nothing guard, against the unwritten production wiring | `a7bf10b2f` - **Failed: 1, Passed: 3, exit code 1** | `18fb72a7c` - **Passed: 127, Failed: 0, exit code 0** |
| The Gateway unit suite, after the endpoints and the host wiring | | `73273a457` - **Passed: 3360, Failed: 0, Skipped: 2, exit code 0** |
| The honest send outcome (the defect the live run found) | old wording restored on the new plumbing - **Failed: 2, Passed: 16, exit code 1** | `79f699c82` - **Passed: 128, Failed: 0, exit code 0** |

The 55 passes in the first red are deliberate: they are the phase 1 tests the same filter catches, so
the instrument was reading something rather than an empty set. The 3 passes in the guard's red are
the same thing - the scanner does find the typing seam where it really is, and the rules namespace is
not empty.

### The red on the tightened guard, quoted

```
Assert.Contains() Failure: Item not found in collection
Collection: []
Not found:  "CcDirector.Gateway.Rules.GatewayRuleEnvironment"
```

Phase 1's guard said nothing in the rules namespace may type. Phase 2 types, which is its whole
point, so the guard was TIGHTENED rather than deleted: the set of types in the namespace that reach
the prompt verb must be exactly one, it must be the production wiring by name, and the evaluator -
where the dry-run decision is made - must not be able to reach it at all.

### The red on the send outcome, quoted

```
Assert.Contains() Failure: Sub-string not found
String:    "the send did not land, so the session was"...
Not found: "did not confirm"

Failed: 2, Passed: 16, exit code 1
```

That red was produced by putting the OLD wording back on top of the new plumbing, so the failure is
about the behaviour under test and not about a type that does not compile yet.

---

## Three things the work found out, recorded rather than tidied away

### 1. A guard that walked the namespace could not see inside an async method, and reported that as clean

The phase 1 types-nothing guard reads the built assembly with Mono.Cecil and filters types by their
namespace. **An async method's body does not live in the method**: the compiler moves it into a
generated nested type, and in the metadata that nested type carries an EMPTY namespace. So a scan
filtered on `Namespace` found nothing for every async method in the namespace it was guarding - and
answered with an empty list, which reads exactly like "nothing reaches the seam".

It never mattered in phase 1, because nothing in the namespace typed at all. It surfaced the moment
something did: the guard was asked to find the one type that is SUPPOSED to type, and came back with
`Collection: []`. It now walks out to the outermost declaring type. The lesson is the ordinary one -
the guard was only caught because it had a PRESENCE assertion in it as well as an absence.

### 2. The prompt route's 502 is not a failed send, and the record said it was

The live demonstration wrote this into a real firing record:

```
outcome: the send did not land, so the session was never reached.
```

The session's own screen showed `/usage-credits` sitting on it. The prompt verb had answered
`never started a turn ... parked in the composer unsubmitted` - which it does for any session whose
turn is over in milliseconds - and the evaluator read that as a failed send. This is the trap the
mission brief names in those exact words, and it was walked into anyway.

The send seam now answers THREE things rather than two, because "it did not work" hid the distinction
that matters:

- **not sent** - the keystroke never left this Gateway (the machine is not connected). Nothing was
  typed, and the record says so with a blank typed text.
- **not confirmed** - it went out and nobody would confirm it. The text is kept as typed, the route's
  own words are quoted on the record, and the record names the session's SCREEN as the only evidence
  of whether the keystroke landed.
- **confirmed** - it went out and the route confirmed a turn started.

### 3. The agent's reason quoted text that was not on the screen it was given

On a re-run of the demonstration against the fixed build, the rule DECLINED, and its recorded reason
said:

> the echo output explicitly says 'THE SCREEN HAS MOVED ON WHILE THE RULE WAS THINKING', confirming
> it is no longer stopped on the allowance notice.

That sentence is not on the screen the firing record stores, and the firing record stores the exact
text the question carried. The words were on the session's screen twelve minutes earlier, in an
unrelated run. **The decline itself was safe** - declining is the direction that does nothing - and
the whole apparatus behaved correctly around it: the pass ran, the reason was recorded, nothing was
typed. But the JUDGEMENT was not faithful to the screen it was given, and the same unfaithfulness in
the other direction would be a rule acting on evidence that was not there.

This is recorded, not fixed, and it is the sharpest thing phase 2 learned. It belongs with phase 4's
hardening: the reply already has to name a rule that was offered and checks that exist, and the
natural next bound is to require an act's reason to quote something the screen actually contains.

---

## What phase 2 did NOT prove

- **No authoring conversation.** The rule's derived parts - the plain-English screen description, the
  trigger words, the stored check - were written by hand into the create route. Deriving them from
  the account's sentence with a model is phase 3, and row 1 of the QA report stays PENDING.
- **No user interface.** Rules are read and written over `/gateway/rules` only.
- **The rule's own stored check never ran on a live screen.** A rule stores the checks derived for
  it, but the evaluator runs the checks the AGENT names in its reply (ruling A5), and on every live
  screen in this run the agent named none. The check-running path is proved in the unit suite and NOT
  on a live screen.
- **The judgement is not stable across runs.** The same rule, on near-identical screens, ACTED once
  and DECLINED twice. Every one of those is a recorded firing with a reason, so the record is
  complete either way - but "the rule fires when it should" is proved by ONE observed act, not by a
  repeatable rate.
- **The session was a plain shell**, made to print an allowance notice. This is the mechanism, not
  the recovery of a real provider limit; row 3 of the QA report stays PENDING and is not to be faked.
- **One machine, one tenant, SQLite, no auth on the rig.** Nothing ran against Postgres, nothing ran
  hosted, and no client authentication was exercised.
- **`CcDirector.Gateway.Tests` did not run.** It is parked and host-bound; a queued run cannot acquire
  the machine-wide lock within its 45-minute wait.
- **The rules routes are not on the session-key allow list.** An agent's session key cannot call
  `/gateway/rules`; a device key can. Whether an agent should be able to write rules is a product
  decision for the owner, and it was deliberately not made here.

---

## The rig, and how to stand it up again

The live runs were made against a Gateway and a Director built from this branch and isolated from the
owner's own fleet - a separate data root, port 7899, no Tailscale, no auth, and a scratch repository:

1. `dotnet build src\CcDirector.Gateway\CcDirector.Gateway.csproj`, then run
   `CcDirector.Gateway.exe --port 7899` with `CC_DIRECTOR_ROOT` pointed at a scratch root and
   `CC_GATEWAY_NO_TAILSCALE=1`, `CC_GATEWAY_NO_AUTH=1`. Copy the real `keyvault.json` into that root
   so the model call can be made.
2. `scripts\local-build-avalonia.ps1 -Slot 6`, write `<root>\instances\default\config\config.json`
   with `gateway.url = http://127.0.0.1:7899` and `onboarding.completed = true`, and launch it
   through a scheduled task (never from an agent's own process tree).
3. Spawn the target: `POST /directors/{id}/sessions` with `agent: RawCli`, `command: cmd`.

**Two things about driving a plain shell that cost time and are worth knowing.** A brand-new session
suppresses the byte-to-Working flip entirely, and the flag only clears when a send passes the submit
verifier - so until one command produces more than 2048 bytes of output, the session never crosses
Working-to-idle and NOTHING wakes the evaluator. And the submit verifier answers 502 for every small
command in a shell, which also fires several nudge keystrokes. Both are properties of driving a shell
rather than an agent, and neither is a fault in this feature; a command shaped
`cls & <something noisy> & cls & echo <the notice>` gets past both and leaves a clean screen.
