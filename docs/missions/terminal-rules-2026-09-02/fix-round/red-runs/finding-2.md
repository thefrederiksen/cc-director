# Finding 2 - the red run, before the fix

`CaptureMarkDescribesTheCapturedFrameTests` drives the interleaving the inspection established: a
subscriber ordered ahead of the session's parser holds a second write open after the buffer's counter
has advanced and before the parser has seen it, then takes the capture.

Command:

```
dotnet test src/CcDirector.Core.UnitTests/CcDirector.Core.UnitTests.csproj \
  --filter "FullyQualifiedName~CaptureMarkDescribesTheCapturedFrame" --nologo -v n
```

Result against the unfixed capture:

```
Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1

CaptureMarkDescribesTheCapturedFrameTests.The_mark_describes_the_frame_that_came_back_not_a_later_terminal [FAIL]
  Assert.Equal() Failure: Values differ
  Expected: 18
  Actual:   36
```

Eighteen bytes is the first write - the only bytes the returned frame reflected. Thirty-six is the
buffer's total including the second write, which the parser had not seen. The capture returned the old
frame with the new total: the OVERSTATEMENT the shipped comment said was impossible.

The two assertions before it had already passed, so the bad state was positively established rather
than assumed: the rendezvous subscriber was reached, and the buffer's total really had moved to 36.

## The green run - PENDING

**The green side of this finding is NOT in hand and is not quoted here.**

A green run WAS taken when the fix landed and it passed - 4 passed on the two capture classes, 164
passed on the whole Core unit project - but at 11:3x, BEFORE the provisional migration was deleted and
regenerated at 11:48. Those numbers are withdrawn as this finding's green rather than left standing.

This class was re-run once after the regeneration and after the test was hardened (the rendezvous flag
moved to Interlocked): 1 passed, at 12:42, on commit `5a93de2aa`. That is a real post-regeneration green
for the finding's own test - but it is a single filtered case on a commit that is no longer the tip, and
it is recorded as such rather than presented as the finding's proof.

Row 0's own bound assertion - the mark never exceeds the buffer's total - still holds, and is now true
for a stronger reason: the parser can only have consumed what the buffer already accepted. That
assertion's run is owed with the rest.

**What is owed:** re-run both capture classes and the Core unit project, and quote them. PENDING on the
Architect's clearance of the test lock.
