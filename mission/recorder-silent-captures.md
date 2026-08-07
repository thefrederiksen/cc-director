# Mission: did we actually capture audio, or only silence?

## Why this is urgent

Soren spent 6 Aug 2026 at the CPMC conference in Montreal recording sessions with
the PWA recorder. Thirteen recordings landed. **Most captured essentially no
sound.** He may have lost a day of conference content, and this is his own
product failing at the job it was fixed for yesterday.

The first question to answer is the one that decides everything else:

**Is the AUDIO on the Gateway silent, or is the audio fine and only the
transcription failed?**

If the audio has speech in it, the day is recoverable by re-transcribing. If the
audio is silent, the content is gone and the job is to make sure it never happens
silently again.

## The evidence

Continuous speech transcribes at roughly 800-1000 characters per minute. Today:

| id (prefix) | start UTC | minutes | segments | seg/min | chars/min | state |
|---|---|---|---|---|---|---|
| 56b96c75 | 13:17 | 61.2 | 29 | 0.47 | 266 | transcribed, REAL content |
| 1303d6c4 | 15:00 | 1.0 | 1 | 1.0 | 90 | transcribed |
| 1199a46c | 15:01 | 5.7 | 2 | 0.35 | 26 | empty in practice |
| ad7b8d17 | 15:12 | 7.1 | 3 | 0.42 | 32 | empty in practice |
| ec2d379d | 15:19 | 42.0 | 8 | 0.19 | 11 | empty in practice |
| 45a098f7 | 17:31 | 30.5 | 3 | 0.10 | - | **error, no transcript** |
| ed1f1474 | 18:01 | 0.1 | 1 | - | 170 | transcribed |
| 8a06876b | 18:01 | 2.2 | 2 | 0.91 | 19 | empty in practice |
| 4154a8de | 18:29 | 30.7 | 5 | 0.16 | - | **error, no transcript** |
| 1c8b6f6c | 19:15 | 1.0 | 1 | 1.0 | 14 | empty in practice |
| 8efc7bc1 | 19:42 | 16.3 | 3 | 0.18 | 6 | empty in practice |
| 6e741948 | 20:02 | 18.9 | 3 | 0.16 | 9 | empty in practice |
| 2a47fc64 | 20:35 | 15.3 | 14 | 0.92 | 353 | transcribed, REAL content |

Full ids are obtainable from `GET /ingest/recordings`.

**The correlation is the lead.** Every recording with roughly one segment per
minute produced real content. Every recording rotating a segment only every five
or six minutes produced nothing. `SEGMENT_MS` is 60 seconds, so a five-minute gap
means the rotation timer was not firing on schedule.

What little text came back from the dead recordings is hallucinated, not sparse:
"is the tallest mountain in the world", a line of Russian, "Die Tur." That is a
transcription model inventing text from silence.

## Leading hypothesis - verify, do not assume

The browser was backgrounded or the screen locked, so `setTimeout` was throttled.
The same timer drives segment rotation AND keeps the capture pipeline alive, so
rotation stretched to minutes and the captured audio degraded to near-silence.
This matches the platform limit already documented in
`mission/recorder-unlimited-capture.md`: a web app cannot hold audio capture
through a locked screen.

Rule that out or confirm it. Other candidates: the microphone being claimed by
another app, an OEM audio-focus policy, or the stream being live but muted.

## What to do

1. **Pull the actual audio.** `GET /ingest/recording/{id}/audio/{index}` returns
   the stored segment bytes. For a working recording (56b96c75, 2a47fc64) and a
   dead one (ec2d379d, 8efc7bc1, 6e741948), measure real levels - mean and peak
   dBFS per segment, with ffmpeg `volumedetect` or equivalent. Report the numbers.
   This single measurement answers the capture-versus-transcription question.
2. **If the audio has speech**, work out why transcription produced nothing and
   whether the day can be re-transcribed. That is the recovery path and it matters
   most.
3. **If the audio is silent**, prove where it went silent - from the first segment,
   or partway through - and tie that to the rotation gaps.
4. **The two error recordings** (45a098f7, 4154a8de) have no transcript at all.
   Find out what the error was and whether their audio survived.
5. **Then the product fix.** Whatever the cause, a recording that captures silence
   must not look like a successful recording. Soren believed he had a day of
   conference audio. The app should have told him otherwise while it was still
   fixable - a live input-level indicator, a silence warning, or a refusal to
   report success on a recording with no signal.

## Access - read this before you start

The `/ingest` surface REFUSES an agent session key with 403
`session_key_out_of_scope`. It accepts a per-device key. The working route is the
signed-in `cencon` browser profile, which holds that key as a cookie:

```bash
eval "$(powershell -NoProfile -File "$LOCALAPPDATA/cc-director/connections/bh-profiles.ps1" env cencon)"
browser-harness <<'PY'
new_tab("https://gateway.devthrottle.com/ingest/recordings")
wait_for_load()
print(js("document.body.innerText"))
PY
```

The cookie is `cc-gateway-token` and it also works as an `Authorization: Bearer`
header if you would rather use curl. It expires 22 Aug 2026.

## Definition of done

A clear, evidenced answer to whether the audio exists, a recovery path if it does,
and a product change so a silent recording can never again be presented as a
successful one. Report the measured dBFS numbers, not an impression.

## Outcome - investigated 6 Aug 2026 (session 70519565)

**The audio is silent at the source. The locked-screen stretches were never
captured, and the content is not recoverable.** Measured, not inferred:

Only the two error recordings still hold audio - a successful transcription
deletes its segment files (`CleanupSegmentFiles` in
`src/CcDirector.Core/Recording/RecordingIngestService.cs`), so every
"transcribed" recording returns 404 on `/audio/{index}`. The transcription
failure on the two error recordings is what preserved the evidence.

Measured with ffmpeg volumedetect on all 8 stored segments:

| recording | segment | duration | mean dBFS | peak dBFS | verdict |
|---|---|---|---|---|---|
| 45a098f7 | 0 | 60.6 s | -28.4 | -0.0 | real speech |
| 45a098f7 | 1 | 29 min 26 s | -90.3 | -35.7 | silence, no interval above -55 dBFS |
| 45a098f7 | 2 | 2.5 s | -31.8 | -13.7 | real speech (stop tail) |
| 4154a8de | 0 | 60.6 s | -41.3 | 0.0 | audio present |
| 4154a8de | 1 | 86.4 s | -91.0 | -91.0 | pure digital silence |
| 4154a8de | 2 | 59.7 s | -25.7 | 0.0 | real speech |
| 4154a8de | 3 | 27 min 13 s | -90.3 | -31.8 | silence, no interval above -55 dBFS |
| 4154a8de | 4 | 0.9 s | -15.7 | -1.3 | real speech (stop tail) |

The pattern confirms the hypothesis exactly: unlocked phone = 60-second
segments with speech; locked phone = one giant stalled segment of silence;
audio returns the instant the screen unlocks (the short, loud stop tails).
Silence begins at the START of each stalled segment, so nothing inside a
stalled stretch ever reached the encoder as sound.

The two error recordings failed because the upstream transcription service
returned 400 on the giant silent segments (3 chunk attempts x 5 job attempts,
exhausted). The already-transcribed real chunks kept their per-chunk text on
the Gateway. The real-speech segments were pulled and transcribed locally with
whisper: a speaker introduction ("what she's going to talk about... massive
science and information management"), a talk fragment about time-to-money
metrics, and closing thank-yous - about 3.5 minutes total across both.

What survived of the day: the 61-minute morning session (56b96c75) and the
15-minute 16:35 session (2a47fc64), both with real transcripts. The middle of
the day - roughly 15:00 through 20:02 UTC - is silence and is gone. The dead
"transcribed" recordings cannot be re-measured (audio deleted), but they carry
the identical stalled-rotation signature and their sparse text is hallucinated
from silence.

Product fix filed as issue #2468: live input-level meter, silence detection
during capture, stalled-rotation warning, and a silent recording must be
flagged, never shown as an ordinary success.
