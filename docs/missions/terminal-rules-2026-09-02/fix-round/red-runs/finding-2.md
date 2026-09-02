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

## The green run

```
dotnet test src/CcDirector.Core.UnitTests/CcDirector.Core.UnitTests.csproj \
  --filter "FullyQualifiedName~CaptureMarkDescribesTheCapturedFrame|FullyQualifiedName~TurnEndScreenCapture" --nologo -v q
Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4        exit 0
```

On commit `43694cffa`, and inside the green tip gate on the same commit (`Core.UnitTests`
outcome=Completed, total 164, 0 failed, runner exit 0). See `../runs.md`.

Row 0's own bound assertion - the mark never exceeds the buffer's total - is in those four and still
holds, and is now true for a stronger reason: the parser can only have consumed what the buffer already
accepted.

An earlier green was taken when the fix landed, before the provisional migration was regenerated at
11:48; it is not the one quoted, because a green taken against a migration that no longer exists is not
evidence about the tree that ships.
