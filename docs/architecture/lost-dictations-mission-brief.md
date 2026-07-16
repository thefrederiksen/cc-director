# Mission: Lost Dictations

**Status: FINISHED** (opened 2026-07-15, closed 2026-07-15 - both fixes landed on origin/main)
**Mission id:** `00d27b9e-7fc4-43b6-a297-f86d1a33dc4e`
**Mission worktree:** was `D:\ReposFred\devthrottle-lost-dictations` (removed at close)
**Mission branch:** was `mission/lost-dictations`, cut from origin/main at `dd34a6a9` (deleted at
close)
**Issues:** #1593 and #1590, both CLOSED by this mission. #1595 remains open and out of scope.

**How it ended:** item 1 (#1593) merged as `5b0fdf31` (pull request 1687) after THREE inspection
rounds - Codex first forced the re-baseline write to be atomic, then found that the added gate
covered only one writer, so a slow re-baseline could resurrect a delivered tombstone; the final
shape serializes every record write for an upload id under one refcounted, canonicalized per-
directory gate. The pre-existing buffer-epoch blindness it also surfaced is issue #1688. Item 2
(#1590) merged as `f4c615ad` (pull request 1690) after two rounds - Codex caught Send-anyway
double-submitting and silently dropping the typed before/after context; the landed shape composes
the full message exactly as the Gateway does and shares the delivery driver's in-flight gate. The
inspector found real, confirmed defects in every first pass of this mission; none were false
alarms.
**Conduct:** this brief describes the WORK only. How a mission is run - roles, authority, landing,
inspection, the report - lives in `.claude/skills/mission/SKILL.md` and is not restated here.

---

## THE WHY

On 2026-07-15 the owner spoke into his phone twice, and both times DevThrottle threw his words away
and told him nothing. The Director log recorded the whole thing (session `59d2e552` at 05:31,
`fe2ec700` at 05:34): the first delivery attempt failed to submit (the composer never echoed), the
failed attempt itself wrote ~8,700 bytes of its own typing-and-clearing noise into the terminal, the
phone retried, and the Gateway's moved-on guard read OUR OWN noise as "other turns happened" and
discarded the recording as stale. The phone then deleted the audio and cleared the status strip -
"it worked and then nothing happened."

Voice is the flagship phone flow. A product whose defining feature silently eats the user's words is
broken in the one place it most claims to work. When this mission is finished: a dictation whose
delivery attempt fails is retried against an honest baseline and DELIVERED; and on the rare genuine
drop, the phone says so, keeps what it heard, and offers the words back - nothing vanishes silently.

## The mechanics, verified against the code at `dd34a6a9`

1. `POST .../dictation/complete` transcribes, then delivers via
   `route.PostPromptAsync` (`src/CcDirector.Gateway/Api/GatewayDictationEndpoint.cs:444`). A failed
   submit returns 502 (`:445-446`) - RETRYABLE from the phone's point of view, and the phone
   correctly holds the clip and retries.
2. The moved-on guard (`:417-430`) drops a RESUMED clip when `session.TotalBufferBytes >
   req.BaselineBufferBytes + 512`. The baseline is stamped by the phone when the clip was RECORDED
   (`packages/client-core/src/dictation/backgroundSend.ts:114`) and never moves. The failed attempt
   in step 1 typed the text twice and cleared it twice - thousands of bytes of growth the guard
   cannot tell apart from real turns. So the retry of a failed delivery is judged against a baseline
   the failure itself invalidated, and is dropped.
3. The drop writes a durable DELIVERED tombstone with `movedOn = true` (`:426`) - by design
   (#1183), a re-complete of the same upload id returns the same moved-on outcome forever.
4. On the phone, `driveRecord` treats any terminal outcome with `submitted = false` as "nothing to
   acknowledge on screen": `deletePending` + `clearDictationStatus`
   (`packages/client-core/src/dictation/backgroundSend.ts:277-291`). Audio deleted, no banner, no
   trace. The user is never told.

## The work, in landing order (one pull request per item)

### 1. The moved-on guard must never count our own failed attempt (#1593)

- **The fix (server-side):** when a delivery attempt fails (`PostPromptAsync` returns not-ok at
  `GatewayDictationEndpoint.cs:445`), RE-BASELINE that upload id before returning the 502: persist,
  on the durable upload record, the freshest session buffer position available after the failure.
  The moved-on guard then judges a retry against the LARGER of the request's baseline and the stored
  re-baseline. The phone keeps sending its original baseline and needs no change for this item.
- **Accepted consequence (ruled):** if the session genuinely moved on during the seconds of our own
  failed attempt, the retry will now inject rather than drop. In that window the user's words win.
  That is the correct side to err on.
- **Implementation care:** the session snapshot held at `:409` predates the attempt, so its
  `TotalBufferBytes` does NOT include the attempt's noise - re-read the freshest pushed value after
  the failure rather than reusing the stale snapshot. If the push stream lags and the fresh read
  still misses some noise, the re-baseline is still strictly better than today; say so in a comment
  rather than pretending the window is zero.
- **The proof that must exist:** an end-to-end test driving the REAL dictation complete endpoint
  over loopback HTTP with a scripted Director whose prompt verb FAILS the first time (growing the
  reported buffer, as the real failure does) and succeeds the second: the retried clip must be
  DELIVERED, not dropped. Watched failing against the un-re-baselined guard. A control pins the
  guard still dropping when the growth happened BEFORE any delivery attempt (a genuine move-on).

### 2. A dropped dictation must be loud and give the words back (#1590)

- **The fix (client-side, `packages/client-core` + `apps/mobile`):** in `driveRecord`, a terminal
  outcome with `submitted = false` must never silently `clearDictationStatus` - except for
  abandonment, which the user did on purpose (ruled out of scope by the issue itself). Split the arm:
  - **Moved-on / dropped:** the outcome already carries the TRANSCRIPT
    (`GatewayDictationEndpoint.cs:429` returns it). Keep the record visible in a parked-style sticky
    state that names what happened in plain words, and offer the transcribed text back with a
    "Send anyway" action that sends it as a NORMAL prompt to the session (a fresh turn - re-driving
    the same upload id is useless by design, see mechanics point 3), plus a Dismiss. If the
    transcript is empty (rare - dropped before transcription), keep the audio parked with an
    explicit user Retry that re-drives under a FRESH upload id.
  - **Empty clip** (silence - `submitted = false, movedOn = false` from `:404`): a visible,
    dismissible notice ("nothing was heard"), no retry (there is nothing to retry), and the
    on-device copy may be dropped.
- **The proof that must exist:** client-core tests driving the real `driveRecord` and the real
  status store through each terminal-not-submitted shape, asserting the status is VISIBLE and
  carries the transcript/action, watched failing against today's silent-clear. The abandoned path
  stays silent - pinned by a control. Mobile rules apply: failures are loud and sticky, never a
  toast that fades.

## Design rulings already made

1. Scope is #1593 + #1593's safety net #1590. #1595 (counting and surfacing delivery failures
   generally) stays open as its own issue - do not build dashboards here. (Stated by the Architect.)
2. The #1593 re-baseline lives on the SERVER, on the durable upload record - not in the phone's
   record - so a phone that missed the 502 response entirely still retries against the honest
   baseline. (Inferred from the issue's "re-baseline the buffer position for that upload id".)
3. Recovery for a moved-on drop is TRANSCRIPT-first ("here is what I heard - send it?"), because the
   transcript already rides the outcome and re-uploading audio against a tombstoned upload id is a
   dead end by design. (Inferred; the issue's stated bar is only "a visible message on the phone" -
   if the transcript-first shape balloons, the visible+parked minimum lands first and the Send-anyway
   action follows in the same pull request series.)
4. Items land in the order above: item 1 kills the observed drop, item 2 is the safety net for
   genuine drops. Two pull requests. (Stated by the Architect.)
5. What cannot be proven in this mission is named in the QA report rather than papered over: no live
   phone-to-Gateway drive is required to merge, but the endpoint-level end-to-end (real HTTP, real
   endpoint, scripted Director) is the floor for item 1, and the issues' "driving a real drop" bar is
   met at that level. (Stated by the Architect.)

## Out of scope - do not do these

- #1595 (surface/count delivery failures generally), #1594 (the submit harness), #1151 (idempotent
  submit), and anything touching `EchoVerifiedSubmit` itself - the echo failure is the TRIGGER of
  this defect, not its subject.
- The desktop dictation path (#1130) and Director-side dictation code.
- Any Gateway API surface change beyond the upload record and the complete endpoint's failure arm.

## Inspection note (mission-specific fact, not conduct)

The Inspector is Codex, invoked by the Architect per pull request with the diff and touched files
bundled inline (`codex exec` cannot run shell commands on this machine). Findings go back to the
builder; the Architect lands.

---

*When this mission ends, this document's status line must be updated to say so, in the past tense.*
