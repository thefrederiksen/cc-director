# Finding G3 - the cost investigation found the feature already built, cheaply, twice over

**Asked by the owner on 2026-09-04: what would it cost to run the smart model on every turn end?**
The answer turned out to matter far less than what was found while measuring it.

## 1. The frequency, measured

From the retained turn-brief records (`gateway-turnbriefs/*.jsonl`):

| | |
| --- | --- |
| Turn ends recorded | 1,287 over 6 days |
| Average per day | about 215 |
| Busiest day | 412 |
| Distinct sessions | 154 |
| Turns per session | median 5, maximum 44 |

**This is a FLOOR, not a ceiling.** It is a retained June sample, briefs are only written for sessions
where the wingman is on, and the fleet has grown since. Nobody should quote it as the current rate
without re-measuring.

## 2. One turn-end event, TWO consumers, neither aware of the other

`GatewayHost` fans a single `TurnEndSignal` out to two independent places:

```
_sessionSupervisor?.OnTurnEnd(signal);                              // GatewayHost.cs:2599
_ruleLauncher?.OnTurnEnd(tenant, signal.DirectorId, signal.SessionId); // GatewayHost.cs:2610
```

Each then reads the session's screen for itself and decides for itself. That is the duplication the
owner sensed from the outside without having seen the code.

## 3. The turn brief ALREADY answers "does this session need me"

`needsYou` is a field on every turn brief, and it is not a flag. Its shape, read off the real records:

```
statement, options, urgency, confidence, evidence, answerVia, submit, selectionMode,
railLine, ifIgnored
```

774 of 1,287 briefs carry one. The capability the owner asked for on 2026-09-04 - "look at whether the
session actually needs the user, because a lot of them are just giving information or controlling
another session" - is largely built. It is simply built by a different call, and nothing connects it
to the rules engine.

## 4. THE HEADLINE: `SessionSupervisor` already does the rules engine's scenario, for almost nothing

Issue #915, shipped. It fires on the same Working-to-idle signal, and:

- It classifies the screen with a **pure deterministic table first** -
  `TerminatingFaultClassifier` - and reaches a model **only** for an error nothing in the table
  recognises. Its own words: *"One live-screen read per idle transition, and nothing else on the
  common path: no timer, no poll, no model call ... The model is reached only for a turn that ended on
  an error nothing in the table recognizes - a rare minority of a minority."*
- It waits, re-reads the activity state immediately before every send, refuses when a menu owns the
  screen, and then sends `ContinueText = "continue"`.
- It counts attempts against a **fault EPISODE** rather than a turn, precisely so the Working flicker a
  failed continue produces cannot reset the ceiling forever.
- It escalates to an owner email and writes a recovery-log line for every detection, wait, send,
  recovery and escalation.

**That is scenario B of this mission, already in production.**

### The classifier already solves our negative cases, deterministically

`TerminatingFaultClassifier` is not a naive substring search. It carries the exact defences our 32-case
corpus was built to exercise:

- it looks only at the last few lines of **real content**, because *"a screen that merely REMEMBERS an
  old error must never be sent a continue"*;
- ambiguous words require an error marker beside them, so *"a session whose agent happened to PRINT the
  words 'connection refused' while discussing a log is never typed into"*;
- signatures that cannot occur in ordinary prose - the errno codes, `rate_limit_error`, `socket hang
  up` - stand on their own;
- and it already lists `usage limit reached`, `context limit reached`, `429`, `overloaded`.

Those are our negative classes - n01 through n22 - handled in a table, at zero cost, with tests.

## 5. So the cost answer is not about the model at all

| Path | Model calls per idle transition |
| --- | --- |
| `SessionSupervisor` (shipped) | **zero on the common path** |
| Rules engine (this mission) | **one on every changed screen with a rule in scope, plus a second whenever the first says act** |

The rules engine is the outlier, and it is the outlier because it was designed as a model-first
pipeline while the supervisor beside it was designed as a table-first one. At a few hundred turn ends a
day, that is the whole cost, and it is self-inflicted.

**The owner's own proposal - "it does a word search first, and then it will run wingman" - is not a new
idea to design. It is the shape already shipped next door, tuned, tested, and demonstrably aware of the
confusions that beat our model.**

## 6. What the two features are NOT

They are not the same feature and this must not be collapsed carelessly:

- The **supervisor** recovers from faults the PRODUCT knows about. Its signatures are ours, its text is
  always `continue`, and an account cannot change any of it.
- **Rules** let an ACCOUNT say, in its own words, what to watch for and what to type. The trigger is
  arbitrary, which is exactly why a model is in the loop at all.

The overlap is the spine - the turn-end trigger, the screen read, the fault gate, the wait, the
re-read, the menu refusal, the episode ceiling, the recorded log. Rules adopted none of it and rebuilt
a thinner version of each.

## 7. The recurring habit this exposes, and it is the mission's real lesson

Three missions have now hung something off the same turn-end signal, each reading the screen again and
each deciding again. Nobody looked next door first. The evidence that this is a habit rather than an
accident: **the codebase already raises the model deadline where judgement matters and nobody is
waiting** - the dictionary scan uses three minutes and the history summariser ninety seconds, both with
a written reason - while the rules path silently inherited a sixty-second default borrowed from the
voice conversation. The answer to phase 1 was sitting two files away the entire time.

## 8. What this does NOT settle

- Whether the classifier's window and marker rules hold up on the rules corpus. It was tuned for
  transport faults, not for authored instructions. **It must be run against the 32 cases before any of
  this is believed** - that is a harness run, and the harness already exists.
- The real current turn-end rate, per section 1.
- Whether the brief call and the rules call can share one model call. They differ today: the brief uses
  the FAST role deliberately, the rules judgement needs the thinking role. A shared call is a design
  question, not a merge.

---

# PRODUCTION EVIDENCE, read 2026-09-04 from the hosted Gateway's own log

Section 4 above was read off the code. The owner asked the right question - is it actually running,
and does it work? - so it was checked against the running service rather than argued. The log was read
through Kudu on `devthrottle-gw` at `/home/gateway/cc-director/logs/director`.

**Instrument note, confirming a known fault:** today's log is reported by `ls` as **0 bytes** and by
`wc -c` as **148,922,106 bytes**. Never take the directory listing's size as evidence that a hosted log
is empty.

## The supervisor is LIVE and it WORKS

101 supervisor lines across 3 and 4 September:

| Event | Count |
| --- | --- |
| `supervisor-fault-detected` | 24 |
| `supervisor-waiting` | 27 |
| `supervisor-continue-sent` | **8** |
| `supervisor-recovered` | **19** |
| `ESCALATED` | 4 |

By cause: `rate-limited` 32, `transient-transport` 22, `non-recoverable` 8, `working-observation` 19
(the recovery observation), `unknown` 1.

A complete episode, verbatim, in twelve seconds of wall clock:

```
12:48:41 supervisor-fault-detected  cause=transient-transport
12:48:41 supervisor-waiting         cause=transient-transport
12:49:29 supervisor-continue-sent   cause=transient-transport
12:49:29 supervisor-waiting         cause=transient-transport
12:49:41 supervisor-recovered       cause=working-observation
```

**It detected a fault, waited, typed `continue`, and observed the session go back to work.** That is
this mission's scenario B, in production, on the owner's real fleet, two days ago.

**And it already handles the usage-limit case:** `cause=rate-limited` is the most common cause in the
window, 32 of the tagged lines.

## The cost, measured rather than estimated

Only TEN model calls in two days - the entire set of `verdict=` lines: `transient_recoverable` 5,
`needs_human` 4, `healthy_done` 1. Everything else was decided by the word table for nothing.

Against that, the real turn-end rate, taken from the rules engine's own per-turn line: **775 on
3 September and 403 by mid-afternoon on 4 September.** So the June figure of about 215 a day in
section 1 was a floor and it was low by roughly three and a half times.

| | Model calls |
| --- | --- |
| Supervisor, measured | **10 in two days**, against ~1,200 turn ends |
| Rules engine, as designed | one per changed screen with a rule in scope, plus a second when the first says act |

That is the cost answer, and it is about two orders of magnitude. It is not a model-choice problem.

## The rules engine is DEPLOYED, RUNNING, and doing nothing

It is armed in production and it evaluates every turn end. Every single evaluation on 4 September says
the same thing:

```
403 [RuleEvaluator] no-rules: this account has no rules.
```

**Nobody has ever created a rule, because the authoring half has never been merged.** The engine that
runs rules shipped; the half that lets a person write one is still sitting on a branch. That is why the
feature looks like nothing at all from the outside - and it is the single clearest statement of where
this mission actually stands.
