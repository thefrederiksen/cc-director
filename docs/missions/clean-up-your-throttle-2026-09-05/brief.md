# Mission: Clean up Your Throttle

**Status: ACTIVE.** Chartered 2026-09-05. Architect seated. Phase one (measure) not yet started.

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
