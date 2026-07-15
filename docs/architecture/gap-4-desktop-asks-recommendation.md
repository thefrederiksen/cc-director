# Gap 4 - the desktop cannot see four things the phone can

**Status:** INVESTIGATION AND RECOMMENDATION ONLY. Nothing here is built. This is a decision paper for
the Architect and, on one question, for the owner.
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
- **Cost: large, and it is a mission, not a slice.** `SessionViewModel` is the rail's whole fold surface
  and today it derives everything from an in-process `Session` with **15 change subscriptions**. All of
  it becomes a projection of a pushed row. `MainWindow`, the FIFO queue window (which gap 2 just routed
  through the local fold), and every rail test move with it.
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

- **Small, and it reuses a proven pattern.** It would work.
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
straightforward in shape (if large in work) and I would start with the transport.

If the answer is "the desktop must always show me my sessions, Gateway or not", then Option A as stated
is not acceptable, and the honest position is that **the desktop keeps a local fold and gap 4 does not
fully close** - because a local fold that is missing Gateway facts is what gap 4 *is*. In that world the
best available answer may be a bounded, explicit version of Option B, and it should be taken knowingly,
with the tax written down, rather than drifted into.

**Do not start this on my recommendation alone.** The cost is real, the offline question is the owner's,
and guessing it wrong wastes the run.

## What I did NOT verify

Said plainly, so nobody spends their scepticism in the wrong place:

- **I did not measure the traffic** a pushed folded row would generate, nor whether the existing
  `DirectorCommand` down-channel can carry it. "The direction is proven" is not "the volume is proven".
- **I did not confirm the snooze-expiry path end to end.** I am reading it as a propagation delay from
  the Gateway owning timed snooze; I did not watch it happen.
- **I did not size Option A in files or hours.** "Large, a mission not a slice" is a judgement from the
  15 subscriptions and the surfaces involved, not from a count.
- I did not investigate whether the Cockpit's rail has the same offline question. It probably does, and
  it may already have an answer worth copying.
