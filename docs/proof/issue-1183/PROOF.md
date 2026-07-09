# Issue #1183 - Durable per-upload-id dictation record: proof

Task 2 of the Mobile Resiliency mission (#1181). One durable per-upload-id record with a PENDING /
DELIVERED / ABANDONED lifecycle replaces the ephemeral staging-plus-in-memory-cache, so an undelivered
dictation is retained until delivered or abandoned, and a delivered (or abandoned) upload id is
de-duplicated forever - past the old one-hour window and across a Gateway restart - until the client
acknowledges it.

## What changed

Gateway (server):
- `src/CcDirector.Gateway/Voice/VoiceUploadStore.cs` - added the durable delivery record: `ReadRecord`,
  `IsPending`, `MarkDelivered`, `MarkAbandoned`, `Acknowledge`. A terminal transition writes the small
  `record.json` marker (atomic temp+move) FIRST, then discards the heavy `*.part` chunk bytes (resume is
  no longer needed) while keeping the marker. PENDING is the absence of a terminal record while chunks are
  staged. The unrelated voice-turn path (`Delete` / `SweepAbandoned`) is untouched.
- `src/CcDirector.Gateway/Api/GatewayDictationEndpoint.cs` - `complete` and `register` now short-circuit on
  a terminal tombstone (return the cached outcome, never inject a second turn); a successful delivery writes
  `MarkDelivered` as the immediate next step after the session accepts the prompt (instead of delete +
  in-memory cache); added `DictationOutcome.Dropped` for the abandoned read side; added the idempotent
  `POST /dictation/{uploadId}/ack`; removed the now-dead `SweepCompletes`. The in-memory single-flight is
  KEPT (it serializes concurrent completes so a still-PENDING id injects at most once); only the age sweep
  was removed.
- `src/CcDirector.Gateway/GatewayHost.cs` - removed the one-hour dictation sweep timer (block + field +
  dispose). There is deliberately NO age sweep: a fixed cut would reopen exactly the hole this closes.
  The transcription lane's `TranscriptionAnalysisEndpoint.Map` / `TranscriptionCleanupEndpoint.Map` lines
  were preserved.

Client (built on top of the Task 1 changes):
- `packages/client-core/src/api/client.ts` - after a terminal delivered/abandoned outcome (at register or
  complete) it calls the ack endpoint (best-effort, idempotent, keyed by upload id), and a cached-delivered
  register response is treated as a fresh success. Added `abandoned` to `DictationSubmitResult`.
- `packages/client-core/src/dictation/backgroundSend.ts` - a cached-delivered outcome is treated identically
  to a fresh success (drop the on-device copy); an abandoned outcome drops the copy and does not re-drive.

## The residual, deliberately-unfixed limitation

A Gateway crash in the few milliseconds between `PostPromptAsync` returning (the session accepted the
prompt) and the `MarkDelivered` marker landing on disk would let a later re-complete inject the turn a
second time. `MarkDelivered` is written as the immediate next statement after a successful inject to
minimize that window; it is documented in the code rather than papered over with a fallback.

## The four required scenarios (all proven)

### 1. Retention - PENDING chunks kept past the old one-hour window

`VoiceUploadStoreTests.PendingChunks_AreRetainedPastTheOldOneHourWindow_AndStillAssemble`: stage two
chunks, age the staging dir two hours into the past (well beyond the old one-hour cut), then assemble.
The upload is still `IsPending` and assembles in full ("AAABBB") - not swept. The runtime age sweep that
used to delete it was removed from `GatewayHost.cs`.

### 2. Restart - assemble a still-PENDING id from disk with a FRESH instance

`VoiceUploadStoreTests.RestartWithFreshStoreInstance_AssemblesPendingChunksFromDisk`: stage three chunks
in one store, then a FRESH `VoiceUploadStore` over the same root (a simulated restart) reports the id
`IsPending` and assembles it in full ("AAABBBCCC"). Register is idempotent and re-opens the same on-disk
directory.

### 3. Durable de-dupe - the correctness proof (injects at most once, across a restart)

- `VoiceUploadStoreTests.MarkDelivered_DiscardsChunkBytes_KeepsMarker_SurvivesFreshInstance`: after
  delivery the `*.part` bytes are gone, `record.json` remains, the id is no longer PENDING, and a FRESH
  store returns the same submitted outcome ("hello there").
- `DurableDictationDedupeTests.Delivered_ReComplete_ReturnsCachedOutcome_AndSurvivesAFreshGatewayInstance`:
  a delivered upload id re-completed over the HTTP front door returns `200 { submitted:true, transcript }`
  from the tombstone (with no chunks on disk the live path could only 409/err, never fabricate a submitted
  turn - so this is an unambiguous short-circuit), and it returns the SAME cached outcome from a SECOND,
  freshly-started `GatewayHost` over the same on-disk root. Zero re-injection.
- `DurableDictationDedupeTests.Delivered_Ack_RetiresTheTombstone_AndReCompleteAfterAckDoesNotReinject`:
  `POST /dictation/{id}/ack` returns `retired:true`, the record is gone from disk, and a re-complete after
  ack does NOT return a submitted outcome (nothing to re-inject; the client no longer holds a copy). A
  re-ack is a harmless `retired:false` no-op (idempotent).

### 4. Abandoned read side

- `VoiceUploadStoreTests.MarkAbandoned_WritesAbandonedTombstone_SurvivesFreshInstance`: an abandoned id is
  terminal (not PENDING) and a fresh store reads back the ABANDONED state with the reason.
- `DurableDictationDedupeTests.Abandoned_ReComplete_ReturnsAClearDroppedOutcome_WithNoInjection`: a
  re-complete of an abandoned id returns `200 { dropped:true, reason:"user cancelled" }` with no injection.
- `DurableDictationDedupeTests.ReRegister_OfATerminalUploadId_ReturnsTheCachedOutcome`: a re-register of a
  terminal id returns the cached outcome so the client drops its copy and acks instead of re-uploading.

Note on scope of the HTTP proof: the complete/register de-dupe short-circuits BEFORE it locates or injects
into a session, so the integration tests drive a real `GatewayHost` with no Director and simulate a prior
delivery by writing the tombstone to the same on-disk staging the Gateway reads. The tombstone WRITE on a
real delivery (the `MarkDelivered` immediately after a successful inject) is proven by the store unit test
above; a first delivery through the whole pipeline needs a live transcription provider.

### Client (ack + cached-delivered handling)

`packages/client-core/src/api/dictationDedupe.test.ts` (4 tests): a cached-delivered register response is
treated as a fresh success and acknowledged (no re-upload, no complete); a terminal delivered complete fires
the ack and returns submitted; an abandoned complete returns terminal + `abandoned` and acks; a failed ack
never turns a delivered turn into an error (best-effort, idempotent).

## Test output

Gateway store + HTTP de-dupe (21 tests, all passed):

```
Passed VoiceUploadStoreTests.PendingChunks_AreRetainedPastTheOldOneHourWindow_AndStillAssemble
Passed VoiceUploadStoreTests.RestartWithFreshStoreInstance_AssemblesPendingChunksFromDisk
Passed VoiceUploadStoreTests.MarkDelivered_DiscardsChunkBytes_KeepsMarker_SurvivesFreshInstance
Passed VoiceUploadStoreTests.MarkAbandoned_WritesAbandonedTombstone_SurvivesFreshInstance
Passed VoiceUploadStoreTests.Acknowledge_RetiresTombstone_AndIsIdempotent
Passed VoiceUploadStoreTests.ReadRecord_ForPendingOrUnknownUpload_IsNull
Passed DurableDictationDedupeTests.Delivered_ReComplete_ReturnsCachedOutcome_AndSurvivesAFreshGatewayInstance
Passed DurableDictationDedupeTests.Delivered_Ack_RetiresTheTombstone_AndReCompleteAfterAckDoesNotReinject
Passed DurableDictationDedupeTests.Abandoned_ReComplete_ReturnsAClearDroppedOutcome_WithNoInjection
Passed DurableDictationDedupeTests.ReRegister_OfATerminalUploadId_ReturnsTheCachedOutcome
(plus the pre-existing VoiceUploadStore tests - all still pass)
```

Wider regression - voice-turn + dictation + recording suites (shared `VoiceUploadStore`), 89 tests passed,
0 failed. Full client-core suite: 13 files, 140 tests passed. `tsc --noEmit` clean. Gateway build: 0
warnings, 0 errors.
