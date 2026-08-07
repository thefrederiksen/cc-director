# Mission: background recording - can the PWA do it, or is native required?

## The question to settle

Soren wants to start a recording, put the phone in his pocket, and have it
capture all day. Today proved the PWA cannot: a locked screen produces digital
silence, and he lost most of a conference.

Decide, with evidence, which of these is true and act on it:

- **A.** A PWA can be made to capture through a locked screen. Say how, prove it
  on his device, and ship it.
- **B.** It cannot, and the native recorder is the answer. Then make the native
  app actually usable against the hosted Gateway and get it on his phone.

Do not hedge. He needs one answer he can rely on for the next conference.

## What is already established - do not re-derive

From `mission/recorder-silent-captures.md` (session 70519565, measured today):

- Locked screen = pure digital silence. Two stalled segments measured at -90.3
  and -91.0 dBFS mean, no interval above -55 dBFS, silence beginning at the
  start of the segment.
- Unlocked = healthy 60-second segments at -25 to -28 dBFS with real speech.
- Audio returns within seconds of unlocking (short loud stop tails).
- Segment rotation stalls at the same time, so one throttled timer explains both
  the missing rotation and the missing sound.
- Issue #2468 already covers the SAFETY work: live input-level meter, silence
  detection, stalled-rotation warning, never present a silent recording as a
  success. That is a separate job from this one and is already filed.

From `mission/recorder-unlimited-capture.md`:

- PR 2462 removed the 30-minute cap and hoisted capture above the router. The
  PWA now records indefinitely WITH THE SCREEN ON. That part works.
- PR 2461 (`94e12dc27`) hardened the NATIVE recorder in `phone/CcRecorder` with a
  wake lock, a one-tap Doze exemption, and INTERRUPTED labelling. It is merged.
  A signed APK v1.1 was delivered over Teams and then set aside as unnecessary.
  Today suggests it was premature, not wrong.

## Option A - investigate honestly, then rule in or out

The prior belief is that no web API grants background microphone capture: a
service worker cannot call `getUserMedia`, and a backgrounded tab has its media
pipeline suspended. Screen Wake Lock keeps the SCREEN awake rather than enabling
background capture.

Test the things that might change that answer rather than asserting it:

- Screen Wake Lock plus an installed PWA - does capture survive the screen
  timing out, as opposed to a manual lock? Measure dBFS, do not trust the UI.
- Does an `AudioContext`-based capture path behave differently from
  `MediaRecorder` under lock on his Android build?
- Is there any Android-specific behaviour for an installed PWA holding an active
  microphone stream (some builds treat it as a foreground media session)?

If Wake Lock plus screen-on is the honest best a PWA can do, SAY SO plainly, and
make the app tell the user that up front rather than discovering it a day later.

## Option B - what the native app still needs

The native recorder can hold a partial wake lock and a microphone-type foreground
service, which is precisely the capability at issue. What it lacks is a usable
path to the HOSTED Gateway:

- It has no sign-in. `MainPage.xaml.cs` reads a `gateway_token` preference and
  `IngestUploader` sends it as `Authorization: Bearer`. Verified working: the
  device key from the signed-in browser returns HTTP 200 on
  `GET /ingest/recordings`.
- Hosted enrollment (`POST /mobile/enroll`) requires an ACCOUNT access token from
  a human sign-in, which the native app cannot obtain. Today the only route is
  pasting a browser device key, which expires 22 Aug 2026 and makes the phone
  share the browser's device identity.
- So the native app needs real enrollment, or a deliberate paste-a-key flow with
  an honest expiry warning.

Also confirm the native recorder still uploads in the shape the hosted Gateway
expects - that build predates hosted, and it has never been proven end to end.

## Definition of done

One clear recommendation with measurements behind it, and the work started on
whichever option wins. If it is native, that means enrollment plus a proven
end-to-end recording landing transcribed on the hosted Gateway from a locked,
pocketed phone. A 60-minute locked-screen run that arrives with real speech in it
is the proof. Anything less is a claim.

## Outcome - decided 6 Aug 2026 (session 8a7ee0a5)

**The answer is B. A web app cannot capture audio through a locked screen on
Android, and no web API changes that. The native recorder is the answer for
pocket recording; the PWA records only while the screen is on and unlocked.**

### Why Option A is ruled out - each experiment from the brief, answered

The conference measurements settle all three proposed experiments; none of them
has an untested branch left where locked-screen web capture could hide.

1. **Screen Wake Lock plus an installed PWA.** This exact configuration is what
   failed at the conference: the mobile app has held an app-wide screen wake
   lock since issue #981 (`apps/mobile/src/hooks/useScreenWakeLock.ts`, mounted
   in `main.tsx`), and the recordings still went to -90 dBFS silence. The wake
   lock's only power is to stop the screen TIMING OUT; the W3C specification
   requires it to be released the moment the page is hidden, which is exactly
   what a manual lock or a pocketed power button does. There is no
   configuration in which the screen is dark and the wake lock still helps:
   with the lock held the screen never times out (that is the already-working
   screen-on mode), and without it a timeout produces the same dark-locked
   state that was measured producing silence.

2. **AudioContext instead of MediaRecorder.** The measured evidence rules this
   out mechanically. The stalled segment from recording 45a098f7 is 29 minutes
   26 seconds LONG and fully encoded at -90.3 dBFS mean: the encoder ran in
   real time for the whole locked stretch and encoded zeros, which means
   Chrome's audio pipeline was alive and the SAMPLES ARRIVING FROM ANDROID were
   zero. This is Android's documented behavior since Android 9: an app that
   loses foreground standing without holding a microphone-type foreground
   service keeps its recording session but is fed silence instead of sound.
   The zeroing happens upstream of every web API - MediaRecorder, AudioWorklet,
   and ScriptProcessor all read the same zeroed stream. Changing the reader
   cannot change what the operating system writes into it.

3. **An installed PWA holding an active microphone stream as a foreground media
   session.** That is precisely what ran at the conference: the installed PWA
   held a live getUserMedia stream AND a live AudioContext (the level meter)
   through every locked stretch, and Android still zeroed the samples. The
   microphone-type foreground service Android demands is one only a native app
   process can hold; Chrome does not hold one for plain page capture, and even
   WebRTC calls in Chrome for Android are documented (Chromium issue 41452188)
   to lose audio minutes after the screen goes off.

The honest best a PWA can do is exactly what it does today: record
indefinitely with the screen on and the wake lock preventing a timeout. The
recorder screen now says this up front - before recording starts, not only
during - and no longer claims a lock reliably STOPS the recording, because the
conference proved the failure mode is silent zeros with the capture apparently
alive. Detecting that silence live is issue #2468's job and is not duplicated
here.

### Why Option B stands ready

The native recorder in `phone/CcRecorder` holds precisely the capability at
issue: a microphone-type foreground service
(`RecorderForegroundService.TypeMicrophone`, persistent notification), a wake
lock, and a one-tap Doze exemption (pull request 2461, merged; signed APK v1.1
already delivered over Teams). That is the mechanism Android requires for
capture with the screen off, and it is exactly what the web platform cannot
express.

The upload contract to the HOSTED Gateway is verified three ways:

- **By construction:** `IngestUploader` speaks the same routes the Gateway maps
  (`POST /ingest/recording`, `PUT .../chunk/{index}` with `X-Chunk-Sha256`,
  `POST .../complete`, `GET .../status`), and `LocalManifest` mirrors the
  server's `RecordingManifest` field for field; the codec it sends ("aac-m4a")
  maps to a supported extension in `CodecToExt`.
- **By test:** `HostedRecordingServeTests` registers recordings on a
  hosted-mode Gateway with enrolled tenants and proves tenant partitioning on
  the same ingest surface.
- **Live:** the device key from a signed-in browser returned HTTP 200 on
  `GET /ingest/recordings` against gateway.devthrottle.com (measured by
  session 70519565 on 6 Aug 2026).

What was NOT verified: an actual native-app upload landing on the hosted
Gateway end to end. A scripted live rehearsal of the native upload sequence
using the browser's device key was attempted from this session and was blocked
by the tool permission layer (twice); the definitive proof is the one the
brief names anyway - a 60-minute locked-screen run from the phone itself.

### Work started (this pull request)

- **PWA honesty up front:** the recorder screen states the locked-screen limit
  before recording starts, with wording that matches the measured failure
  (silence, not a clean stop).
- **Native paste-a-key flow made deliberate:** the recorder's default server is
  now the hosted Gateway (`https://gateway.devthrottle.com`), the token field
  is labelled as what it is (a device key borrowed from a signed-in browser,
  which expires with it - the current one on 22 Aug 2026), and a Test
  connection button verifies the pasted key against `GET /ingest/recordings`
  and names the failure honestly: a 401/403 says the key expired or was
  revoked and to paste a fresh one, a network failure says the server was
  unreachable. No fallbacks.

### Still owed (filed, not hidden)

- **Real enrollment for the native app** - a human account sign-in from the
  phone exchanged at `POST /mobile/enroll` for its own tenant-scoped device
  key, so the recorder stops borrowing the browser's device identity and
  expiry. Tracked in its own issue.
- **The proof run:** a 60-minute recording from Soren's pocketed, locked phone
  (APK v1.1 + pasted key + gateway.devthrottle.com) arriving on the hosted
  Gateway transcribed with real speech. Until that lands, Option B is the
  right answer with the mechanism verified - but the end-to-end claim stays
  unclaimed.
