# Turn push - what changed, and what you can trust

## The thing you asked about

Session 111 kept failing to produce a spoken narration. You asked whether it was the text, the turn
data not reaching the Gateway, or a full disk. It was none of those.

The Gateway did not hold your conversations at all. Every time it needed one - to draw the chat
screen, to narrate a turn, to wait for a spoken answer - it sent a request down to your computer
asking the Director to open the agent's transcript FILE and read it back. And it worked out where
that file was by BUILDING a path from the session's repository folder.

That agent had moved into a worktree. The transcript moved with it. From that moment the Gateway
was asking for a file at an address where nothing lived, getting back "nothing here", and treating
that as "this session has said nothing worth narrating" - for hours, and it would never have
recovered on its own, because the formula would have produced the same wrong address forever.

Chat kept working in that same session, which is why it looked so strange. Chat already followed the
pointer the agent's own hook reports, instead of computing one.

## What it does for you now

**The Gateway keeps your conversations itself.** Your computer pushes each finished turn up as it
happens; the Gateway stores it and everything reads from that store - chat, the transcript view, and
the wingman's narration. There is no file read anywhere in that path any more. A read that cannot be
made cannot fail.

Two practical consequences you will notice:

- **Chat is faster and no longer depends on a round trip to your machine.** It used to ask your
  computer for the whole conversation every 2.5 seconds.
- **If your computer is temporarily unreachable, the conversation is still there to read.** To be
  precise, because this is easy to overstate: the session itself still needs your computer running -
  it is where the agent lives. What changed is that READING what has already been said no longer
  waits on reaching that machine.

**And the voice screen now tells the truth and gives you a way out.** This is the thing you asked
for on the phone: when a narration does not appear, the Gateway tries again a few times, minutes
apart, and then genuinely stops and offers you a Generate button. Before, it retried every 45
seconds forever, the screen said "still trying" forever, and the button was deliberately withheld.

The exact shape: five attempts, three minutes apart. The count is against the TURN, not the session,
so a new answer never inherits an old one's exhausted budget.

## What is proven, and how

- **The whole failure class is gone, checked by machine.** A test reads the compiled code and fails
  if anything on your computer's command surface builds a transcript path by formula again, or if
  the Gateway resolves one at all. I proved the test works by putting the old code back: it went red
  and named the exact method. It runs on every change, not only in the slow suite.
- **The retry schedule's boundaries** are tested with the clock injected, so each moment is checked
  rather than waited for.
- **The full slow suite** (2324 tests) was run against the finished code.

## What is NOT proven - please read this part

- **Nobody has watched this work on a real phone with a real session.** Every proof is unit-level
  plus the test suites. The first genuine end-to-end run will be yours.
- **Nobody has seen the schedule elapse in real time** - five attempts, three minutes apart, button
  appears. That has only been tested with a fake clock.
- **The Generate button has not been pressed after the Gateway gave up.** A test proves its route
  does not consult the retry schedule, so it cannot be blocked by it. That is not the same as proving
  the press produces audio.

## What I got wrong along the way

Three things worth your knowing, because two of them cost real time:

**I started by fixing the symptom.** My first change made the path formula smarter at finding a
moved transcript. You stopped me, and you were right - that was a band-aid on a design that should
not have existed. The fix was to stop reading transcripts at all.

**I killed a healthy test run and reported it as hung.** I said it was wedged, citing flat processor
time and no file writes. Both signals were worthless - flat processor time at that resolution is
what a waiting process looks like, and nothing writes to that folder after the build. The run was
normal and I stopped it eight minutes before it would have passed. I corrected that with the other
session before they acted on it.

**My fixes introduced new defects, twice.** An independent reviewer found eight problems in this
work; the worst would have shipped a screen saying "the Gateway has stopped trying" while it went on
retrying every ten minutes forever. I fixed those - and a second review of THE FIXES found nine
more, two of which I had just introduced, including one that was a smaller copy of the very bug I
was fixing. That is now written down as a rule rather than a story: a fix round is new writing and
gets reviewed as hard as the original.

## One thing that is yours to decide

The Director still carries the old `turns` command that nothing on the Gateway calls any more. I
left it deliberately, because the Gateway running in production is still the previous build and
still asks for it. It can be deleted once the new Gateway is deployed - a small, safe cleanup, but
it needs that deploy to happen first.
