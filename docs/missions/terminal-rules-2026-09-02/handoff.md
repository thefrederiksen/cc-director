# Terminal Rules - Architect's handoff note

The one file a fresh Manager needs. Kept current by the Architect; everything this seat knows
is reconstructable from here plus `brief.md`, `phase-0-proofs.md` and `rulings/`.

Last updated 2026-09-02, mid phase 0.

## Where the work lives

- Branch `mission/terminal-rules`, worktree `D:\ReposFred\devthrottle-terminal-rules`.
- Issue: thefrederiksen/devthrottle#2644. Motivating case: devthrottle_internal#1619.
- **Nothing has landed on `main` and nothing may.** Landing is the Architect's act alone, and it
  waits on #2643 (ruling 6).

## Read these three, in this order

1. `cc-devthrottle workflow instructions mission` - the conduct.
2. `brief.md` - the work and the owner's settled rulings.
3. `rulings/` - ten architect rulings. They are settled; do not re-litigate them. The four that
   bind hardest:
   - **r1** - two questions, two methods. History reads freely from the store; live-truth reads need
     byte-equality AND a connected tunnel AND a fresh snapshot, else tunnel, else Unreadable.
   - **r3** - every proof must fail when the thing it measures never ran. Apply that test to each new
     proof *before* writing it.
   - **r6** - the migration is provisional; holding a snapshot locally is not landing one on main.
   - **r7/r9** - never hand-keep a list of the thing. Derive it, and invert any allow-list into an
     exception list that must be argued for.

## Phase 0 state

Eight rows in `phase-0-proofs.md`. Row 0 is the Director-side seam; rows 1-7 the acceptance set.
The Manager's own report is the current truth on which are proven - read the file, not this line.

**Two labels travel with every phase 0 result, together:**

> proven against the mapped model, not the migrated schema; and proven from the store inwards,
> with the push path unexercised except by row 0, which stops at the sink contract.

**Owed the moment #2643 lands** (rulings 6, 8, 9):

1. Re-run the **corrected** sweep in `r2`'s amendment and say what it returned - never infer the slot
   is free from #2643 merging.
2. Rebase, delete our migration, regenerate on the new snapshot, and run
   `has-pending-model-changes` to *no changes* on **both** providers.
3. Re-run the full gate and **every** proof row, rows 4 and 7 included - they were proven against a
   migration that will have been replaced.
4. Delete the throwaway `EnsureCreated` instrument, as `StatsConcurrencyTestDb` did.

## Phase 1 - do not start it until phase 0 is closed

Phase 1 is the rule store and the rule contract: storage, CRUD, validation, **dry-run only, nothing
typed into any session**. The rule shape is in `brief.md`.

It does not start on the strength of phase 0's provable rows, because it would be built on a schema
the pending-model-changes check has not yet confirmed against the regenerated migration (r4, r6).

Its acceptance row, stated now so it is not invented later: *a rule matching the real limit screen -
the fixture at `fixtures/blocked-session-101-screen-tail.txt` - is recorded as would-have-fired, and
no keystroke was sent.* The second half is the proof; the first half alone passes on a rule engine
that types into everything.

## Standing constraints

- Every action a rule can take is something a person could have typed into that session. No shell
  execution, ever.
- The judge answers with a **rule id from a closed set, or none**. It never supplies text. Every
  character typed comes from the owner's own rule, verbatim.
- Tenant-scoped always; 7-day retention; both are the owner's rulings, not design choices.
- One tree per concurrent activity (r7). A Worker gets its own worktree off this branch. The
  Architect never builds in the Manager's tree - it has already jammed it once.

## Carry to the owner at the phase 0 report, one line each

- The parked Gateway suite was red on `main` from 2026-08-05 through the v2.0.0 and v2.0.1 tags.
- The inverted collation check found **thirteen** string key columns across five other features'
  tables with no byte-ordinal collation - recorded as inherited debt, deliberately not fixed here.

Neither is this mission's to fix. Both are his to know.
