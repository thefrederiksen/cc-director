# Session Rules - the demonstration rig

Written by the Architect while phase 1 was being built, so that phase 2 can execute it the moment
the store lands rather than designing it then. This is the rig that produces the owner's acceptance
test as an ARTIFACT.

> Put words on a terminal screen. Something happens automatically because a rule said so.

Ruling A9 says get this captured EARLY and crudely rather than polished and late. This file says
exactly how, and - just as importantly - what each demonstration does NOT prove.

---

## Demonstration A - the headline, and it is fully real

**This is the owner's literal test and it is the one that must exist.** Everything in it is real: a
real session, a real terminal screen read over the tunnel, a rule read from the real store, the real
prompt route, and a real recorded firing. The only crude part is that there is no user interface yet
and the rule is created through the store rather than through a conversation.

**The session.** A `RawCli` session running `cmd` - deliberately not a coding agent, because a plain
shell makes the cause and effect impossible to argue with: a command typed into it either ran or it
did not, and the screen says which.

    cc-devthrottle session spawn "<a scratch directory>" --agent RawCli --command cmd --name "Session Rules - demonstration target"

**The rule**, stored before the demonstration begins, in dry run first and then promoted, so the
promotion itself is on the record:

- The instruction, in English: *"If a session's screen says it has run out of its model allowance,
  type the command that shows me what is left."*
- The derived trigger words, chosen by the model, not by us.
- Scope: this one session.
- Cooldown and daily cap: both set, both small.

**The steps, in order, each one captured.**

1. **Capture the screen BEFORE.** Read it and store the text. This is the baseline and it must be
   quoted in the report.
2. **Put the words on the screen.** Type a line into the session that makes the notice appear on the
   terminal - the exact wording from the real blocked session is in
   `devthrottle-terminal-rules/docs/missions/terminal-rules-2026-09-02/fixtures/blocked-session-101-screen-tail.txt`,
   whose operative line is: `You've reached your Fable 5 limit. Run /usage-credits to continue or
   switch models with /model.`
3. **Let the session go idle.** The evaluator hangs off the working-to-idle transition. Nothing is
   nudged; the transition happens on its own.
4. **The rule fires on its own.** Free checks pass, the agent reads the screen and the instruction
   together, decides, and the act types into the session.
5. **Capture the screen AFTER**, and the firing record: what was on screen, what the agent
   understood, what it decided and why, which primitives ran with what arguments and what they
   answered, what was typed, and what happened next.

**What A proves:** words went onto a terminal screen and something happened automatically because a
rule said so, with the whole chain recorded. That is the mission's acceptance test.

**What A does NOT prove**, and the report says so in these words: the session was a plain shell, not
a coding agent that had genuinely exhausted a model allowance. A proves the MECHANISM end to end. It
does not prove the recovery of a real provider limit - that is demonstration B.

---

## Demonstration B - the real case, and it is not to be faked

A session genuinely blocked on a provider usage limit recovers with nobody watching, verified by a
COMPLETED TURN - not by the prompt endpoint's own response, and not by the reported current model
alone, which is turn-end truth and lags a slash-command switch.

**This one cannot be manufactured honestly.** A real limit notice is the session's terminal STATE.
Making the words appear by asking a healthy session to print them produces the negative control
below, not this. If the two are confused, the demonstration proves the opposite of what it claims.

So:

- **If a genuine limit occurs** on any session during this mission - and the owner's weekly allowance
  was at 82 per cent when it started, so one may - capture it: the screen, the rule that matched, the
  typed `/model opus`, and then a COMPLETED TURN afterwards as the proof of recovery.
- **If no genuine limit occurs**, B is reported as NOT PROVEN, in those words, with what IS known
  stated alongside it: demonstration A proves the mechanism end to end, and the injection route
  itself was already proven separately in devthrottle_internal#1619, in both directions, with a
  negative control showing the fleet-message route cannot do it.

**Do not substitute a printed line for a real block.** An unproven row stated plainly is worth more
than a proven-looking row that is not true.

---

## The negative control, and it is not optional

A report showing only successes has not shown the feature has a boundary. Two rows, both required.

**N1 - a session merely DISCUSSING a usage limit is not convicted.** Put the same words on a screen
in a context where they are conversation rather than the session's state - a session reading this
very document, or one whose own output quotes the notice. The rule must NOT act, and the record must
say why it did not. This is the sharpest test in the mission, because trigger words alone cannot
tell the two apart; only reading the screen against the instruction can.

**N2 - a rule DECLINES a screen its instruction does not cover.** Give a stored rule a screen that
matches its trigger words but that its instruction plainly does not reach, and require a recorded
decline with a stated reason. A rule that never declines has not been shown to have a boundary.

Both declines are recorded firings, not silence. **Silence is not a decline** - a rule that did
nothing because the evaluator crashed looks exactly like a rule that declined, unless the decline is
written down. Prove the decline by the PRESENCE of its record, never by the absence of an action.

---

## How each capture is stored

Straight into `qa-report.md` as it happens, never held back to be written up later:

- the screen before, quoted verbatim;
- the rule that matched, quoted as the sentence the account said;
- what the agent understood and decided, and its reason;
- which primitives ran, with their arguments and their answers;
- exactly what was typed;
- the screen after, quoted verbatim;
- the commit the run was on, and the exit code.
