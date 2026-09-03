# The three demonstrations - what the QA report must show, and how to capture it

**This is the mission's deliverable.** Everything else exists to make these three possible. The
owner's words:

> Implement three rules and produce a QA report with screenshots that shows, for each, how it was
> implemented, how the rule was set up, and then the rule being USED and TRIGGERED so we can see it
> actually working.

Three scenarios, each needing **set-up shown** and then **triggered shown**. A row with one and not
the other does not pass.

Read `demonstration-rig.md` first - it is the mission's own rig recipe, written before phase 2 and
already executed once successfully. This brief adds the three scenarios and what each one must not be
allowed to fake.

---

## The rig - reuse it, do not redesign it, and do not deploy to production

Established and proven in `qa-report.md` section 2:

| Part | What it is |
| --- | --- |
| The Gateway | Built from the branch, on its OWN data root and port, so it never touches the owner's Gateway |
| The Director | A real Director on slot **6 or above**, connected over the ordinary Director stream. Slots 1 to 5 and the installed app are the owner's and are never touched |
| The session | A real `RawCli` session running `cmd` - a plain shell, deliberately, so a command typed into it either ran or it did not and the screen says which |
| The screen read | The real `screen-grid` verb over the tunnel |
| The trigger | The real Working-to-idle transition. **Nothing is nudged** |

`scripts/phase2-gateway-proof.ps1` is a worked example of standing up an isolated Gateway and
Director from a worktree on their own roots and ports; read it rather than inventing the plumbing.

**Never stop or force-kill one of the owner's Directors.** Stop your own with its named shutdown
signal (`Local\cc-director-shutdown-<directorId>`), which is the documented clean stop.

## What "how it was set up" means, and it is a screenshot of the page

The owner asked to see how the rule was SET UP. That means the **Rules page**, in a browser, against
the isolated Gateway's own Cockpit - the sentence typed in ordinary words, the drafted rule with its
read-back, and the rule in the list afterwards showing dry run or live. Drive it with
browser-harness. A rule created only by an HTTP call does not answer the question he asked.

The first demonstration to run should also capture the **refusal**, because it is the most persuasive
single screenshot in the report: ask for a rule whose trigger words are not on the captured screen and
show the Gateway naming the invented word and refusing. That is the grounding check visible.

---

## Scenario A - the usage limit. The headline.

**"Waiting until it resets, then continue."** This needs Phase 2 (the clock). Without it there is no
wait, only an immediate firing, and the scenario is not proven.

| Step | What the screenshot must show |
| --- | --- |
| Set up | The Rules page: the rule authored from the REAL limit screen, trigger words taken off it, scoped to one agent, stored as a dry run |
| Trigger | The session stopped on a usage-limit notice that states a reset time |
| Wait | The firing record: decided to act, read the reset time with `retry_delay_from`, waiting until then - **typed nothing yet** |
| Act | After a person promoted it: the wake at the reset time, `continue` typed by nobody, and the screen afterwards showing it accepted |
| Proof | The whole chain in the record: screen, understanding, decision, reason, wait, keystroke, outcome |

**The honest limit on A, and it goes in the report in these words.** A plain shell that PRINTS a
limit notice is not a session that has genuinely exhausted an allowance. A proves the MECHANISM end to
end - the wait, the wake, and the keystroke. It does not prove recovery from a real provider limit.
If a genuine limit happens to occur on any session during this mission, capture it and prove recovery
by a COMPLETED TURN afterwards - not by the prompt endpoint's own response. If none occurs, that row
says NOT PROVEN in those words. **Do not substitute a printed line for a real block.** An unproven row
stated plainly is worth more than a proven-looking row that is not true.

## Scenario B - the provider outage

**"Wait and start it back up."** This does not need the clock: the shipped cooldown and daily cap
already bound a wait-and-retry.

| Step | What the screenshot must show |
| --- | --- |
| Set up | A rule authored from a real provider-error screen: wakes on the error text, waits, then continues |
| Act | The session stopped on an API error; the rule waits the cooldown, types continue; the record shows both the wait and the send |
| Bound | The error PERSISTING: the rule stops at the daily cap instead of looping, with the cap-reached record. **This is the half that is usually skipped and it is the half that shows the feature is safe to leave running** |

## Scenario C - the negative control. The one that proves it judges.

**A LIVE rule declines a screen that carries its own trigger words but is not the session's own
state.** Two things about this row are not negotiable:

1. **The rule must be LIVE, not a dry run.** A dry-run rule types nothing anyway, so a dry-run decline
   proves nothing about the boundary. The owner asked for a live rule declining.
2. **The decline is proven by the PRESENCE of its record**, never by the absence of a keystroke.
   Silence is not a decline: a rule that did nothing because the evaluator crashed looks exactly like a
   rule that declined, unless the decline is written down with its reason.

| Step | What the screenshot must show |
| --- | --- |
| The trap | A live rule whose trigger words are on the screen - but in documentation the session is READING, not in its report of its own state. This brief on a screen is itself a valid trap |
| The decline | The firing record: read it, DECLINED, and said why - in the shape of "the words are in something the session is reading, not its report of its own state" |
| Why it matters | A rule that never declines has not been shown to have a boundary. This is what a fixed word list could never do |

Also keep the second half already proven as row 4 of `qa-report.md`: a rule declining a screen its
instruction plainly does not reach.

---

## What the report must say about itself

- **Every red before its green.** A test that has never been watched failing is decoration.
- **Every claim tied to the commit it ran on**, and the exit code.
- **An honest list of what was demonstrated versus what was only unit-tested.** This is the mission's
  own standard and the owner asked for it explicitly.
- **Captures go straight into `qa-report.md` as they happen**, never held back to be written up later.
  A capture written up from memory is a summary, and the mission's own rule is to read the artifact
  and never the summary.

## The gate

- ASCII only, except where a captured screen genuinely contains other bytes - keep the capture
  faithful rather than tidied, and say so.
- No mention of any assistant, model, vendor or AI tool in a commit message, a document or a comment.
  Naming a MODEL as a subject of measurement is required where the report compares them.
