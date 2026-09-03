# Ruling 1 - when the store may answer, and when it must not

Architect ruling on the phase 0 plan's central decision. Binding for phase 0 and after.

## The plan's proposal

> A stored screen is served only when the session's pushed `TotalBufferBytes` still equals the mark
> taken at capture, so a changed screen always falls to the tunnel.

The instinct is right and the direction is right. **Strict equality, not a threshold, is correct** -
and note that it deliberately differs from the nearest precedent in the codebase. The dictation
moved-on guard (`GatewayDictationEndpoint`, issue #1006) compares
`TotalBufferBytes > baseline + MovedOnBufferGrowthBytes` because it is deciding whether to DROP a
user's words and a false drop costs the user their sentence. Here the conservative direction is the
opposite: any byte at all means the screen may have moved, so it falls to the tunnel. Do not copy
the threshold across. Say so in the code comment, because the next reader will find the precedent
and wonder why this one is stricter.

## Why the check as stated is not sufficient

`SessionDto.TotalBufferBytes` on the Gateway is **the last value the owning Director pushed**, not a
live reading. The repository already says this about itself, in
`GatewayDictationEndpoint.cs`, on the re-baseline path:

> NOT PROVEN TO CLOSE THE WINDOW COMPLETELY, and deliberately not dressed up as if it does: this
> reads the pushed store, so it sees only what the owning Director has pushed BY NOW. If the push
> stream lags the failure, some of the attempt's noise is not in this number yet [...] the residual
> lag window is real and is not closed here.

Follow that through for this feature. When the Director stops pushing - machine asleep, tunnel down,
or simply lag - the pushed byte count **freezes**. The mark taken at capture and the current value
are then equal *because nothing is arriving*, not because nothing changed. The check passes, and the
store serves a screen it cannot vouch for.

That is a check whose pass condition is an ABSENCE - "no newer byte count has arrived" - and it
fails open by construction. The fleet has a skill named for exactly this shape
(`cc-devthrottle skill get checks-that-fail-open`); read it before implementing this slice.

It matters most in precisely the case phase 0 is built for. "The machine is offline" is one of the
acceptance rows. Under this check, offline is indistinguishable from quiet.

## The ruling

**Split the callers. Two different questions are being answered by one store.**

**Question A - "what was on screen at the end of that turn?"**
History. The store is the right and only answer. Staleness is irrelevant; the machine being offline
is the point. Serve it freely, with no freshness test at all.
*Consumers:* the Cockpit screen view, the rules engine's evaluation record, "make a rule from this
screen", anything reviewing the past.

**Question B - "what is on screen RIGHT NOW?"**
Live truth, and a keystroke may be pressed on the answer. The store may answer **only when it can
prove freshness**, which requires all three, not any one:

1. the byte mark taken at capture equals the currently pushed `TotalBufferBytes`, AND
2. the owning Director's stream is **currently connected** - a positive liveness fact, read the way
   the roster already reads it, not inferred from the absence of a newer push, AND
3. the pushed snapshot is younger than a stated freshness budget, named as a constant with its
   reasoning beside it.

If any of the three cannot be established, go to the tunnel. If the tunnel cannot answer either, the
honest result is **unreadable** - and unreadable must be returned as unreadable, never as a stored
screen. `SessionSupervisor` already handles that correctly today and its comment says why: *"an
unreadable screen is never a fault, because acting on one would be guessing."* Preserve that
behaviour exactly; a session that goes quiet must not start getting keystrokes pressed at it on the
strength of a screen from before it went quiet.

*Consumers:* the menu guard before a guarded Enter, the supervisor before it acts, any future rule
action.

**The mark and the capture are taken together or not at all.** Read `TotalBytesWritten` in the same
operation that snapshots the screen. A mark taken a moment after the capture describes a different
terminal and silently widens the window this whole ruling exists to close.

## A correction to the brief, which was mine

The brief claims phase 0 takes a tunnel round trip out of *every* voice turn. That is too strong and
I wrote it, so I am withdrawing it rather than leaving the Manager to hit it.

`TurnReviewLogger` captures on one trigger only: the Working -> WaitingForInput flip. So the store
holds turn-end screens and nothing else. Callers that ask mid-turn - the menu guard runs before a
guarded Enter, which is frequently mid-turn - will usually find nothing stored and fall to the
tunnel. That is correct behaviour, not a defect.

The real phase 0 win is the turn-end readers, and it is still a good one: `WaitingScreenReader` runs
at turn end, twice, and the supervisor runs at turn end. Those are the round trips that go.

**Do not widen the capture trigger to chase the rest.** Capturing every screen change is terminal
streaming, which #2644 puts out of scope, and it would multiply the store's volume for a caller
whose fallback already works.

## What the acceptance must now show

Add to the phase 0 acceptance, both as PRESENCE checks:

- **The offline case is served, and is labelled.** A stored screen read back with the machine offline
  is returned for question A, and the same request for question B is refused and falls through -
  proven by driving it, not by reading the code.
- **A frozen push stream does not certify a stale screen.** Stop the Director's push, change the
  session's screen, and show that a question-B caller does not receive the pre-freeze screen. This is
  the negative control for the whole slice. Without it the slice is not done.
