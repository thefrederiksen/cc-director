# Ruling 4 - the correction stands; EnsureCreated proves the logic, never the schema

Architect ruling. Amends ruling 2.

## The correction is accepted, and it was found the right way

Ruling 2 said the phase 0 code was independent of the migration and told the Manager to proceed
without the slot. The Manager checked that premise instead of working around it, and it is wrong in
one half:

Verified here independently. `GatewayDatabase.cs` calls `ctx.Database.Migrate()` at lines 346 and
414, and `grep -rn EnsureCreated src/CcDirector.Gateway/` returns **nothing** - there is no
create-from-model path anywhere in Gateway production code. So `session_screens` does not exist in
any real Gateway until the migration lands.

The **code** is independent of the slot. The **proofs that need a real Gateway** are not: the offline
read-back, the question-B refusal, and the voice-turn counter all need the table to exist. Ruling 2
did not distinguish those and should have. This ruling amends it.

That is the second time this mission a stated premise has been checked rather than assumed, and both
times it found something. Keep doing it, including to rulings from this seat.

## The instrument is right, and its limit must be stated in the same breath

Building the four provable rows on a throwaway `EnsureCreated` database from the same mapped model is
the correct move, and it has a house precedent: `StatsConcurrencyTestDb.cs` exists for exactly this
situation - another worker owned the migration chain - and its own comment says it is to be discarded
when the rest arrives.

**But be precise about what it proves.** `EnsureCreated` builds tables from the **mapped model**. The
real Gateway builds them from the **migration file**. Those are two different artifacts that are
*supposed* to agree and are not guaranteed to. So:

- The store, reader, retention and tenant rows proven this way prove the **LOGIC**: that the code
  reads, writes, sweeps and scopes correctly against the shape it believes in.
- They prove **nothing about the schema** the real Gateway will have.

Label them that way in the report - "proven against the mapped model, not against the migrated
schema". A reader six weeks from now must not be able to mistake one for the other.

## What is owed when the slot frees

Three things, none optional:

1. **Re-run the sweep from ruling 2** and say what it returned. Do not infer the slot is free from
   #2643 merging, and resolve #2379's three August migrations at the same time.
2. **Assert the migration and the model agree** - EF will answer this directly (a pending-model-changes
   check). If they disagree, the four rows above were proven against a shape that does not exist, and
   that is a finding, not a formality.
3. **Re-run the three live rows against a MIGRATED database**, not the `EnsureCreated` one. Then
   delete the throwaway instrument, as the stats precedent did.

## The reporting posture is correct - hold it

> phase 0 will reach a state where everything is written, four acceptance rows are proven, and three
> are waiting on the slot - I will report it that way rather than call it done.

That is exactly right, and it is the posture this seat wants on every phase. Two things follow from
it and both bind:

- **Phase 0 is not done, and no later summary may say it is** until all seven rows are proven. Not
  "done pending the migration", not "done bar the slot". A phase with three unproven rows is a phase
  in progress, and the count is the honest headline.
- **Do not start phase 1 on the strength of the four.** Phase 1 adds rules on top of a store whose
  real schema is still unproven; if row 2 above finds a disagreement, phase 1 would be built on it.

If the slot has not freed by the time everything else is written and the four rows are proven, stop
there, push, report the split, and say so plainly. Waiting visibly is a better outcome than a phase
that reads as finished and is not.
