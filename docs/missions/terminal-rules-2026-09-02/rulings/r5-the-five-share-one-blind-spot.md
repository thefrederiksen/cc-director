# Ruling 5 - the five provable rows share one blind spot; and row 6 needs a clock it can move

Architect ruling. Read after r3 and r4.

## The plan is accepted

`phase-0-proofs.md` is the standard for this mission. Ruling 3's test was applied to every row and to
the **instruments** as well - the sweep shown to return 0 and 1 so its return is known not to be a
constant, `Validate` shown to fire on malformed input, the tenant filter shown to ANSWER before it is
shown to refuse. That last one is the difference between a partition proof and a broken fixture, and
it was the row most likely to be waved through. Rows 3, 5, 6 and 7 are now conjunctions in a single
run with a positive line first. That is right.

Correcting the count from four to five unprompted is also the right instinct; a count that moves in
the honest direction is worth more than a count that stays fixed.

Two things remain.

## 1. The five provable rows contain no evidence that the push works

Row 4 notes it in passing and the consequence was not drawn:

> Every other row drives the store and the reader directly, so every other row can pass with the
> Director half never having run.

Follow that through. Rows 1, 2, 3, 5 and 6 all begin by putting a screen into the store by hand.
**Not one of them exercises `TurnReviewLogger` -> the sink -> the hub -> the store.** Row 4 is the
only row that does, and row 4 is blocked.

So the five provable rows share a single blind spot, and it is the feature's own input path. If the
push were wired to nothing at all, all five would still pass.

This does not change the plan - row 4 is genuinely blocked and inventing a substitute would be worse.
It changes what may be **claimed** from the five, and the label from ruling 4 is now too narrow. Both
halves travel together from here:

> **proven against the mapped model, not the migrated schema; and proven from the store inwards, with
> the push path unexercised.**

Nobody may say "the store works" on the strength of the five. The five say the store and the reader
behave correctly **when handed a screen**. Who hands it to them is untested until row 4 runs.

If a cheap in-process seam exists that drives the real `TurnReviewLogger` capture into the real sink
without a live Gateway - a unit-level test of the Director half alone, stopping at the sink boundary -
take it, and say exactly where it stops. Do not stretch it into a claim about the hub. If no such
seam exists cheaply, leave it and report the blind spot; naming it is worth more than a contrived
test that hides it.

## 2. Row 6 step 3 needs a clock the test can move

> Time advances past the freshness budget.

Establish HOW before the test is written, because two of the three ways are bad:

- **Injectable clock or a backdated snapshot timestamp** - correct. Set the snapshot's observed-at to
  `now - budget - 1s` and assert. Deterministic, instant, and it tests the boundary rather than
  a delay.
- **A real sleep past the budget** - a 20-second sleep in the suite, on every run, forever. Refuse it.
- **Shortening the budget in the test** - only acceptable if the budget is a real injectable
  parameter, never by editing the constant. A test that rewrites the value it is testing proves the
  arithmetic and nothing else.

Whichever holds, assert the boundary from **both sides** in that row: just inside the budget still
certifies from the store, just outside does not. One-sided, it passes on a rule that always refuses.

## Standing

Nothing here blocks the Worker. Rows 1, 2, 3 and 5 are unaffected; row 6 needs the clock question
settled before its test is written; and the widened label applies to every report from now on.
