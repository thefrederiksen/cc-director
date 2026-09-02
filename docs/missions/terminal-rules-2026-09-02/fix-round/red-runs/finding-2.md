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

## The green run, after the fix

The mark is now taken from the parser's own consumed-byte count, read inside the same lock that
produces the rows.

```
dotnet test src/CcDirector.Core.UnitTests/CcDirector.Core.UnitTests.csproj \
  --filter "FullyQualifiedName~CaptureMarkDescribesTheCapturedFrame|FullyQualifiedName~TurnEndScreenCapture" --nologo -v q
Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4

dotnet test src/CcDirector.Core.UnitTests/CcDirector.Core.UnitTests.csproj --nologo -v q
Passed!  - Failed: 0, Passed: 164, Skipped: 0, Total: 164
```

Row 0's own bound assertion - the mark never exceeds the buffer's total - still holds, and is now true
for a stronger reason: the parser can only have consumed what the buffer already accepted.
