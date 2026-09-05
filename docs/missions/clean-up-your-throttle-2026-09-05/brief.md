# Mission: Clean up Your Throttle

**Status: ACTIVE.** Chartered 2026-09-05. Phase one (measure) DONE. Phase two open.
**Read the amendment at the foot of this document before acting on rule R2 - it is revised.**

**Branch:** `mission/clean-up-your-throttle` in BOTH repositories.
**Worktrees:** `D:/ReposFred/devthrottle-throttle` (product) and
`D:/ReposFred/devthrottle_internal-throttle` (mentor harness and the report).

**How this mission is conducted is not in this document.** It is in
`cc-devthrottle workflow instructions mission`. Read that first. This document describes only the
work.

---

## THE WHY

The owner's own mentor report tells him he is **59% spoken**. Your Throttle tells him he is
**92% spoken**. Both claim to describe how he drives DevThrottle, and neither page says what it is
measuring, so he cannot tell which one is describing him.

He believes 59% is too low as a description of how he actually works, and he asked directly whether
the report is counting one session's messages to another as his own prompts. He is explicit about
what counts as him: **the text box, the phone, and dictation. Nothing else.**

In his words, what he wants out of this: *the reporting should become a library that both use; the
report should be using Your Throttle as a library, so we need a Your Throttle library that is
reusable and consistently correct.*

**What is true when this mission is finished.** There is one place that answers "how does this person
drive DevThrottle", both the Cockpit page and the mentor report get their answer from it, a check
fails if they ever drift apart again, the page says which stretch of time it is describing, and the
two known ways the product mis-attributes his own words have been fixed rather than documented.

---

## The rulings

Marked **OWNER** where he said it, **INFERRED** where the Architect decided it. An inferred ruling is
the Architect's to change on evidence; an owner ruling is not.

### R1 - Your Throttle is a HOSTED-GATEWAY-ONLY feature (OWNER, 2026-09-05)

Asked whether a figure should be combined across every Gateway he drives through, he answered that
Your Throttle should only work for the hosted Gateway and should not work for an on-premise Gateway
at all.

This kills a whole branch of the problem. Both sides now read the same store, so "which Gateway is
this describing" stops being a question anyone has to answer. It also creates work: the page has to
stop pretending to answer on a self-hosted Gateway.

### R2 - The two product attribution holes are fixed INSIDE this mission (OWNER, 2026-09-05)

Asked whether to fix them here or measure them and leave them as separate work, he said fix them.
They are:

- **The chat relay.** When he sends something through the product's chat relay, it leaves stamped as
  text the *product* wrote rather than text *he* wrote, so the mentor report does not count it as his
  at all (`thefrederiksen/devthrottle#2639`).
- **Phone voice through the one-shot transcription.** When he speaks into his phone and it arrives
  through `/wingman/transcribe` rather than the durable dictation path, it is recorded as **typed**.

Both push his spoken share down. Both match the symptom he is reporting. Fixing them is what makes
the shared number *right* rather than merely *agreed*.

### R3 - The mentor report is a CONSUMER, not a second implementation (OWNER, parent issue)

Whatever shape the shared thing takes, the report asks for the answer. It does not compute its own.

### R4 - Period selector, seven-day default, the window stated on the page (OWNER, parent issue)

### R5 - The default window is a ROLLING seven days; the report's link carries its own week (INFERRED)

His stated wish is "seven days, so it lines up with the report". Those are two different things and
they cannot both be the default. The report covers a calendar week, Monday to Sunday; a rolling seven
days ending now is never that week.

The ruling: **the page defaults to a rolling last seven days**, because Your Throttle is a live
dashboard that refreshes while he works, and a Monday-to-Sunday default would show him a nearly empty
page every Monday morning. The selector offers other lengths. The page always names the window it is
showing. **The link from the mentor report opens Your Throttle on exactly the week that report
covered**, so following the link gives him the identical number rather than a different one. That
satisfies both halves of what he asked for without making either one the default.

### R6 - On a self-hosted Gateway the page says so; it does not vanish and it does not lie (INFERRED)

R1 says Your Throttle does not work there. It must therefore say that, plainly, in one sentence,
rather than disappearing from the rail (which reads as a broken build) or showing a number computed
from a store that is not the one the report reads (which is the defect this mission exists to remove).
The Gateway decides and tells the client; the client renders what it is told and works nothing out for
itself (`CLAUDE.md` rule 7).

### R7 - What cannot be attributed is EXCLUDED and DISCLOSED, never guessed into a bucket (INFERRED)

The mentor harness already does this and calls the class `unresolved`. That behaviour belongs to the
shared definition, not to one side of it. A share computed over a subset must publish the size of the
subset beside it.

### R8 - The headline unit is SUBMITTED TURNS (INFERRED)

Not words, not characters. Both sides already do this today - the report's ring is the share of human
prompts, and Your Throttle's ring is the share of submitted turns - but nothing holds them there. The
library holds them there. The report's separate word-based voice figure is untouched by this mission
and stays as it is.

### R9 - The SHAPE of the shared thing is settled at the start of phase three, by the Architect, on what phase one measured (INFERRED)

Choosing it now would be guessing, and the parent issue is explicit that getting it wrong produces a
third implementation rather than one.

**The Architect's leaning, stated so the work is not rudderless:** given R1, both sides read the same
hosted Gateway, and the Gateway already computes this figure in C# next to the data. So the likely
shape is **the Gateway computes it and serves it, and the mentor report asks for it** - which is
literally "the report using Your Throttle as a library". The risk to weigh against it is that the
report's classifier can see classes of record the Gateway's turn counter cannot, and its coverage
disclosure must survive whatever shape wins. Phase one decides it.

---

## The work, in the order it lands

### Phase one - MEASURE. Changes nothing.

`thefrederiksen/devthrottle#2690`. **Nothing else in this mission starts until this exists.** The
library's whole job is to encode one answer to what counts and over what period, and that answer is
not yet known. A story that fits 59 and 92 is not evidence that it is the story.

Produces one written account, with arithmetic, at
`docs/missions/clean-up-your-throttle-2026-09-05/reconciliation.md`, carrying at minimum:

- Your Throttle's spoken share **computed over the same calendar week, in the same time zone, for the
  same account** as the report's 59%. If Your Throttle cannot express that window today, that is
  itself the finding and it is written down as one.
- **The population each side counts, as counts and not adjectives**: how many records each started
  from, how many it excluded and under which rule, how many remain.
- **Which of the two attribution holes is actually biting, in numbers.** Not in principle.
- **A verdict on the owner's own question**: is any session-to-session traffic being counted as his
  own prompts, on either side? Both sides claim by design that it is not. The account must show
  whether that claim survives contact with the week's real records.
- **What window Your Throttle's 92% is actually over**, established as fact rather than assumed.
- Whether his week's driving is split across more than one Gateway. R1 makes this moot for the
  design, but the account is dishonest without it.

**What would make this phase wrong:** explaining the whole gap by window - lifetime against a week -
without also reconciling the populations. That answers the easy half. A shared library has to agree
on *what counts*, not only on *when*.

### Phase two - the two product fixes (R2)

- The chat relay carries the person's own origin, so his own words are counted as his.
- Phone voice arriving through the one-shot transcription path is recorded as **voice**.

A fix and the guard that stops it regressing are ONE slice. Each fix is proved able to fail: revert
it, watch the guard go red with the reported symptom, restore it.

### Phase three - the library, and hosted-only

- The Architect settles the shape (R9).
- One definition of who counts and over what window, in one place, with R7 and R8 encoded in it.
- Your Throttle refuses to answer on a self-hosted Gateway, and says why (R1, R6).
- **A conformance check that fails when the two sides diverge**, run over real weeks for both
  accounts. Without it, "they agree" is a claim about today.

### Phase four - the period selector

`thefrederiksen/devthrottle#2692`. Selector, rolling seven days by default (R5), and the window
stated on the page. **The page states its window before or with the default change, never after** - a
number that quietly starts meaning something else is worse than one that was always ambiguous.

Your Throttle exists on two surfaces (`apps/cockpit/src/throttle/` and
`apps/mobile/src/pages/YourThrottle.tsx`). Both get the selector, and they do not drift.

### Phase five - the report links to Your Throttle

`thefrederiksen/devthrottle_internal#1680`, in the internal repository's worktree. One sentence, in
the place the two figures are drawn - not a banner, not a second footer - pointing at the reader's own
Your Throttle on the Gateway behind the sign-in that already gates everything else about them. Never
a public or a signed link. The email carries it too, in both parts. The link opens the page on the
week the report covers (R5).

### Then

The Architect calls an inspection by **Codex**, on the owner's explicit instruction: a different agent
family, in its own tracked session, after the builder has left. Then the Architect lands the work and
brings the owner one report.

---

## Out of scope

- Combining figures across several Gateways. Killed by R1.
- The mentor report's prose, its rubric, its spoken feedback, and its video.
- The report's separate word-based voice figure (R8).
- Restating past weeks' published figures under the new definition. The change is disclosed once,
  going forward; history is not rewritten.
- Any public or signed link to a person's Your Throttle.
- Anything that makes a third implementation of this figure.

---

## Amendment, 2026-09-05, after phase one

Phase one measured the week and refuted the premise R2 was built on. The Architect independently
verified the two load-bearing claims before ruling: `InputStats.RecordTurn` has exactly one call site
in the product (`Session.cs:2592`, inside the text path) and `Session.SendInput` genuinely never
reaches it; and `ChatService` has no construction site and no mapped route anywhere in the repository.

### R10 - R2 is REVISED. Neither named hole is what makes the number wrong.

The owner ruled "fix those two" when they were presented to him as the two things pushing his spoken
share down. On his own week they are worth nothing and at most 3.4 points. His ruling was about
fixing what makes his number wrong; applied to the facts phase one established, that is defects one
and two, not these.

- **The chat relay is NOT fixed.** `ChatService` is unreachable: nothing constructs it, no route maps
  it, and the Control API that hosted it was removed. Fixing unreachable code is theatre. Report it,
  recommend `thefrederiksen/devthrottle#2639` be closed as unreachable, and leave the code where it
  is - deleting it is a different mission.
- **Phone voice through the one-shot transcription IS still fixed.** It is real, reachable and cheap,
  and what counts as spoken belongs to the shared definition even in a week when it did not fire.

### R11 - Phase two's core is defect one and defect two

- **Defect one:** `Session.SendInput` records characters and never records the turn, so 594 of the
  week's 771 typed submissions are missing from the ring's denominator. Fix it **at the same choke
  point**: the submission event and the turn counter are written eight lines apart in the same method
  and must be written together, so that they cannot disagree again.
- **Defect two:** the store counts 2,061 of 3,279 turns more than once. **The mechanism must be
  proven before anything is changed.** Phase one names a plausible path and explicitly refuses to
  claim it. No fix lands on a hypothesis; find the cause, then fix the cause.

### R12 - The fleet-message origin is a third fix, and a small one

292 of the week's 296 fleet messages were recorded as ordinary `UserInput` with no origin rather than
as agent traffic. They are left out of his count by accident rather than by record, and the
agent-driven lane under-reports by the same 292. The right answer by the wrong road is not good enough
for a definition that has to be provably correct.

### R13 - One validated forensic repair of the stored history, or an honest truncation

The store cannot be corrected in place. Attempt ONE repair over the whole run using the same
restatement-adjudicated walk phase one proved against the submission ledger to within one turn, and
validate it the same way. If it validates, the history stands. If it does not, truncate to the repair
date and say so on the page. A repaired number that was not validated is never served.

### R14 - The page's own disclosure is false and is fixed in the same slice as defect one

`StatsPageEndpoint.NotCaptured` tells the reader that terminal typing on the desktop is counted. It is
counted in characters and not in turns, and the ring is a turn ratio. The sentence is false for the
only unit the reader sees.

### For phase three, carried forward rather than settled

Phase one's strongest structural finding is that **there are two counters at one choke point**: the
submission ledger and the Your Throttle turn tally, written in the same method, eight lines apart,
free to drift - and they did, by 28 points. R9 stays open, but the shape that removes the defect
rather than repairing it is the one where Your Throttle's figure derives from the same submission
ledger the report already reconciles against, instead of from a second tally beside it. Phase three
weighs that against what `stat_delta` gives cheaply that `activity_events` does not.

### R15 - The seven-day default lands directly. No sequencing for existing viewers. (OWNER, RELAYED 2026-09-05)

**Provenance: relayed, not heard first-hand.** Carried to this mission by the session
`devthrottle_internal - mentor` and written by it onto `thefrederiksen/devthrottle#2692`. The owner's
quoted words: *"it is okay to change the period nobody's really using a software yet it's released but
you don't have a lot of people on your throttle, so don't worry about what this will look like to
people that are already on and have used the site."* A later reader should know it reached the
Architect second-hand rather than from the owner in conversation.

The effect on phase four: **change the default to a rolling seven days directly.** No staged
sequencing, no migration note, no preserving what an existing viewer saw. The brief's phase four
caution about stating the window "before or with" the default change is withdrawn as a SEQUENCING
constraint.

**What is NOT withdrawn: the page must still say which period it is showing.** That is the owner's own
item, it stands on its own, and it is the half that makes the number readable rather than merely
narrow. It ships in phase four with the selector.

---

## R9 SETTLED, 2026-09-05, on phase two's proven mechanism

Phase two proved defect two's cause: **the aggregator concludes "all of this is new" from the ABSENCE
of a prior record.** A sound repair means the Director stating positively which incarnation of a tally
it is reporting - a change to the wire contract between the Director and the Gateway. The Manager
correctly refused to build that before this ruling, because the two answers are the same decision.

### The ruling

**The shared figure derives from the SUBMISSION LEDGER (`activity_events`, `turn-submitted`), not from
the second cumulative tally in `stat_delta`.**

One choke point already writes both, eight lines apart. The ledger is append-only and idempotent on
replay, so absence can never be mistaken for novelty and **defect two stops existing for this figure
rather than needing a fix.** Phase one validated the ledger against an independent reconstruction of
the store to within one turn, and the mentor report's classifier already reconciles against this same
ledger - so both consumers anchor on one substrate, which is what agreeing by construction means.

**The second tally is not repaired in this mission and is not trusted.** Building an incarnation token
to rescue a counter the design is replacing is the waste the Manager named. The one-line containment
that already landed stands on its own merits.

### What this costs, stated plainly rather than discovered later

- **Reach falls from ninety days to thirty.** The ledger's retention is thirty days by the owner's own
  ruling of 2026-07-24. The selector therefore offers at most thirty days, honouring #2692's rule that
  it must never offer a length the store cannot honestly answer. Thirty days of a correct number beats
  ninety days of an inflated one. **The extension, if he ever wants the reach back, is a derived hourly
  rollup folded from the ledger before the purge - noted here, NOT built now.**
- **The ledger carries no character count.** Acceptable: R8 makes turns the unit of every share, and
  characters are a supporting volume, never a ratio.
- **The ledger carries no repository.** It carries the session, and session history carries `RepoName`
  and keeps ninety days, so the repository split is a join rather than a blocker.

### What follows from it, as instructions

1. **Every figure Your Throttle presents as a count of TURNS comes from the ledger** - modality,
   surface, and the per-agent split (the ledger carries `AgentKind`), and the per-repository split
   through the session-history join.
2. **No two numbers on the page may come from different substrates without the page saying so.** The
   ring reading 57 while a tab still reads 91 about the same week is the same defect this mission
   exists to remove, one screen further down.
3. **Character volume stays on `stat_delta` and stays inflated.** Phase three decides whether to
   disclose that or drop the figure, and tells the Architect which - it is a small call and it is not
   worth a round trip to the owner, but it is worth being deliberate about. Prefer showing less and
   being right to showing more and contradicting the page above.
4. Defect two's wider fault, the incarnation token, and the Director-to-Gateway contract change are
   **explicitly OUT OF SCOPE** for this mission. Record the fault; do not build the token.

---

## Rulings closing phase two, 2026-09-05

### R16 - Character volume is DROPPED from the page, not disclosed

The Manager's recommendation, and it is right: after R9 it would be the only figure left standing on
the untrusted tally, R8 already makes turns the unit of every share, and a page that has just been
made honest must not carry one number whose own footnote says do not believe it. Drop it. If it is
ever wanted back, it comes back from a trustworthy source, not with an apology attached.

### R17 - The ledger predicate is stated EXACTLY, once, and the excluded population is disclosed

Phase three does not get to paraphrase this. The shared figure is computed over `activity_events`
rows where `EventType` is `turn-submitted` **and `InputOrigin` is present**, grouped by the origin's
modality and surface. Three consequences, all of which must be true in the code and proven by a test:

- **The 594 terminal-typed turns are IN.** They carry a null `SendSource` and a present `InputOrigin`,
  so the predicate takes them. They were never missing from the ledger - only from the tally.
- **Agent traffic is OUT by record**, now that R12 makes the fleet paths stamp the send source. Not
  out because no surface happened to resolve, which is how it was being excluded before.
- **Submissions with no `InputOrigin` are OUT and DISCLOSED as a count beside the share** (R7). On the
  measured week that population was 502 rows, and it is where agent traffic used to hide. A share
  computed over a subset publishes the size of the subset.

### R18 - The parked Gateway suite runs IN FULL before anything lands

Phase two closed with the default gate green, all four web workspaces green, and the two parked suites
merely compiling with affected tests run by name. That is not sufficient for this mission: the work is
almost entirely Gateway statistics, and `Gateway.Tests` is precisely the suite the default gate does
not run. Phase three's first act is `.\scripts\test-local.ps1 -Parked`, in full, with the result
recorded. A red there is a phase two defect, not a phase three one.

### An honesty note the final report must carry

Defect one's fix - counting a turn typed at the terminal - corrects the `stat_delta` tally, and R9 then
stops the shared figure reading that tally at all. **The 28.3 points are recovered by moving to the
ledger, not by that fix.** The fix is kept because a correct counter is better than an incorrect one
for anything still reading it, and its companion R14 disclosure correction stands on its own. But the
report must not claim the fix as the thing that corrected the number. The rest of phase two's work -
the fleet-message source and the dictation modality - lands at the submission choke point and
therefore reaches the ledger as well as the tally, so it is not affected by this.
