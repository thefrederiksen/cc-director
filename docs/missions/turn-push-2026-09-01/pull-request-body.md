Closes the turn-push mission (#2638). Two phases and two rounds of adversarial fixes, in one pull
request because splitting them would put known defects on main - see **Why this is one pull request**
at the bottom.

## What this does for a person

**Phase 3c - the Gateway tries again, then hands you the button.** When a narration does not appear,
the Gateway now retries a few times, minutes apart, and then genuinely stops and offers a Generate
button. Before this it retried every 45 seconds forever, the screen said "still trying" forever, and
the button was withheld on the argument that pressing it would only re-run what had already failed.
That argument is right for the first minute and wrong at nineteen - which is what the owner was
looking at on a phone when he asked for this.

**Phase 4 - nothing reads a transcript any more.** The Gateway's transcript read is deleted, and the
Director resolves a transcript in exactly one place. This is the defect that started the mission:
session 111 produced no spoken narration for hours because the path was BUILT from the session's
repository folder, the agent moved into a worktree, the transcript moved with it, and every read
went on opening the empty spot for the rest of the session's life. Chat in that same session worked
fine, because Chat already followed the pointer the agent's own hook reports.

## What is removed

- `SessionVerbClient.GetTurnsAsync` and the tunnel dispatcher's `turns` entry. Until phases 2 and 3
  landed, the Gateway asked the owning Director to open and parse the user's transcript on every
  2.5-second chat poll, on every narration, and once per 750 milliseconds while waiting for a spoken
  answer. There is no caller left, and a read that cannot be made cannot fail - no missing file, no
  parse error, and no "succeeded with an empty widget list" for the voice path to mistake for a
  session that had nothing to say.
- Eight path-by-formula call sites across `ChatService`, `ControlEndpoints`, `SessionReadExecutor` and
  `SessionWriteExecutor`, replaced by `SessionHistoryReader.ResolveTranscriptPath`.

**Deliberately NOT removed:** the Director's own `turns` verb. Production still runs the previous
Gateway build, which asks for it. It goes when that Gateway is deployed, not before.

## The retry schedule

Five automatic attempts, three minutes apart, counted against the TURN rather than the session - the
turn being named by a digest of the reply plus the conversation's length. Keyed on identity and not
reset by an observed event, because that event is sampled and a quick turn slips through it; this
repository has been bitten by exactly that shape before (the bare has-audio guard of #1322). A spent
schedule is re-read after ten minutes so a turn that changed unobserved is never stranded - but that
pass is a look, not another try, and the policy enforces that rather than leaving it to the caller.

## Two review rounds, and the second one mattered more

An independent Codex pass found **eight** findings; six were real. The worst would have shipped a
screen that lies: a spent schedule kept retrying every ten minutes for the life of the session while
the Voice screen said the Gateway had stopped trying and offered a button.

A **second** pass, pointed at the FIXES rather than at the original work, found **nine** more - and
**two had been introduced by the first round**:

- The fix for "a coalesced turn is dropped" put its marker in a different map from the guard that
  coalesces, opening a window in which a losing caller could leave a marker nobody owned. A smaller
  copy of the bug being fixed.
- The two-pass bound meant to stop the loop spinning stranded the marker it existed to protect.

Both are closed here: the holder re-reads the marker after releasing the guard, and the idle sweep's
has-audio skip - otherwise permanent for a session holding a clip for an older turn - now defers to a
waiting narration.

Also fixed across the two rounds: a cancellation was counted as a failed attempt; bookkeeping could
throw out of a `finally` and abandon a turn; attempts were recorded after the concurrency guard was
released, so two could run back to back with none of the promised minutes between them; and an
observed Working transition cleared a turn-keyed count, which let a work cycle that produced no new
reply re-arm a spent turn.

## Guards that check the claim rather than assert it

- `OneTranscriptResolverArchitectureTests` reads the compiled intermediate language: the Director's
  command surface must not derive a path, the four types that read a transcript must still call the
  one resolver **by name**, and the Gateway must resolve a path by neither route. A positive control
  keeps it honest - a renamed member turns it red instead of letting the absence checks pass by
  looking for nothing.
- `ManualNarrationIsNotGatedArchitectureTests` is an allowlist of the callers permitted to ask whether
  an automatic narration is due. The Generate button exists to be pressed at the moment the schedule
  gives up, so gating it on the schedule would make it do nothing for as long as the screen advertises
  it. A reviewer concluded that had happened - reasonably, because nothing said otherwise except the
  absence of a call in a nine-hundred-line file.

Both guards were watched failing. Reverting one call site reddened the resolver guard naming
`ControlEndpoints::ComputeTurnCount`; removing the rerun marker reddened both concurrency tests.

## Tests

Default local gate green: 3269 Gateway unit tests, and back under the 120-second ceiling at 1m15s.
The two new concurrency tests originally held thread-pool threads to arrange their race and cost the
suite fifty seconds - 1m26s before, 2m19s after, 1m15s once they parked on an await instead. The
ceiling is paid by everyone on every change, so the tests gave way rather than the ceiling.

Parked `Gateway.Tests` run against this exact tree: see the comment below.

## What is NOT proven

- No live run against a real Director and a real phone. Every proof here is unit-level plus the
  parked suite.
- The schedule's timing has never been watched elapsing in a real session - only with the clock
  injected. Nobody has seen five attempts happen three minutes apart and the button appear.
- The Generate button has not been PRESSED after a schedule was exhausted. The guard proves its route
  does not consult the schedule; it does not prove the press produces audio.
- The rerun marker's two race windows are proven by arranged races, not observed in production.

## Why this is one pull request

The mission's own rule prefers small slices, and its other rule says a fix and the thing that stops
it regressing are one unit. Those pull in opposite directions here: the two fix rounds repair defects
in phase 3c using guards that live in phase 4's files, and splitting them would mean knowingly
merging the "retries forever while claiming it stopped" defect to main and fixing it afterwards. The
commit history keeps the four steps separate and readable.
