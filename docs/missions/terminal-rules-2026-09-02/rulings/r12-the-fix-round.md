# Ruling 12 - the fix round, and the principle that decides finding 1

Architect ruling. Governs the round that answers `inspection-01.md`. The owner has said: fix the
findings.

## Standing rules for this round

**A fix round is new writing.** Six defects were found in code that was reported complete with a
green gate. The fixes are not a formality on top of proven work - they are new code of exactly the
kind that just failed, and they get the same treatment: their own proofs, and a second independent
inspection before anything lands.

**Every fix owes a test that FAILED BEFORE IT.** Write the test first, watch it go red against the
current code, then fix it and watch it go green. A fix whose test passes on the unfixed code has
tested nothing - and finding 4 is precisely that failure, so it must not be repeated in the round
that answers it.

**Do not argue with the inspection.** Each finding was established by a reproduction the inspector
ran and reverted. If one is genuinely wrong, disprove it with an artifact and say so plainly; do not
downgrade it in prose.

**The inspector does not fix anything and is not asked to.** It is a different agent family on
purpose and it stays that way.

## Finding 1 decides the shape of the phase, so it is ruled here

The inspector established that terminal writes do not push a session delta: the pushed byte count is
refreshed by a ten-second timer and some activity transitions, not by the terminal moving. So all
three freshness facts can pass on a screen that has changed - and the shipped negative-control test
asserts that stale screen is served rather than forbidding it.

**The principle: a certification may only rest on a signal that is refreshed by the event it claims
to detect.** The byte count claims to establish "the terminal has not moved since capture". If it is
not refreshed when the terminal moves, it cannot establish that, and no combination of connection
state and snapshot age repairs it - those answer different questions. Ruling 1 required all three
facts and was right to; what it missed is that the first fact was not measuring what its name said.

Two acceptable resolutions. Pick one on evidence, and say which and why:

1. **Make the count current.** The terminal's byte total is pushed when the terminal writes,
   coalesced to a stated interval, and the freshness budget is set strictly shorter than the worst
   case that coalescing allows. The guarantee then has a real bound and can be stated in a sentence.
   Measure the push volume this creates on a busy session before choosing it - a per-write push on a
   noisy terminal is its own defect.
2. **Stop answering live questions from the store.** `ReadLiveAsync` always goes to the tunnel;
   `ReadStored` keeps serving history, which is where the store's value was always least in doubt.

**Resolution 2 is the default, and it is not a failure.** Phase 0's history half - a screen visible
from anywhere, surviving the machine going offline, still there in the morning - is the half that
motivated the mission and it stands untouched. The live half was an optimisation, and an optimisation
that cannot be made sound is dropped, not weakened. Choose resolution 1 only if the measurement says
the push volume is small; do not choose it because it is the more impressive answer.

Whichever is chosen, **the negative-control test is rewritten to forbid the stale serve**, not
adjusted to keep passing. That test currently encodes the bug; a fix that leaves it asserting the old
behaviour has fixed the code and kept the reason nobody noticed.

## The rest, in the order they should be taken

- **Finding 2** (capture pairs an old frame with a new mark) - the mark and the frame must come from
  one consistent observation, or the mark must be taken so that it can only understate, which is what
  the comment already claims and the code does not do. Delete the claim if it cannot be made true.
- **Finding 3** (stored row not bound to the routed Director) - the certification compares the
  stored row's Director with the routed Director, and the key carries what it needs to make that
  comparison meaningful.
- **Finding 4** (the end-to-end proof accepts mangled content) - the rig asserts the rows EQUAL what
  was on the terminal, quoted both sides. This is the finding that invalidated the phase's headline
  claim; it is not a low-priority tidy-up.
- **Finding 5** (a reconnect-window failure loses that turn permanently) - either give screens the
  durability the report claims, or change the report to describe the real loss boundary. Both are
  acceptable; the current mismatch between the two is not.
- **Finding 6** (the 200-row cap is not exact across overlapping processes) - make the bound true or
  state it as approximate. Do not leave a comment asserting a guarantee the code does not provide.

## What must not happen

- No summary calls phase 0 complete until every finding is answered and a second inspection has run.
- The parked gate's verdict is the runner's own, presence-based, and reported honestly - the previous
  round reported exit 0 where the inspector observed exit 1 on an unrelated case, and that
  discrepancy is itself worth resolving rather than averaging.
- Ruling 6's obligations at #2643's landing are unchanged and still owed on top of all of this.
