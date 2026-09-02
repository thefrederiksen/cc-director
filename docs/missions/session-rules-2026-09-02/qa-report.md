# Session Rules - the QA report

**Status: IN PROGRESS. Every row below is either evidence with its exit code and the commit it ran
on, or the word PENDING. Nothing here is written ahead of the run that proves it.**

This report accumulates from the first proof onward rather than being written at the end, so that a
run which stops early still leaves an honest account rather than nothing. It is emailed to the owner
once, when the mission finishes or when it stops.

Mission: Session Rules. Branch `mission/session-rules`, cut from `origin/main` at `fac79fb56`.
Issue thefrederiksen/devthrottle#2644.

---

## What was asked for

> The goal is a QA report that clearly shows this feature working and shows also the rules of how to
> do something. The QA report needs to show that it can accept something into a screen if a rule is
> triggered. You should be able to test that fairly simply by just putting words in a terminal screen
> and then seeing that you can do something when those words happen automatically.

---

## 1. A rule created from plain English

The sentence in, the built rule out, quoted.

**PENDING.**

## 2. The headline - words on a screen, and something happens because a rule said so

**PROVED, on a real session, on commit `73273a457`.** Words were put on a real terminal screen; the
session went idle on its own; a rule stored in the real store fired on its own; `/usage-credits` was
typed into the session by nobody; and the screen afterwards shows it there.

### The rig - what was real, and what was crude

Everything in the chain is production code and real machinery:

| Part | What it was |
| --- | --- |
| The Gateway | The real Gateway built from this branch, `/healthz` reporting `2.0.4+73273a4570da4d54e3972de45bbb1a1ebca9236b`, on an isolated data root and port so it never touched the owner's own Gateway. |
| The Director | A real Director (slot 6, built from this branch), connected to that Gateway over the ordinary Director stream. |
| The session | A real `RawCli` session running `cmd` - `0234084a-2e6a-42af-b794-ca982f867266` - deliberately a plain shell, so a command typed into it either ran or it did not, and the screen says which. |
| The screen read | The real `screen-grid` verb over the tunnel. |
| The rule | Written into the real phase 1 store through `POST /gateway/rules`, which is the store's own gate. |
| The trigger | The real Working-to-idle transition. Nothing was nudged: the terminal went byte-silent for its 10-second quiet threshold, the Director flipped the session to `WaitingForInput`, and the turn-end watcher woke the evaluator. |
| The decision | One real model call - `chat/completions model=devthrottle/wingman OK: 571 chars in 18.4s`. |
| The keystroke | The real prompt verb, the same route everything else in the product types through. |
| The record | Real rows in the real firing store, read back over `GET /gateway/rules/{id}/firings`. |

What was crude, stated plainly: there is **no user interface**, and there is **no authoring
conversation** - the rule's derived parts (the plain-English screen description, the trigger words,
the stored check) were written by hand into the create route rather than derived from the sentence by
a model. Deriving them is phase 3, and row 1 of this report stays PENDING until it is built.

### The rule

Stored in dry run - the store takes no state parameter, so no caller can create a live rule.

```
id:                  e34f821a-d59d-4940-b7d1-9c5c8938faf0
the account said:    If a session's screen says it has run out of its model allowance, type the
                     command that shows me what is left.
watching for:        A session that has stopped on a notice from its model provider saying its
                     allowance for the current model is used up.
trigger words:       limit, usage-credits, allowance, out of credits
stored check:        matches_any(text=<screen_text>, terms=limit,allowance)
scope:               agent RawCli, repository C:\Users\soren\AppData\Local\Temp\ccrules\scratch
cooldown:            20 seconds       daily cap: 5
state:               dry_run
```

### The dry run first, and it typed nothing

The words were put on the screen. The session went idle. The rule fired, decided to act, and typed
**nothing**, because it was in dry run - and the record says what it WOULD have typed:

```
decision:       act
occurred:       2026-09-02T20:16:13.4132778Z
understanding:  The session has stopped on a notice from the model provider saying the Fable 5 model
                limit has been reached, which is an allowance exhaustion message.
reason:         The screen reports the model allowance for the current model is used up, and the
                instruction says to type the command that shows what is left. The screen itself
                suggests /usage-credits, which serves that purpose.
checks run:     (none - the agent named no check on this screen)
typed:          ""
outcome:        dry run: nothing was typed. It would have typed: /usage-credits
```

The Gateway's own log for that pass:

```
16:16:01.910 [RuleEvaluator] sid=0234084a-...: 1 rule(s) worth asking about
16:16:13.412 [RuleEvaluator] firing: rule=e34f821a-... sid=0234084a-... decision=act typed=no
16:16:13.481 [GatewayHost] turn-end rules: sid=0234084a-... outcome=dry-run
```

### Then a person promoted it

```
POST /gateway/rules/e34f821a-d59d-4940-b7d1-9c5c8938faf0/promote
-> state=live  updatedUtc=2026-09-02T20:16:35.2494581Z
```

A rule cannot promote itself; there is no route by which it could.

### The free checks stopping the pass, on the way - unplanned, and worth keeping

The next turn end produced the same screen byte for byte, and the pass stopped before it reached a
model at all:

```
16:17:07.477 [RuleEvaluator] stopped-before-any-rule: the screen has not changed since this session
             was last looked at
16:17:07.477 [GatewayHost] turn-end rules: sid=0234084a-... outcome=stopped-before-any-rule
```

That was not staged. It is the free-check layer doing exactly its job on a real screen: a turn end
that changes nothing costs one screen read and no model call.

### The screen BEFORE

```
[16:17:23.65] provider notice follows
You've reached your Fable 5 limit. Run /usage-credits to continue or switch models with /model.


C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
```

That is the screen the decision was made on, quoted from the firing record rather than from a
separate read - it is the text the agent actually saw.

### What the agent understood and decided

```
occurred:       2026-09-02T20:18:11.521661Z
decision:       act
understanding:  The screen shows a provider notice stating the session has reached its Fable 5 model
                limit, and the session is idle at the command prompt. The notice suggests running
                /usage-credits to see credit status.
reason:         The screen explicitly reports a provider notice that the model allowance for Fable 5
                is used up, and the session has stopped at the prompt. This matches the instruction's
                trigger condition exactly.
checks run:     (none - the agent named no check on this screen)
typed:          /usage-credits
```

### The screen was read AGAIN immediately before the keystroke

From the Gateway log, in order, within four milliseconds:

```
16:18:01.738 [HostedInferenceBrain] chat/completions model=devthrottle/wingman OK: 571 chars in 18.4s
16:18:01.738 [GatewayHost] SendCommandAsync: verb=screen-grid, sid=0234084a-...
16:18:01.739 [DirectorCommandRouter] screen-grid sid=0234084a-... : stream status=Ok
16:18:01.741 [GatewayHost] SendCommandAsync: verb=prompt, sid=0234084a-...
```

The model answers, the screen is re-read, the re-read matches, and only then is anything typed. A
screen that had moved on in those milliseconds would have been abandoned instead - see row 4.

### The screen AFTER

```
[16:17:23.65] provider notice follows
You've reached your Fable 5 limit. Run /usage-credits to continue or switch models with /model.C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>/usage-credits
'/usage-credits' is not recognized as an internal or external command,
operable program or batch file.C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
```

**`/usage-credits` is on that screen and nobody typed it.** The shell does not know the command, and
says so - which is the correct behaviour of a plain `cmd` and is exactly why a plain shell was chosen:
the shell's answer is unambiguous evidence that the text arrived and was submitted.

### A DEFECT this run found, recorded rather than tidied away

The firing's `outcome` field says:

```
outcome:  the send did not land, so the session was never reached.
```

**That sentence is wrong, and the screen above is the proof.** The prompt verb returned HTTP 502 from
the submit verifier - `never started a turn ... the agent produced under 2048 bytes, so the prompt is
parked in the composer unsubmitted` - which is the trap the mission brief warns about in those words,
and the evaluator read that 502 as a failed send. The keystroke had in fact landed. The evaluator
must not treat that 502 as a failure; that belongs to phase 4, which already owns this trap, and it is
carried there rather than patched here. Nothing else in the chain is affected: the decision, the
re-read, the keystroke and the record all happened, and only the last sentence of the record is wrong.

### What this row does NOT prove

The session was a plain shell that had been made to PRINT an allowance notice. It was not a coding
agent that had genuinely exhausted a model allowance. This row proves the MECHANISM end to end - a
screen, a rule, a decision, a keystroke, a record - and it does not prove the recovery of a real
provider limit. That is row 3, and it is not to be faked.

## 3. The real case - a session blocked on a provider limit recovers with nobody watching

Verified by a COMPLETED TURN, not by an endpoint's own response and not by the reported current model
alone, which is turn-end truth and lags a slash-command switch.

**PENDING.**

## 4. The negative control - where the boundary is

**BOTH PROVED, live, on commit `73273a457`, on the same session and the same rig as row 2.**

Neither is proved by an absence. A rule that did nothing because the evaluator threw looks exactly
like a rule that read the screen and declined, so each decline below is proved by the PRESENCE of its
recorded firing, with the screen it saw, what it understood, and why it refused - and only then by the
fact that nothing was typed.

### N1 - a session merely DISCUSSING a usage limit is NOT convicted

This is the sharpest test in the mission, because the trigger words are identical: `limit` and
`usage-credits` are both on this screen, so the free checks let it through and the rule reached the
agent as a candidate. Only reading the screen against the instruction can tell the two apart.

**The rule was LIVE when this ran**, not in dry run - it had already typed into this session ninety
seconds earlier (row 2). Nothing but its judgement stood between this screen and a keystroke.

The screen:

```
I am writing documentation for our runbook.
The example notice I need to quote reads - You've reached your Fable 5 limit. Run /usage-credits to continue.
That line is an EXAMPLE in a document. This session is not blocked and has plenty of allowance left.


C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
...
```

The recorded firing:

```
rule:           e34f821a-d59d-4940-b7d1-9c5c8938faf0   (state: live)
occurred:       2026-09-02T20:20:34.8199316Z
decision:       decline
understanding:  The session is writing documentation and quoting an example notice about a model
                limit. The screen explicitly states this is an example and that the session is not
                blocked and has plenty of allowance left.
reason:         The model-allowance language on the screen is content being written into a document,
                not the session's own report of its state. The instruction targets a session that has
                actually stopped due to an exhausted model allowance, which this is not.
typed:          ""
outcome:        declined - nothing was typed.
```

```
16:20:15.642 [RuleEvaluator] sid=0234084a-...: 1 rule(s) worth asking about
16:20:34.819 [HostedInferenceBrain] chat/completions model=devthrottle/wingman OK: 595 chars in 19.2s
16:20:34.819 [RuleEvaluator] firing: rule=e34f821a-... decision=decline typed=no
16:20:34.823 [SessionRuleStore] RecordFiring: rule=e34f821a-... decision=decline typed=no
16:20:34.823 [GatewayHost] turn-end rules: sid=0234084a-... outcome=declined
```

A fixed list of trigger words would have convicted this screen. The judgement is what did not.

### N2 - a rule DECLINES a screen its instruction does not cover

A second stored rule, whose trigger words are on the screen and whose instruction plainly is not
about it:

```
id:                  ade51b9f-4cde-4292-94aa-4d678a9dfb49
the account said:    If a build failed because of a compiler error, type the command that shows me
                     the first error.
trigger words:       failed, error
```

The screen carries `failed`, so the free checks let it through:

```
Deleting temporary files failed - the file is in use by another process.
Nothing was built and no compiler ran.

C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
...
```

The recorded firing:

```
occurred:       2026-09-02T20:21:20.2197026Z
decision:       decline
understanding:  The screen shows a failed build attempt, but it explicitly states 'Nothing was built
                and no compiler ran.' The failure was in deleting temporary files (file in use by
                another process), not a compiler error.
reason:         The instruction applies only when a build stopped on a compiler error. This screen
                reports that no compiler ran at all, so there is no compiler error to show.
typed:          ""
outcome:        declined - nothing was typed.
```

### The free checks turning rules away, live, with reasons

Not staged; taken from the same run. Every rule that did not reach the agent said why it did not:

```
16:18:21.545 [RuleEvaluator] no-candidates: none of the words this rule watches for are on the
             screen: failed, error. this rule acted on this session 10 seconds ago and waits 20
             seconds between acts on one session.
16:17:07.477 [RuleEvaluator] stopped-before-any-rule: the screen has not changed since this session
             was last looked at
```

The first line accounts for BOTH stored rules by name in one pass - one turned away on its words, the
other on its cooldown - and neither reached a model. The second is an unchanged screen costing one
screen read and nothing else.

### Rule e34f821a's whole record, as the store returns it

```
2026-09-02T20:20:34.8199316Z  decline  typed=""
2026-09-02T20:18:11.5216610Z  act      typed="/usage-credits"
2026-09-02T20:16:13.4132778Z  act      typed=""            (dry run)
```

Three firings: what it would have done, what it did, and what it refused to do.

## 5. How to write a rule

Short, plain, from the real screen.

**PENDING.**

## 6. What is NOT proven

To be filled in honestly at the end, and never left empty.

As of the end of phase 1: rows 1 to 5 above are all still PENDING. Nothing has fired, nothing has
been typed, no model has built a rule from English, and there is no user interface. Phase 1's own
list of what it did not prove is in `phase-1-report.md` and is carried forward here rather than
restated.

---

## Phase 1 - the rule store, the contract, the primitives (done)

Full account, with every red quoted before its green: `phase-1-report.md`. Branch
`mission/session-rules-p1`, head `48eeb1e83`.

What phase 1 proved, all on `48eeb1e83` unless a red is named:

- A rule ROUND-TRIPS through the store - the account's sentence, the derived screen description, the
  trigger words, the derived checks, scope, cooldown, daily cap and state - read back through a
  second store over the same database.
- A rule naming a check that does not exist is REFUSED at write time with a stated reason, proved by
  writing a refused rule and reading the reason, and nothing is stored.
- A rule handing the wrong arguments to a real check is REFUSED with a stated reason - thirteen
  distinct wrong-argument cases.
- The check registry is DERIVED by reflection, non-empty, and complete: the test finds the attributed
  methods with its own independent scan and requires every one to be reachable through the registry.
- The five checks have their own tests, including `is_path_inside` against `..`, a real link, and the
  prefix collision `repo-other` beside `repo`.
- A new rule is ALWAYS in dry run - `Create` has no state parameter - and a dry-run rule cannot record
  having typed anything.
- Nothing in the rules code can type into a session, proved by a reference scan of the built assembly
  whose scanner was first run against a known-BAD input and watched to fail by name.

What phase 1 did NOT prove: the parked `CcDirector.Gateway.Tests` suite did not run (the machine-wide
lock was held, and that suite measures 48.88 minutes against a 45-minute maximum wait, so a queued run
cannot acquire it); nothing ran against a live Postgres; no rule has fired; no model has built a rule
from English; and the decline is stored but not yet earned by an agent.

---

## Phase 2 - the thin vertical slice (done)

Full account, with every red quoted before its green: `phase-2-report.md`. Branch
`mission/session-rules-p2`. The live runs below were all made against a Gateway built from
`73273a457` and reporting `2.0.4+73273a4570da4d54e3972de45bbb1a1ebca9236b` on `/healthz`.

The five acceptance rows phase 2 owed:

| Row | Where it is proved |
| --- | --- |
| 1. Demonstration A, captured as an artifact | Row 2 of this report - live, with the screen before, the rule, the decision, the keystroke and the screen after |
| 2. N1 - a session DISCUSSING a limit is not convicted | Row 4 of this report - live, by a rule that was already LIVE and had typed ninety seconds earlier |
| 3. N2 - a rule DECLINES a screen its instruction does not cover, as a RECORDED FIRING | Row 4 of this report - live, with the record quoted |
| 4. Dry run types nothing, by an instrumented send seam counted at zero | Below, plus the live dry-run firing in row 2 |
| 5. The screen is re-read immediately before acting, and a changed screen is abandoned | Below - live, and in the unit suite |

### Row 5, live: a screen that moved on between the decision and the keystroke

Staged deliberately and then observed for real. The provider notice was put on the screen; the
session went idle; the evaluator woke and asked the agent; and WHILE the model was thinking, a
different keystroke was sent into the session from outside, changing the screen underneath the
decision.

The agent decided to act - its understanding is on the record - and nothing was typed:

```
rule:           e34f821a-d59d-4940-b7d1-9c5c8938faf0   (state: live)
occurred:       2026-09-02T20:23:18.3898418Z
decision:       abandoned
understanding:  The session has stopped on a provider notice stating the user has reached their
                Fable 5 model limit, and it suggests running /usage-credits to see remaining credits
                or /model to switch.
reason:         the screen changed between the decision and the keystroke, so the decision was about
                a screen that is no longer there and nothing was typed.
typed:          ""
outcome:        abandoned - nothing was typed.
```

```
16:23:07.767 [RuleEvaluator] sid=0234084a-...: 1 rule(s) worth asking about
16:23:18.389 [RuleEvaluator] firing: rule=e34f821a-... decision=abandoned typed=no
16:23:18.393 [GatewayHost] turn-end rules: sid=0234084a-... outcome=abandoned
```

The screen afterwards carries the interfering keystroke and NOT `/usage-credits`:

```
[run 5] the provider notice again
You've reached your Fable 5 limit. Run /usage-credits to continue or switch models with /model.C:\...\scratch>
C:\...\scratch>echo THE SCREEN HAS MOVED ON WHILE THE RULE WAS THINKING
THE SCREEN HAS MOVED ON WHILE THE RULE WAS THINKINGC:\...\scratch>
```

The abandonment is proved by the PRESENCE of that record with its reason. The absence of
`/usage-credits` from the screen is corroboration, not the proof.

### Row 4: dry run types nothing, counted at the send seam

Two independent things, because the second one is the kind that fails open.

**The seam is counted at zero.** `RuleEvaluatorTests` wires a fake environment in which there is
exactly ONE method that can type, and counts every call to it. With the rule in dry run and the agent
answering "act", the count is zero AND a firing is recorded saying `act`, `typed=""`, and
`dry run: nothing was typed. It would have typed: /usage-credits`. The count alone would pass just as
happily if the evaluator had crashed before reaching the send, which is why the record is asserted
beside it.

**The structure, read out of the BUILT assembly.** `RulesTypeNothingGuardTests` reads the compiled
metadata with Mono.Cecil and requires that EXACTLY ONE type in the rules namespace reaches the prompt
verb, that it is `GatewayRuleEnvironment` by name, and that `RuleEvaluator` - where the dry-run
decision is made - cannot reach it at all. The scanner is proved on a known positive first (the
session supervisor's wiring, which really does type), so an empty result fails rather than certifying
a run that looked at nothing.

The live dry-run firing in row 2 is the same property observed on a real session.

### What phase 2 did NOT prove

- **No authoring conversation.** The rule's derived parts were written by hand. Row 1 of this report
  stays PENDING until phase 3.
- **No user interface.** Rules are read and written over `/gateway/rules` only.
- **The rule's own stored check did not run.** A rule stores the checks derived for it, but the
  evaluator runs the checks the AGENT names in its reply (Architect ruling A5), and on all five live
  screens the agent named none. So the check-running path was proved in the unit suite and NOT on a
  live screen. The stored `matches_any(text=<screen_text>, terms=limit,allowance)` round-tripped
  through the store and was never executed.
- **The session was a plain shell.** See the end of row 2: this is the mechanism, not a recovery from
  a real provider limit. Row 3 stays PENDING.
- **One machine, one tenant, SQLite.** Nothing ran against Postgres and nothing ran hosted.
- **`CcDirector.Gateway.Tests` did not run** - it is parked and host-bound.

---

## Runs

Every number carries its exit code and the commit it ran on. A run is only evidence for the tree it
ran on.

| What ran | Commit | Exit code | Result |
| --- | --- | --- | --- |
| Rules tests, first run against unwritten code | `a8259bcbb` | 1 | 33 failed, 1 passed (the one pass is the instrument check) |
| Rules tests, after the checks and registry | `84c25911e` | 0 | 34 passed, 0 failed |
| Validator tests, first run against unwritten code | `84c25911e` | 1 | 18 failed, 0 passed |
| All rules tests, after the validator | `5523025ec` | 0 | 52 passed, 0 failed |
| Store tests, first run against unwritten code | `522b1cee5` | 1 | 21 failed, 0 passed |
| Store tests, after the store | `515759985` | 0 | 21 passed, 0 failed |
| Types-nothing guard against a known-BAD input | `c991921d2` | 1 | 1 failed, 2 passed - named the offending type |
| All rules tests, probe removed | `7a7422119` | 0 | 76 passed, 0 failed |
| Local gate, which caught the tenant-key defect | `7a7422119` | 1 | 1 failed, 3310 passed |
| Tenant guard and rules tests, after the key fix | `48eeb1e83` | 0 | 81 passed, 0 failed |
| Local gate, `scripts/test-local.ps1` | `48eeb1e83` | 0 | all 9 projects Completed; 4604 passed, 2 skipped |
| `has-pending-model-changes`, SQLite | `48eeb1e83` | 0 | no changes since the last migration |
| `has-pending-model-changes`, Postgres | `48eeb1e83` | 0 | no changes since the last migration |
| Parked `CcDirector.Gateway.Tests` | | | **PENDING** - machine-wide lock held; see phase 1 report |
