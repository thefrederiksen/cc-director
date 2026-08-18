# Dictation Dialog: The One Canonical User Experience

Status: SPEC. Audience: anyone implementing or changing the "Dictate" / "Speak"
voice-to-text dialog on ANY surface (desktop Avalonia, Blazor Cockpit, plain
HTML page, phone). Every surface MUST implement exactly this contract so the
feature looks and behaves the same everywhere. Where a surface deviates, the
deviation must be listed in section 8 with a reason.

This document is the single source of truth for the dialog's behaviour. The
text-cleanup safety contract lives separately and is unchanged by this spec. It is no longer
"the model proposes edits": deterministic code isolates candidate spans and a judge rules on them
by id (devthrottle_internal#1554). `EDIT_DOCUMENT_CLEANUP.md` describes the SUPERSEDED protocol and
is kept as history.

---

## 1. The core idea: batch, with a pause checkpoint

Dictation is **batch only**. While you are speaking, NO text appears. Your
speech is turned into text only when you ask for it - by pressing **Pause**, or
by committing with **Insert** or **Send**. The whole captured clip is
transcribed in one call.

Why: transcribing the whole utterance at once is materially higher quality than
streaming word-by-word. Streaming partials also lightly reword phrasing as they
revise. We removed streaming on purpose (issue #589) and we are not bringing it
back. We ARE bringing back the ability to pause.

**Pause is a checkpoint, not an ending.** Press Pause and the dialog transcribes
everything you have said since the last checkpoint, appends it to the transcript,
and shows it to you. You cannot resume while that transcription is running -
Resume re-enables only once the text has landed. Then you may Resume and keep
talking; the next Pause transcribes the new audio and appends it. The transcript
grows segment by segment.

This means a "segment" is one stretch of speech between checkpoints, and the
visible transcript is the accumulation of all transcribed segments, which the
user may also edit by hand while paused.

---

## 2. States

```
            +-----------------------------------------------+
            v                                               |
  (open) -> RECORDING --Pause--> TRANSCRIBING --> PAUSED ---+ (Resume)
               |    \                 ^             |
             Insert  \ Send        Insert         Insert/Send
               |      \ (close now)   |             |
               +---> TRANSCRIBING     |             v
               |          |           |          (commit)
               |          v           |
               |       (commit)       |
               +---> (close now) -----+---> [background transcribe + submit;
                                             session shows "Transcribing" - section 10]

  any stage --(unrecoverable error)--> FAILED
```

Note the fork at RECORDING: **Insert** goes through the in-dialog TRANSCRIBING
state (it must produce text to hand back), while **Send** closes the dialog
immediately and the transcription happens in the background (section 10). PAUSED
already holds transcribed text, so both Insert and Send commit from it instantly.

| State | Meaning | Mic | Transcript box |
| --- | --- | --- | --- |
| RECORDING | Capturing audio. No text yet. | live | empty; explanatory placeholder shows through; read-only |
| TRANSCRIBING | Turning the current segment into text. | stopped | shows accumulated text so far (if any); read-only |
| PAUSED | Checkpoint reached; reviewing. | stopped | accumulated text; EDITABLE |
| FAILED | Recording or transcription failed unrecoverably. | stopped | the error message; read-only; only Cancel/Close remains |

There is deliberately NO separate "connecting" state on batch surfaces: capture
starts immediately and no network connect precedes it. (The web surfaces may
show a brief "starting" flash only until the microphone permission resolves.)

---

## 3. Controls (labels, positions, behaviour)

Two groups. Recording controls / back-out on the LEFT, commit actions on the
RIGHT, with space between so Pause is never mis-clicked for Send.

LEFT group:

- **Cancel** (neutral). Closes the dialog, discards everything, returns no text.
  In FAILED it reads **Close**.
- **Pause / Resume** (neutral, the two-bar glyph - see section 4):
  - In RECORDING, shows the two-bar Pause glyph. Pressing it transcribes the
    current segment, appends it, and moves to PAUSED.
  - In PAUSED, reads the word **Resume**. Pressing it starts a fresh recording
    segment (RECORDING) that will append to the (possibly edited) transcript.
  - Disabled during TRANSCRIBING. This is the "you cannot resume until it has
    been transcribed" rule.

RIGHT group:

- **Insert** (green). Commits the transcript and closes WITHOUT auto-submitting,
  so the caller drops the text at the caret for the user to review/edit in place.
  From RECORDING it first transcribes the current segment and appends it - Insert
  MUST wait, because the point of Insert is to hand back editable text the user
  then reads, so there is nothing to hand back until the transcript exists.
- **Send** (blue, primary). Auto-submits the prompt into the session. From
  RECORDING, Send is **fire-and-forget** (see section 10): the moment it is pressed
  the dialog captures the recorded audio buffer and CLOSES IMMEDIATELY, and the
  transcribe-and-submit runs in the background. Send does NOT wait for the
  transcript, because the transcript cannot be seen on this dialog anyway (Send
  submits it straight into the session), so holding the screen is pure dead time.
  From PAUSED the text is already transcribed, so Send submits it instantly.

Commit from PAUSED uses the text currently IN THE BOX (the user's edits win),
not the raw accumulator.

Keyboard: Enter = Send (except while the focused transcript box is being edited
in PAUSED, where Enter inserts a newline). Escape = Cancel.

---

## 4. The Pause glyph

Two vertical bars, built from two solid rectangles (NOT a Unicode pause
character - the project forbids Unicode in any output). Each bar is 4 wide by 14
tall, 5 apart, in the same neutral foreground colour as the button text. When
the button is in its Resume state it shows the plain word "Resume" instead.

---

## 5. Transcript box placeholder text

The empty box must explain the batch model rather than imply live text. Use:

```
Speak naturally - your words are turned into text when you pause or finish,
not while you talk (it is more accurate that way). Press Pause any time to
see what you have said so far.
```

Surfaces with a narrow box (phone) may use the shortened form:

```
Your words appear when you pause or finish - press Pause to see them so far.
```

---

## 6. Microphone selector

A dropdown at the top listing capture devices, defaulting to the system default
and persisting the chosen device by NAME (indices reorder across replugs).
Changing device restarts the current segment on the new device; audio buffered
on the abandoned device is discarded (mixing two devices into one clip is not
meaningful). The phone has NO microphone selector (it uses the OS default).

---

## 7. Equalizer, timer, level hint

- A nine-bar equalizer driven by the real microphone level. Red while RECORDING,
  amber while TRANSCRIBING, parked grey while PAUSED/FAILED.
- A timer showing total elapsed capture across all segments (it freezes during
  TRANSCRIBING and PAUSED and resumes adding when recording resumes; it never
  ticks in FAILED).
- A one-line hint row (reserved height so the layout never jumps) used for
  "speak up" when the input is too quiet and for "Transcribing..." while busy.

---

## 8. Per-surface deviations (the ONLY allowed differences)

| Surface | Allowed deviation |
| --- | --- |
| Desktop Avalonia | none - this is the reference implementation |
| Blazor Cockpit | hosted as an in-page overlay rather than an OS window |
| Plain HTML | hosted as an in-page overlay; same JS module as Cockpit |
| Phone | smaller layout; no microphone selector; shortened placeholder |

Anything else that differs between surfaces is a bug against this spec.

---

## 9. Implementation notes

- Desktop captures and transcribes in-process via `BatchDictationRecorder`; each
  segment is one recorder instance, transcribed once on Pause / commit, and the
  dialog accumulates the cleaned segments with `DictationText.Join`.
- The web surfaces share ONE JavaScript module driving the `/dictate` WebSocket.
  Pause maps to a batch "flush" control frame: transcribe the buffer so far,
  return one final segment, keep the socket open for Resume. No `partial` frames
  are consumed; the streaming path is not used.
- Fail-open on cleanup is unchanged (see `EDIT_DOCUMENT_CLEANUP.md`): a turn with
  no dictionary term comes back byte-identical to the raw transcription.
- Every surface writes the same `DictationSessionRecord` JSONL audit line with
  its own `Source` tag.

---

## 10. Fire-and-forget Send and the "Transcribing" session state

Send from RECORDING must never make the user wait. The instant Send is pressed:

1. The dialog stops the microphone and grabs the recorded audio buffer (plus any
   already-transcribed PAUSED prefix text). The buffer is independent of the
   recorder, so it survives the dialog closing.
2. The dialog CLOSES immediately - the screen is released.
3. A background task, decoupled from the dialog's lifecycle, does the rest:
   mark the session **Transcribing**, transcode/upload/transcribe the buffer,
   join it onto the prefix, submit the result into the session, then clear the
   **Transcribing** mark. The mark is cleared whether the submit succeeded or
   failed (so a failure never leaves the session stuck), and each surface applies
   a generous stale-mark backstop so a crashed or offline client cannot pin a
   session **Transcribing** forever.

### The Transcribing session state (roster signal)

While that background work runs, the session is shown as **Transcribing** on every
roster/rail that lists it: an **orange** dot with a **"Transcribing..."** label.
This tells the owner - and anyone else on the fleet - that the session is busy
receiving a dictated message, so nobody else starts typing into it mid-dictation.
It is orange, not red, so it is "active", never "needs you". On-hold still wins
over it (a parked session stays grey).

### Where the flag lives, per surface

The mechanism differs because each surface reads its roster from a different place,
but the visible result is identical:

| Surface | How Transcribing is marked and surfaced |
| --- | --- |
| Mobile PWA / Blazor Cockpit | Gateway-owned transient flag: the client calls `POST /sessions/{sid}/transcribing`, the Gateway stamps `SessionDto.Transcribing` onto the roster it serves, and `SessionOrdering.EffectiveColor` folds it to orange. Not forwarded to the Director. |
| Desktop Avalonia | In-process flag on the `Session` object (`IsTranscribing`), honored by `SessionStatusWingman` and painted by the session rail. The desktop reads its own in-process sessions, not the Gateway roster, so it does not use the Gateway flag. |
| Plain HTML (Director view) | Inherits the "release immediately + background submit" speed from the shared web module. The orange roster flag is a Gateway/desktop concept, so it does not light up on this stand-alone page. |

### The one place Send still waits

From PAUSED there is nothing to transcribe (the text already exists), so Send
submits instantly with no background step and no Transcribing state. Insert always
waits for the transcript, on every surface, because Insert exists to hand back
editable text.

### Desktop Send: deliver now or fail loudly - no queue

A desktop Send/Speak dictation either goes into its session at once or reports the
failure at once. There is **no durable queue and no background retry**: the clip is
transcribed once, and if the target session is idle at its prompt it is submitted; if
transcription fails, or the session is not accepting input, the words are dropped and
the failure is surfaced with a modal the user cannot miss. Any text the user had typed
is put back so it is never lost.

This deliberately replaced an earlier hold-and-retry design (a `pending-dictations`
disk store plus a 30-second sweeper) whose persistent "your words are saved and being
sent automatically" notice sat quietly at the bottom of the window. In practice
everyone missed that notice, and undeliverable clips - a session that never became
ready, a missing transcription key - piled up in the store retrying forever. A loud
immediate failure is clearer than a quiet promise of eventual delivery, so the queue
and its notice were removed:

1. **Transcribe once.** The recorded clip is transcribed off the UI thread while the
   session shows the orange **Transcribing** flag. A transcription failure is reported
   immediately and the clip is discarded - it is not saved or retried.
2. **Deliver only into an idle session.** The dictation is submitted only when the
   target session is idle at its prompt. Typing into a busy, streaming composer is what
   piled up duplicate copies of the sentence (issue #1135); a not-ready session fails
   loudly ("the session was not accepting input") instead of being typed into or held.
3. **Never silent.** Every failure path pops a modal ("Dictation not sent: <reason>")
   and restores the typed text. Nothing is queued behind the user's back.

The orange **Transcribing** roster flag shows for the one transcription attempt; when
it clears, the dictation has either been delivered or the user has been told why not.
