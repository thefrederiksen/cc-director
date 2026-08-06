# Mission: recorder captures all day, never auto-stops

## OUTCOME 2026-08-05 evening - shipped and live; proof run pending

The real cause was the PWA segment recorder's 30-minute DEFAULT cap (see the
correction below). Fixed, reviewed, merged, and deployed:

- Pull request 2462 (squash `c9564222f`): the cap is strictly opt-in with no
  default; the capture lifecycle moved out of the Recorder page into an
  app-level recording session above the router (navigation cannot kill it,
  finalization is identity-keyed, header writes are serialized, a lease
  heartbeat protects the live capture from cross-tab recovery); a recording
  banner with a live clock shows on every screen and tells the truth in every
  phase; a capture the system kills is salvaged and marked cut short both on
  the row and as a note that rides into the transcript; a service-worker
  update no longer reloads over a live recording. Reviewed by a different
  agent family over four rounds to a MERGE verdict.
- Deployed to the HOSTED Gateway (the one the phone uses) by the deploy
  workflow on green main: `gateway.devthrottle.com/mobile/build.json` stamps
  `c956422` and the served bundle was grep-verified to carry the new code and
  not the old cap constant. The run's post-swap watch measured an 11.3-second
  post-swap blip and failed the run by design - issue 2463 tracks it; the
  service self-recovered and no rollback was needed. The local Gateway's
  wwwroot/mobile was updated the same evening.
- Pull request 2461 (`94e12dc27`) hardened the NATIVE recorder first, on this
  brief's original wrong premise; it is merged, fine, and not what the owner
  uses. The APK delivered over Teams is superseded and can be ignored.

Still owed by the definition of done: the owner's long foreground test
recording (target 90 minutes) landing on the Gateway, transcribed end to end.
Honest platform limit, stated in-app and to the owner: a web app cannot
capture through a locked screen - keep the app open and the screen on;
if capture is cut anyway, the app says so and keeps everything captured.

## CORRECTION 2026-08-05 - THIS BRIEF TARGETED THE WRONG APP

Everything below the line was written about the NATIVE MAUI recorder in
`phone/CcRecorder`. That was wrong. Soren does not use that app.

He uses the **PWA mobile app** (`apps/mobile`), which has its own recording
feature alongside voice mode. That is the app that must record all day.

The real cause is not Doze and not a wake lock. It is an explicit cap:

- `packages/client-core/src/recorder/segmentRecorder.ts:77`
  `export const MAX_RECORDING_MS = 30 * 60_000;`
- Enforced in `armRotateTimer` (~line 262): on reaching the cap it sets
  `stopped`, releases the stream and fires `onAutoStop`, deliberately at a
  segment boundary.

That explains the evidence exactly: 1,800,275 ms across precisely 30 one-minute
segments. A Doze kill would not land that precisely on 30:00.

### What is actually wanted

1. **No recording limit.** Make the cap opt-in rather than a 30-minute default.
   Soren starts and stops it himself. He explicitly does not want a limit; 12
   hours is the ceiling he would tolerate if one is unavoidable.
2. **Recording survives navigation inside the app.** Today the recorder is
   constructed in `apps/mobile/src/pages/Recorder.tsx` (line ~270), a PAGE
   component, so leaving the page unmounts it. Hoist it above the router into
   app-level state so capture continues while he uses voice mode, chat, or
   anything else.
3. **A persistent recording indicator in the app chrome**, visible from every
   page, top corner. `apps/mobile/src/components/VoiceModeBanner.tsx` is the
   existing precedent for a global banner.

### Known constraint - do not paper over it

A PWA cannot hold audio capture through screen-off the way a native app can.
Foreground with the screen on is achievable; backgrounded or locked is not
reliable, and is worst on iOS Safari. Investigate what is actually attainable
(Screen Wake Lock API, a visibility-change warning) and REPORT the honest limit
rather than quietly shipping something that stops in his pocket. If capture does
stop, the app must say so plainly - a truncated recording must never look
complete.

### Status of the earlier work

Pull request 2461 (`94e12dc27`) hardened the NATIVE recorder with a wake lock,
a Doze exemption and INTERRUPTED labelling. It is merged and it is fine work,
but it was aimed at the wrong app and its premise about this 30-minute stop was
wrong. Do not build on it, do not revert it. The APK is not needed.

---


## Urgency

HARD DEADLINE: Soren needs a working build on his phone by the morning of
Thu 6 Aug 2026. He is speaking at the CPMC conference at Concordia University in
Montreal that afternoon and wants to record the WHOLE DAY - every session he
attends, plus his own talk at 3:15 pm.

A new mobile application build must be produced and installed on his device as
soon as possible. Shipping the code without getting an installable build onto
the phone is NOT done.

## What happened

On 5 Aug 2026 Soren recorded a 30-minute trial run of his talk on the phone
recorder. The recording is on the Gateway as
`e2c0839b-49af-4112-af1d-89d3a5966267`, titled "Recording 5 Aug 2026, 13:05".

Its duration is 1,800,275 ms. That is 30 minutes and 0.275 seconds, across
exactly 30 one-minute segments. He did not stop it. It stopped itself, and it
stopped mid-sentence: the transcript ends partway through a thought at [29:00].

## What Soren wants

1. **No recording time limit.** He explicitly does not want one. He will start
   and stop it himself throughout the day. If a safety stop is unavoidable for
   some platform reason, 12 hours is the ceiling he named, but his stated
   preference is none at all.
2. **Runs in the background.** He starts the recorder, switches to other apps,
   locks the screen, and capture continues uninterrupted.
3. **A persistent on-screen indicator while recording.** He wants to be able to
   glance at the top of the screen and know it is still running.

## What the code already does (verified 5 Aug 2026, read-only)

Do not rebuild what exists. In `phone/CcRecorder`:

- `Platforms/Android/AndroidAudioRecorder.cs:22` sets
  `SegmentLength = TimeSpan.FromMinutes(1)`. Segments roll on a
  `System.Threading.Timer` (line 138) with **no stop condition and no maximum
  segment count**. By design this rolls forever.
- `Platforms/Android/RecorderForegroundService.cs` already runs a foreground
  service with `ForegroundServiceType.TypeMicrophone` and a persistent
  notification on channel `cc_recorder_capture` (notification id 4801). So a
  background-capture mechanism and a status indicator both already exist.
- The Gateway imposes no cap either. `src/CcDirector.Gateway/Api/RecordingEndpoints.cs`
  has no maximum chunk index and no segment-count limit. `VoiceUploadLimits.MaxChunkBytes`
  is a per-chunk size cap (8 MB), unrelated to duration.

**There is no 30-minute limit anywhere in the source.** Searched the phone app
and the Gateway for duration caps, max-segment counts, auto-stop timers, and the
literal 1800. Nothing.

## Leading hypothesis - verify before fixing

The stop is environmental, not a coded limit. Android killed the capture.

The most specific evidence: `MainPage.xaml.cs:76` mentions a wakelock, but the
comment scopes it to draining the pending UPLOAD queue, not to audio capture.
If capture holds no wake lock, Doze and battery optimization are free to suspend
the process once the screen has been off for a while, which fits a clean stop at
a segment boundary about half an hour in.

Confirm this against the device before changing anything. Other candidates worth
ruling out: OEM battery management (aggressive on Samsung and Xiaomi),
Android 14+ foreground-service restrictions, and the `MediaRecorder` instance
itself failing on a roll and being swallowed by the `catch` near
`AndroidAudioRecorder.cs:478`.

## Definition of done

- A recording runs past 30 minutes on Soren's actual device, screen off, with
  other apps in the foreground. Target proof: a continuous run of at least
  90 minutes, uploaded and transcribed end to end.
- The persistent indicator stays visible for the whole run and accurately
  reflects that capture is live.
- An installable build is ON SOREN'S PHONE and he has confirmed it records.
- No silent truncation. If capture ever does stop for a reason outside our
  control, the app must say so plainly rather than presenting a short recording
  as a complete one.

## Notes

- Verify the artifact, not the return code. The failure mode here was a
  recording that looked complete and was not. Apply the same standard to the
  fix: watch a real long recording land on the Gateway before calling it done.
- Soren's own reporting is the tell that the current UX hides truncation: he
  believed he had a full talk recorded and did not learn otherwise until the
  transcript was read.
