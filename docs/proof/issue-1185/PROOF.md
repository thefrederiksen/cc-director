# Issue #1185 - Map PermanentError to a parked FAILED record + 422 on the dictation path: proof

Task 7 server half of the Mobile Resiliency mission (#1181). The Transcription lane landed the
permanent-failure signal (#1139): `GatewayTranscriptionService.TranscribeAsync` returns
`Outcome == TranscriptionOutcome.PermanentError` with `Code` in `unsupported_format` / `audio_too_large`
/ `non_decodable`. This maps it on the mobile dictation path so a genuinely-permanent failure STOPS the
endless retry loop, parks the recording, and releases the session, instead of returning a retryable 502
the durable queue re-drives forever. Built on top of the Task 2 (#1183) durable record model.

## What changed (two files only)

- `src/CcDirector.Gateway/Voice/VoiceUploadStore.cs`
  - Added `Failed` to `DictationDeliveryState`.
  - `MarkFailed(uploadId, reasonCode)` writes the FAILED marker but KEEPS the staged chunk bytes (the
    opposite of a delivered/abandoned tombstone, which discards them). Refactored the marker write into a
    shared `WriteRecordMarker` so `MarkDelivered`/`MarkAbandoned` still discard while `MarkFailed` does not.
  - `ClearFailed(uploadId)` returns a FAILED record to PENDING by deleting only `record.json`, keeping the
    chunks; a no-op for DELIVERED / ABANDONED / unknown.
  - `IsPending` is false for FAILED (marker present), true again after `ClearFailed`.
- `src/CcDirector.Gateway/Api/GatewayDictationEndpoint.cs`
  - The register/complete terminal short-circuit now fires ONLY for DELIVERED and ABANDONED; a FAILED id is
    NOT a short-circuit - it is cleared back to PENDING and re-drives (this complete/register IS the retry).
  - `RunCompleteCoreAsync` maps a non-Ok transcription result through the new internal
    `MapNonOkTranscription`: `PermanentError` -> `MarkFailed(uploadId, code)` + `DictationOutcome.Permanent`;
    the GUARD is exact - only `PermanentError` is mapped, every other non-Ok outcome (provider error,
    out-of-credits, no-key, transient) keeps its existing behavior.
  - `DictationOutcome.Permanent(reason)` maps to HTTP **422** `{ permanent: true, reason }`. It is NOT
    Terminal (so the always-remove drops it from `_completes` and a retry re-runs) and NOT Incomplete (so
    the existing `EndTranscribing` clears the orange mark) - terminal-for-this-attempt, retryable-for-the-record.
  - `TranslatePermanentReason` translates the codes at this boundary: `audio_too_large` -> `audio-too-large`;
    `unsupported_format` and `non_decodable` -> `unsupported-format`.

The concurrency single-flight is unchanged; only the classification is added. No other file was touched
(no `GatewayHost`, no transcription/transcoder/pipeline, no client, no Core desktop path, no cockpit).

## Acceptance criteria -> proof

### 1 + 2. PermanentError -> 422 { permanent, reason } with the translated reason, record FAILED, chunks kept

`DictationPermanentFailureTests.PermanentError_MapsTo422_ParksFailed_KeepsChunks` (Theory, all three codes):
a fabricated `GatewayTranscriptionResult.PermanentError(code)` drives `MapNonOkTranscription`; the returned
outcome's real HTTP result is `422 { permanent:true, reason:<translated> }` (executed against a live
`IResult`, so the exact wire body is asserted - the contract locked with the client half #1184), the record
is `Failed` carrying the code, `IsPending` is false, and the staged `*.part` chunk is retained. Translations
proven: `audio_too_large` -> `audio-too-large`, `unsupported_format` -> `unsupported-format`,
`non_decodable` -> `unsupported-format` (plus `TranslatePermanentReason_MapsEveryCode` including the default).

### 3. Do not dedupe-cache a permanent outcome

`Permanent_Outcome_IsNotTerminal_AndDoesNotKeepTheOrangeMark`: `DictationOutcome.Permanent` is not Terminal
(the endpoint's always-remove drops it from the `_completes` single-flight, so a retry re-runs the real
work) and not Incomplete (so the orange transcribing mark is cleared). The single-flight for the in-flight
run is untouched.

### 4. FAILED in VoiceUploadStore + retry re-entry

- `VoiceUploadStoreTests.MarkFailed_ParksTheRecord_KeepsChunkBytes_AndIsNotPending`: FAILED keeps both chunk
  files, `IsPending` false, `ReadRecord` is `Failed` with the reason.
- `VoiceUploadStoreTests.ClearFailed_ReturnsAFailedRecordToPending_KeepingChunks`: clearing returns to
  PENDING, `ReadRecord` null, and the chunks still assemble ("AAABBB").
- `VoiceUploadStoreTests.ClearFailed_IsANoOpForDeliveredAbandonedOrUnknown`: DELIVERED/ABANDONED tombstones
  and unknown ids are untouched.
- `DurableDictationDedupeTests.Failed_ReComplete_ClearsBackToPending_AndRetainsChunks` (HTTP, real Gateway):
  a fresh `complete` on a FAILED id is NOT a cached terminal - it clears FAILED back to PENDING (`ReadRecord`
  null, `IsPending` true) and retains the chunk for the retry.
- `DurableDictationDedupeTests.Failed_ReRegister_ClearsBackToPending_NoTerminalShortCircuit` (HTTP): a fresh
  register on a FAILED id clears it back to PENDING and returns a normal (no `terminal`) register response.
- The "and a later Ok delivers exactly once" tail: `Ok_ReturnsNull_SoTheCallerContinuesToInject` proves the
  Ok path returns null so the core continues to inject, and Task 2's delivered-tombstone tests prove that
  injection writes exactly one DELIVERED record. (A full over-cap WebM end-to-end belongs to the
  Transcription lane's transcode+split proof, per the issue.)

### 5. The guard holds server-side

- `Guard_ProviderError_IsNotMappedToPermanent_AndDoesNotParkTheRecord`: a `ProviderError` returns a
  retryable outcome and does NOT park the record FAILED (`ReadRecord` null - still PENDING, so a retry
  re-runs).
- `Guard_OutOfCredits_IsNotMappedToPermanent_AndDoesNotParkTheRecord`: out-of-credits is not reclassified
  as permanent and does not park the record.

## Test output

`DictationPermanentFailureTests` + the FAILED-state store tests + the HTTP retry re-entry - all passed:

```
Passed DictationPermanentFailureTests.PermanentError_MapsTo422_ParksFailed_KeepsChunks(code: "audio_too_large", expectedReason: "audio-too-large")
Passed DictationPermanentFailureTests.PermanentError_MapsTo422_ParksFailed_KeepsChunks(code: "unsupported_format", expectedReason: "unsupported-format")
Passed DictationPermanentFailureTests.PermanentError_MapsTo422_ParksFailed_KeepsChunks(code: "non_decodable", expectedReason: "unsupported-format")
Passed DictationPermanentFailureTests.TranslatePermanentReason_MapsEveryCode (all 4 cases)
Passed DictationPermanentFailureTests.Permanent_Outcome_IsNotTerminal_AndDoesNotKeepTheOrangeMark
Passed DictationPermanentFailureTests.Ok_ReturnsNull_SoTheCallerContinuesToInject
Passed DictationPermanentFailureTests.Guard_ProviderError_IsNotMappedToPermanent_AndDoesNotParkTheRecord
Passed DictationPermanentFailureTests.Guard_OutOfCredits_IsNotMappedToPermanent_AndDoesNotParkTheRecord
Passed VoiceUploadStoreTests.MarkFailed_ParksTheRecord_KeepsChunkBytes_AndIsNotPending
Passed VoiceUploadStoreTests.ClearFailed_ReturnsAFailedRecordToPending_KeepingChunks
Passed VoiceUploadStoreTests.ClearFailed_IsANoOpForDeliveredAbandonedOrUnknown
Passed DurableDictationDedupeTests.Failed_ReComplete_ClearsBackToPending_AndRetainsChunks
Passed DurableDictationDedupeTests.Failed_ReRegister_ClearsBackToPending_NoTerminalShortCircuit
```

Targeted store + dedupe + permanent + outcome suite: 42 passed, 0 failed. Gateway build: 0 warnings, 0
errors.

Wider voice/dictation/transcription regression (167 tests): 166 passed; the single failure was
`VoiceTurnEndpointTests.VoiceTurn_TextInput_EmitsTranscriptAndReplyStages`, a live voice-turn reply test
that waits up to 45 seconds for a real agent reply over SSE - it passed on its own (27s) and passed in the
Task 2 regression run earlier this session. It is a timing flake unrelated to the dictation record: my
changes only add new methods and a behavior-identical `WriteTombstone` refactor, and the voice-turn path
uses only `Register`/`StoreChunk`/`Assemble`/`Delete`, none of which changed.
