# Issue #1188 - Gateway-side enforced session lock as a projection of the PENDING dictation record: proof

Task 3a of the Mobile Resiliency mission (#1181). A session is LOCKED for human input at the Gateway front
door exactly while a non-abandoned, undelivered (PENDING) dictation record exists for it. The lock is a
pure projection of the durable record from Task 2 (#1183): it never auto-releases on a timer; it clears
only when the record transitions to DELIVERED, ABANDONED, or FAILED, and it survives a Gateway restart.

## What changed (four files)

- `src/CcDirector.Gateway/Voice/VoiceUploadStore.cs`
  - PENDING is now an EXPLICIT on-disk marker carrying the owning `SessionId` (added to
    `DictationDeliveryRecord`), rather than "the absence of a terminal record". `MarkPending(uploadId,
    sessionId)` writes it; `IsPending` is now `State==Pending`.
  - `MarkDelivered` / `MarkAbandoned` / `MarkFailed` preserve the session id already on the record (so a
    transition keeps the owning session); `ClearFailed` now restores a PENDING marker carrying that session
    id (re-locking the session for the retry) instead of deleting the record.
  - `IsSessionLocked(sessionId)` = any record with `State==Pending && SessionId==sessionId`, computed from
    disk. `LockedSessionIds()` returns the distinct pending sessions.
- `src/CcDirector.Gateway/Api/GatewayDictationEndpoint.cs`
  - Register writes the durable PENDING marker with the session id (`uploads.MarkPending(uploadId, sid)`),
    so the owning session is on disk and the lock survives a restart. The DELIVERED/ABANDONED short-circuit
    is unchanged; a FAILED/PENDING re-register overwrites to a fresh PENDING marker (keeping the chunks).
  - A comment at the injection site records that the Gateway's own delivery (`client.PostPromptAsync`
    direct to the Director) BYPASSES the guarded Gateway endpoint and is therefore exempt from the lock.
- `src/CcDirector.Gateway/Api/GatewayEndpoints.cs`
  - A `VoiceUploadStore? dictationUploads` parameter and a local `DictationLock(sid)` helper. The
    human-text front-door entry points reject with **423 Locked** and body
    `{ error: "This session is receiving a dictation. You cannot send input until it arrives or is
    cancelled." }` when the session is locked: `POST /sessions/{sid}/prompt` (the primary),
    `POST /sessions/{sid}/upload-image`, and `POST /sessions/{sid}/recap`. The voice-turn and wingman voice
    paths are listed as deferred.
- `src/CcDirector.Gateway/GatewayHost.cs`
  - The single `GatewayEndpoints.Map(...)` call now passes the existing `_dictationUploads` instance, so the
    lock reads this host's own on-disk root (one store per Gateway - not a shared static). Confined to that
    call site plus the Map signature; the Task 2 sweep-timer removal and the transcription lane's Map lines
    are preserved.

No timer anywhere (pure projection). No Director, `TranscribingSessions`, client, or cockpit change.

## Acceptance criteria -> proof

### 1 + 2. Persist the sessionId on the durable record; `IsSessionLocked`

`VoiceUploadStoreTests.MarkPending_LocksTheSession_CarryingTheSessionIdOnDisk`: `MarkPending` makes
`IsPending` true, records the session id on disk, `IsSessionLocked(sid)` true (and an unrelated session
false), and a FRESH store over the same root still reports the lock (restart-safe).
`LockedSessionIds_ReturnsTheDistinctPendingSessions` proves the distinct-session query. Register wiring is
proven end-to-end below (the HTTP register locks the session).

### 3. Reject human input at the Gateway front door

- `DictationSessionLockTests.RegisteringADictation_LocksTheSession_PromptRejectedWith423AndTheMessage`
  (real GatewayHost): a real `POST /dictation/upload` for session X locks it, then
  `POST /sessions/X/prompt` returns **423** with the exact message.
- `UploadImageAndRecap_AreAlsoRejectedWhenLocked`: `/upload-image` and `POST /recap` also return 423 when
  locked.

### The lock clears only when the record leaves PENDING (never auto-releases)

- `DictationSessionLockTests.DeliveredClearsTheLock` / `AbandonedClearsTheLock` / `FailedClearsTheLock`: a
  seeded PENDING session returns 423 at `/prompt`; after `MarkDelivered` / `MarkAbandoned` / `MarkFailed`
  the lock is gone (`/prompt` is no longer 423 - it proceeds to the session lookup, 404 with no Director).
- `VoiceUploadStoreTests.IsSessionLocked_ClearsWhenTheRecordLeavesPending`: with three PENDING uploads for
  one session, the lock stays on until EVERY record has left PENDING (delivered, then abandoned, then
  failed) - a pure projection, no timer.

### 4. The dictation's own injection is not blocked

Written trace (and code comment at the injection site): `RunCompleteCoreAsync` injects with
`client.PostPromptAsync(directorEndpoint, sid, ...)`, which calls the owning Director's control API
directly. That is a different host from the Gateway's own `/sessions/{sid}/prompt` endpoint where the guard
lives, so the dictation being delivered never passes through the guard and is naturally exempt. The guard
(`DictationLock`) exists ONLY on the Gateway front-door endpoints in `GatewayEndpoints.cs`.

### 5 + 6. Never auto-releases; restart-safe

- No timer exists anywhere in the change; the lock is purely `IsSessionLocked` reading the PENDING marker.
- `DictationSessionLockTests.LockSurvivesAFreshGatewayInstance`: a fresh `GatewayHost` over the same on-disk
  root with a PENDING record still reports the session locked and returns 423 at `/prompt`.

### Task 2 / Task 7 behavior stays coherent under the explicit-PENDING model

The Task 2 and Task 7 store/endpoint tests were updated to the explicit model (a registered upload has a
PENDING marker; `ClearFailed` restores a PENDING marker) and all still pass, including the durable de-dupe
(DELIVERED/ABANDONED short-circuit unchanged), the FAILED retry re-entry, and the permanent-failure mapping.

## Test output

Dictation store + lock + dedupe + permanent-failure + outcome suite (52 tests) - all passed. The 6 lock
tests:

```
Passed DictationSessionLockTests.RegisteringADictation_LocksTheSession_PromptRejectedWith423AndTheMessage
Passed DictationSessionLockTests.DeliveredClearsTheLock
Passed DictationSessionLockTests.AbandonedClearsTheLock
Passed DictationSessionLockTests.FailedClearsTheLock
Passed DictationSessionLockTests.LockSurvivesAFreshGatewayInstance
Passed DictationSessionLockTests.UploadImageAndRecap_AreAlsoRejectedWhenLocked
```

Gateway build: 0 warnings, 0 errors. Wider regression over the shared surface (VoiceUpload, VoiceTurn,
Dictation, Dedupe, Transcription, Recording, GatewayHost, GatewayEndpoints, SessionsAggregation, session
proxy, prompt): 229 tests passed, 0 failed.
