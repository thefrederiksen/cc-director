# Finding 6 - the red run, before the fix

`SessionScreenStoreTests.SweepAsync_RepairsASessionLeftOverTheCap_AndKeepsTheNewest` seeds 203 rows for
one session straight through the context - past `Append` and therefore past the write-time trim, which
is the state a lost race between two Gateway processes leaves behind - and then asks the retention
sweep to put it right.

Command:

```
dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj \
  --filter "FullyQualifiedName~SweepAsync_RepairsASessionLeftOverTheCap" --nologo -v n
```

Result against the unfixed sweep:

```
Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1

SessionScreenStoreTests.SweepAsync_RepairsASessionLeftOverTheCap_AndKeepsTheNewest [FAIL]
  Assert.Equal() Failure: Values differ
  Expected: 3
  Actual:   0
```

The assertion immediately before it had already passed, so the bad state was positively established
rather than assumed: the session really did hold 203 rows when the sweep ran. The sweep removed none of
them, because it only cut on age.

## What was fixed, and what is now claimed

Two things, and the second is as much of the answer as the first.

**The bound is made true by repair.** `SessionScreenStore.TrimSessionsOverCap` trims every session that
is over the cap, and `SessionScreenSweep` runs it on every pass alongside the seven-day cut. An ACTIVE
session already repaired itself on its next append; an IDLE one had no next append and sat above the
advertised bound until retention removed the rows days later. That was the inspector's residual point
and it is now closed.

**The guarantee stops being overstated.** The store's comment said the cap "holds even under a burst",
which is not true across two Gateway processes - and the store's own duplicate-retry comment names two
overlapping processes during a deploy swap as a real case. What the code provides is now written out
exactly:

> After any write that is not racing another Gateway process, the session holds at most the cap. While
> two processes overlap, each can insert a row, count only its own view, and select the same oldest row
> to delete, so the session can transiently hold up to the cap plus the number of overlapping writers.
> The lock is per store INSTANCE and cannot see across processes; there is no cross-process lock here
> and this comment does not pretend there is one.

## The green run, after the fix

```
dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj \
  --filter "FullyQualifiedName~Screens" --nologo -v q
Passed!  - Failed: 0, Passed: 30, Skipped: 1, Total: 31
```

The test also asserts that the sweep is not a method that always removes something: a second pass over a
session already at the cap answers 0 and leaves the rows alone.
