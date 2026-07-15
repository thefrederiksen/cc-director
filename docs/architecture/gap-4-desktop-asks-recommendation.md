# Gap 4 - the desktop cannot see four things the phone can

**Status:** DECIDED - **OPTION A IS REJECTED BY THE OWNER, 15 July 2026. It will not be built.** This
paper stays as the record of why, and because its analysis of the mechanism is still correct and still
useful. Nothing here was ever built.

> ## THE OWNER'S ANSWER, and it settles it
>
> The question this paper called the whole decision was: *when the Gateway is unreachable, what should
> the desktop session rail show?* His answer, in his words: **"it is good that it still works without the
> gateway"** - and the case is a laptop that cannot reach the Gateway machine, which must still run his
> sessions.
>
> That kills Option A outright, exactly as this paper said it would. A rail that renders only what the
> Gateway folded has NOTHING to show when the Gateway is unreachable. There is no clever version of that.
> **So the desktop keeps its local fold, and gap 4 does not fully close** - the three permanent blind
> spots (phone dictation, server transcription, voice being prepared) stay, and the desktop keeps saying
> "Needs you" about a session that is busy doing something he asked for.
>
> **That is the price of a laptop that works, and he has paid it deliberately rather than drifted into
> it.** Which is what this paper was for.
>
> **Option B is NOT thereby approved.** This paper argues it treats the symptom and taxes us forever, and
> nobody has decided to pay that. It is available, knowingly, if the nagging ever justifies it. The
> default is Option C: the gap stays named.
>
> **What his answer started instead:** he asked what ELSE has quietly become Gateway-dependent, because
> the laptop must run. That audit is the real work, and it is a bigger question than this paper's.
> See the Gateway-dependency audit that follows from it.
**Written:** 15 July 2026, by the Session States Manager.
**Mission:** [`mission-session-states.md`](mission-session-states.md). **Brief:** [`session-states-gaps-manager-brief.md`](session-states-gaps-manager-brief.md).

---

## What the owner sees

Four things the phone gets right and the desktop does not:

| The session is... | The phone says | The desktop says | Heals on its own? |
|---|---|---|---|
| receiving a dictation from the phone | orange, "Uploading from phone" | **red, "Needs you"** | **No** |
| having its audio transcribed on the server | orange, "Transcribing" | **red, "Needs you"** | **No** |
| having its voice prepared | yellow, "Preparing voice" | **red, "Needs you"** | **No** |
| just out of a snooze that expired | awake | still grey, "Snoozed" | Yes, on the next event |

The first three do not heal. The desktop nags the owner to deal with a session that is busy doing
something he asked for, and it keeps nagging.

## The mechanism - verified, not inferred

The desktop rail folds `ControlEndpoints.Map(session)`, which is **the DIRECTOR's view of a session**.
The four facts are **Gateway-owned** and `Map` does not set them - checked field by field in
`src/CcDirector.ControlApi/ControlEndpoints.cs`:

| Fact | Set by `Map`? | Consequence for the desktop fold |
|---|---|---|
| `DictationStatus` (phone dictation phase) | **No** | reads null -> no orange |
| `Transcribing` (server transcription) | **No** | reads false -> no orange |
| `VoiceGenerating` (voice being prepared) | **No** | `IsVoicePreparing` false -> no yellow |
| `VoiceAudioReady` | **No** | no play affordance |

`IsTranscribing` (the DESKTOP's own dictation) and `VoiceMode` **are** set - which is exactly why this
is confusing to read: the desktop sees its own dictation and misses the phone's.

The snooze case is different in kind and worth separating: the Gateway owns timed snooze, so at the
moment a snooze expires the Gateway knows and the Director has not been told yet. It corrects itself on
the next event. **It is a propagation delay, not a blind spot.** The other three are permanent.

**The Director cannot fix this by trying harder.** These are facts about things happening at the
Gateway - a phone uploading audio, a server transcribing it, a warm-brain producing speech. A Director
cannot observe any of them. The answer must arrive.

## The thing worth saying plainly

The four missing facts are the symptom. **The disease is that the desktop rail is a client that decides
its own colours**, and the law says clients render and decide nothing. It currently agrees with the
phone most of the time because it calls the same `SessionOrdering` function - but agreement by shared
code is not the same as one owner, and these four are precisely where shared code is not enough, because
the desktop is missing the inputs.

So gap 4 is not "four facts are missing". It is "the desktop folds at all".

## The options, with costs

### Option A - the desktop ASKS (the design's own recommendation)

The desktop stops folding and renders the Gateway's already-folded row (`EffectiveColor`, `StateLabel`,
`TriageBucket`), exactly as the phone and the Cockpit do.

- **Right by the law.** One owner, one answer, structurally - not by two copies of one function agreeing.
- **The four facts stop being special.** Every future Gateway fact is free.
- **Cost: UNMEASURED. I do not know what this costs, and nobody should read a number into it.**
  `SessionViewModel` is the rail's whole fold surface and derives everything from an in-process
  `Session`; under Option A it becomes a projection of a pushed row, and `MainWindow`, the FIFO queue
  window (which gap 2 just routed through the local fold) and the rail tests move with it. That is a
  description of the SHAPE of the work, not its size.

  **An earlier draft of this paper said "large, a mission not a slice" and cited "15 change
  subscriptions" as the evidence. Both are withdrawn, and the second was worse than useless:**
  - It counted grep lines, not work: those 15 subscriptions point at **12** distinct handlers (four
    share one).
  - **Five of the 15 are not fold inputs at all** - verification (twice), view mode, number, pending
    deletion. They are badges and ordering. Option A does not touch them. The real fold coupling is
    about ten lines collapsing to roughly seven handlers.
  - **Under Option A most of those fold subscriptions GO AWAY**, because the row arrives pre-folded. The
    number was therefore closer to a measure of what Option A **deletes** than of what it costs - it
    argued the opposite of the case it was cited for.

  If a number is wanted, it has to be earned: spike the transport, port one surface, and measure. Until
  then the honest answer to "how big is Option A?" is **"we have not looked."**
- **It needs a transport that does not exist yet.** There IS a Gateway-to-Director down-channel - the
  `set-resolved-role` verb, which is how the role stamp already arrives - so the direction is proven.
  But it carries one field for one session on change; pushing a folded row for every session on every
  terminal event is a different traffic shape, and I have **not** measured it.
- **It has one genuine product question, and it is the owner's:** *what does the desktop rail show when
  the Gateway is unreachable?* Today the desktop rail works with no Gateway at all. Under Option A it
  has no colours to show. "No answer yet" is honest and is what we chose for the role badge in gap 1 -
  but a badge going quiet is not a rail going blank.

### Option B - push the four facts down to the Director (the tempting wrong answer)

Extend the down-channel so the Gateway stamps `DictationStatus`, `Transcribing`, `VoiceGenerating`,
`VoiceAudioReady` onto the Director, like `GatewayResolvedRole` already is. The desktop keeps folding.

- **It reuses a proven pattern and it would work.** I am NOT calling it "small" - I have measured this
  no more than I measured Option A, and "A is unknown, B is small" would tilt the comparison on exactly
  the kind of unearned adjective this paper had to withdraw once already. What is genuinely known is its
  SHAPE: four more stamps on a channel that already carries one.
- **It treats the symptom, and the tax never stops.** Every Gateway-owned fact, forever, needs its own
  stamp, its own event, its own signal into `RaiseFoldProjection`, its own test - and the desktop's fold
  must stay byte-identical to the Gateway's for all time. The mission has already paid this bill once:
  `GatewayResolvedRole` is exactly this pattern, it took a stamp AND an event AND a signal, **and it
  still shipped broken** - the value arrived, the fold was right, and nothing told the rail to re-read.
  Review found that on pull request 1598. Four more facts is four more chances at that same defect.
- **It entrenches the disease.** It makes the desktop a better guesser instead of stopping it guessing.

**This is the option the brief names as the wrong answer, and having now read the code I agree with the
brief.** It is recorded here because "we considered it and rejected it, for this reason" is worth more
than silence - and because it is what the next person will reach for.

### Option C - do nothing, and say so

Leave it named. The three permanent ones keep nagging.

## What I recommend

**Option A, but it is not startable until the owner answers one question,** and that question is the
whole decision:

> When the Gateway is unreachable, what should the desktop session rail show?

If the answer is "the rail may go quiet/unknown - I use it with the Gateway up", Option A is
straightforward in SHAPE, and the next step is not to build it but to measure it: spike the transport,
port one surface, and come back with a real number. I do not have one.

If the answer is "the desktop must always show me my sessions, Gateway or not", then Option A as stated
is not acceptable, and the honest position is that **the desktop keeps a local fold and gap 4 does not
fully close** - because a local fold that is missing Gateway facts is what gap 4 *is*. In that world the
best available answer may be a bounded, explicit version of Option B, and it should be taken knowingly,
with the tax written down, rather than drifted into.

### The third choice: "go and measure it first"

**This paper cannot tell you what Option A costs, so a perfectly good answer is "find out, then ask me
again."** That is a real option and it is cheap to state, so here is what it would take:

> **Spike the transport and port one surface.** Push the Gateway's already-folded row (`EffectiveColor`,
> `StateLabel`, `TriageBucket`) down the existing `DirectorCommand` channel - the one the
> `set-resolved-role` verb already uses - for one Director, and make the FIFO queue window (the smallest
> rail surface, one predicate since gap 2) render the pushed row instead of folding. That answers the two
> things nobody knows: **how much traffic** a folded row generates at real event rates, and **what a
> ported surface actually looks like**. Then the rail is the same job at a known multiple.

The FIFO window is the right guinea pig precisely because gap 2 just reduced it to a single call to
`Classify` - it is the one surface where "does the pushed row work?" is a small question rather than a
tangled one.

**Do not start Option A on my recommendation alone.** The offline question is genuinely the owner's, and
the cost is genuinely unknown - not "large", not "small", unknown. Guessing either wastes the run.

## This paper's own defect, on the record

**The first draft of this paper had the mission's exact defect in it, and it was about to reach the
owner.** It is recorded here rather than quietly corrected, because a paper that hides its own
correction is asking for the same trust as one that never needed it.

The draft said Option A was **"large, a mission not a slice"** and offered **"15 change subscriptions"**
as the evidence. The number was real - `grep` says 15 - and every inference drawn from it was wrong:

- It counted **lines, not work**: the 15 point at 12 distinct handlers.
- **Five are not fold inputs at all** (verification twice, view mode, number, pending deletion). Option A
  never touches them.
- **It argued the opposite of the case it was cited for.** Under Option A the fold subscriptions largely
  GO AWAY, because the row arrives pre-folded. The number is closer to a measure of what Option A
  *deletes* than of what it costs.

**And the paper disagreed with itself.** The body leaned on that count, while "What I did NOT verify"
below said the size judgement was "not from a count". Both were written by the same author in one
sitting, and neither noticed the other - which is precisely how the row that reads "Needs you" beside a
dot folded to "supporting" gets shipped by someone who checked both halves separately.

**The shape is the mission's own.** Defect 19 was a fabricated CAUSE that survived review because it was
written in the same voice as the true sentences around it. "15 change subscriptions" was a fabricated
MEASUREMENT that would have survived for the same reason - it reads like a fact, it sits among facts, and
catching it requires knowing what Option A does, which the reader making the decision does not. A number
nobody can stand behind is worse than a blank space: a blank space at least tells you to ask.

**If a number reappears in this paper, ask what was measured to get it.**

## What I did NOT verify

Said plainly, so nobody spends their scepticism in the wrong place:

- **I did not measure the traffic** a pushed folded row would generate, nor whether the existing
  `DirectorCommand` down-channel can carry it. "The direction is proven" is not "the volume is proven".
- **I did not confirm the snooze-expiry path end to end.** I am reading it as a propagation delay from
  the Gateway owning timed snooze; I did not watch it happen.
- **I did not size Option A. At all.** Not in files, not in hours, not in anything. An earlier draft
  called it "large, a mission not a slice" on the strength of a subscription count that, read properly,
  points the other way (see Option A above). That draft also said in this very section that the size
  judgement was "not from a count" while the body leaned on exactly that count - the paper contradicted
  itself, and I did not catch it because I wrote both halves. **If this paper still moves you toward
  "Option A is expensive", that is rhetoric, not evidence.**
- I did not investigate whether the Cockpit's rail has the same offline question. It probably does, and
  it may already have an answer worth copying.
