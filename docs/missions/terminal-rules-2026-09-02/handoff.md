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
   - **r1 as amended by r12/r13 - READ THE AMENDMENT, NOT r1 ALONE.** r1 originally let a live read
     be answered from the store when three freshness facts held. **That mechanism is deleted.** The
     inspection established that the first fact could not mean what its name said, and the fix round
     showed it never bought anything: the store could only answer while the tunnel was connected,
     which is exactly when the tunnel could have answered. So: `ReadStored` answers history from the
     store, and a live read ALWAYS goes to the tunnel. A live read never returns a stored screen.
   - **r3** - every proof must fail when the thing it measures never ran. Apply that test to each new
     proof *before* writing it.
   - **r6** - the migration is provisional; holding a snapshot locally is not landing one on main.
   - **r7/r9** - never hand-keep a list of the thing. Derive it, and invert any allow-list into an
     exception list that must be argued for.

## Phase 0 state - IN A FIX ROUND, NOT COMPLETE

Phase 0 was reported complete with a green gate. An independent inspection
(`inspection-01.md`) then found six real defects, each with a reproduction: three high, two medium,
one low. **The account boundary was attacked specifically and HELD** - that is the owner's hard
requirement and it is intact. Nothing else survived unchanged.

The fix round is governed by `rulings/r12` and `r13` and planned in `fix-round/plan.md`.

**What phase 0 delivers, after the fix round's decision:** a session's turn-end terminal screen,
pushed to the Gateway, stored per account for seven days, and readable from anywhere including while
the owning machine is offline. The live-read optimisation is gone; that is a deliberate reduction,
not a shortfall.

**The acceptance rows moved with it (r13).** Rows 0-4 stand. Row 5 is restated. Row 6 became vacuous
once there was no certification to defeat and is replaced by the finding-1 test. **Row 7 is
WITHDRAWN** - "a voice turn costs no tunnel screen read" is false by design now that live reads
always tunnel. Do not re-run row 7 and do not let a later summary quietly re-scope it.

**The label that still travels with every phase 0 result:**

> proven against the mapped model, not the migrated schema.

The second old label - "proven from the store inwards, with the push path unexercised" - is retired
by finding 4's fix, which makes the mapping testable in the default gate and makes the rig compare
content against the Director's own buffer. Confirm that landed before dropping the label.

**Owed the moment #2643 lands** (rulings 6, 8, 9):

1. Re-run the **corrected** sweep in `r2`'s amendment and say what it returned - never infer the slot
   is free from #2643 merging.
2. Rebase, delete our migration, regenerate on the new snapshot, and run
   `has-pending-model-changes` to *no changes* on **both** providers.
3. Re-run the full gate and **every** surviving proof row - they were proven against a migration that
   will have been replaced. Row 7 is not among them; it is withdrawn (r13).
4. Delete the throwaway `EnsureCreated` instrument, as `StatsConcurrencyTestDb` did.

## Phase 1 - do not start it until phase 0 is closed

Phase 1 is the rule store and the rule contract: storage, CRUD, validation, **dry-run only, nothing
typed into any session**. The rule shape is in `brief.md`.

**A rule's condition is a terminal screen description and nothing else** - the owner settled this on
2026-09-02 (`rulings/r11`). Waiting time, token spend, conversation text and machine state are
deferred, not rejected, and must NOT be pre-built behind a condition abstraction with one
implementation. Build the narrow thing completely; do not re-ask him about it.

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
