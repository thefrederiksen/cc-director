# Gateway Transcription Migration Tracker

## Objective

Upgrade all DevThrottle transcription paths so every client uploads audio to the
Gateway and the Gateway performs batch transcription through one protocol.

## Target Protocol

```text
POST /transcription/upload
PUT  /transcription/{jobId}/chunk/{index}
POST /transcription/{jobId}/complete
GET  /transcription/{jobId}/status
GET  /transcription/{jobId}/result
```

## Application Tracking

| App | UI surface | Current path | Target | Status |
| --- | --- | --- | --- | --- |
| Director / Control API | `session-view.html` Voice tab | Director `/voice/utterance/*` compatibility shell | Gateway transcription job with `voice_turn` or `return_transcript` action | Gateway-backed; client endpoint still old |
| Director / Control API | `manager.html` Voice panel | Director `/voice/command` compatibility shell | Gateway transcription job or endpoint removal | Gateway-backed; client endpoint still old |
| Director / Control API | `dictation-overlay.js` | Director WebSocket `/dictate` compatibility shell | Gateway transcription job with `return_transcript` or `inject_session` action | Gateway-backed; client endpoint still old |
| Director / Control API | `dictate.html` | Director WebSocket `/dictate` compatibility shell | Gateway transcription job with `return_transcript` action | Gateway-backed; client endpoint still old |
| Director / Control API | `/sessions/{sid}/voice-turn` audio branch | Director uploads audio through Gateway client before command execution | Gateway job id or Gateway-owned voice turn | Gateway-backed; final endpoint shape pending |
| Cockpit | Transcripts | Gateway `/ingest/*` read/manage | Keep Gateway read/manage surface | Already aligned |
| Cockpit | Dictionary | Gateway `/ingest/dictionary` | Keep as shared Gateway dictionary | Already aligned |
| Cockpit | Settings | Gateway settings and transcription config | Keep as Gateway settings surface | Already aligned |
| Mobile PWA | `DictationDialog` Pause/Insert | Gateway `/wingman/utterance/*` | Gateway transcription job with `return_transcript` action | Migrated |
| Mobile PWA | Speak Send | Gateway `/dictation/*` | Gateway transcription job with `inject_session` action | Not started |
| Mobile PWA | status strip and roster badge | Local status store from `/dictation/*` calls | Generic Gateway job status | Not started |
| Desktop app | Speak dialog Insert/Pause | Direct `BatchTranscriptionPipeline` | Gateway transcription job with `return_transcript` action | Migrated |
| Desktop app | Speak Send / background durable dictation | `DictationTranscriber` uploads audio to Gateway | Gateway transcription job with `inject_session` action | Gateway-backed; durable action semantics pending |
| Desktop app | FIFO / VoiceView | Direct `BatchDictationRecorder` | Gateway transcription job action | Migrated |
| Android app | Voice dictation | Director `/voice/utterance/*` | Gateway transcription job with `return_transcript` or `voice_turn` action | Migrated |
| Android app | Long recordings | Gateway `/ingest/recording/*` | Keep or adapt to same job backend for `recording_ingest` | Partially aligned |

## Enforcement Tracking

| Guard | Purpose | Status |
| --- | --- | --- |
| Architecture guard test for `BatchTranscriptionPipeline` references | Prevent non-Gateway transcription execution | Implemented |
| Client endpoint guard test | Prevent new `/voice/utterance`, `/wingman/utterance`, `/voice/command`, `/dictate`, `/audio/transcriptions` client calls | Implemented |
| Runtime provenance requirement | Every transcript must carry Gateway job id | Partially implemented |
| Old endpoint removal or alias tests | Prove old endpoints are gone or Gateway-backed | Partially implemented |
| Integration proof with fake provider | Prove every path creates a Gateway job | Partial; Director endpoint tests cover no-Gateway behavior, Gateway protocol tests cover job path |

## Implementation Log

| Date | Change | Proof |
| --- | --- | --- |
| 2026-07-07 | Created target architecture and migration tracker. | Docs added. |
| 2026-07-07 | Added Gateway transcription job protocol endpoint: upload, chunk, complete, status, and result. | `dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj --no-restore` passed. |
| 2026-07-07 | Migrated Mobile PWA `transcribeUtterance` from `/wingman/utterance/*` to `/transcription/*`. | `npm run typecheck --workspace @devthrottle/client-core` and `npm run typecheck --workspace @devthrottle/mobile` passed. |
| 2026-07-07 | Migrated Android voice dictation from Director `/voice/utterance/*` to Gateway `/transcription/*`. | `dotnet build phone/CcDirectorClient/CcDirectorClient.csproj --no-restore` passed with existing warnings. |
| 2026-07-07 | Migrated desktop `BatchDictationRecorder` from direct `BatchTranscriptionPipeline` to Gateway `/transcription/*`. | `dotnet build src/CcDirector.Avalonia/CcDirector.Avalonia.csproj --no-restore` passed. |
| 2026-07-07 | Added static architecture guards for direct transcription execution and legacy client endpoint calls. | `dotnet test src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj --no-build --filter TranscriptionGatewayGuardTests` passed. |
| 2026-07-07 | Updated stale test compatibility for removed transcription mode APIs without restoring provider choice. | `dotnet build cc-director.sln --no-restore` passed. |
| 2026-07-07 | Migrated Director `VoiceService`, `VoiceUtteranceService`, `DictationTranscriber`, and `/dictate` execution to the Gateway transcription job client. | `dotnet test cc-director.sln --no-build`, `npm run typecheck --workspace @devthrottle/client-core`, `npm run typecheck --workspace @devthrottle/mobile`, and `npm run build` passed. |

## QA Plan

No microphone is required for the core proof. Each path should have a synthetic
audio fixture test that uploads known bytes and verifies:

- A Gateway job is created.
- Upload status advances.
- The Gateway transcription service is invoked.
- The result includes `jobId`.
- The UI/client receives status or result through the Gateway contract.
- No non-Gateway project references transcription execution.

Manual microphone QA remains useful after automated proof:

- Desktop Speak Insert.
- Desktop Speak Send.
- Director browser dictation.
- Director Voice tab.
- Mobile PWA Speak Insert.
- Mobile PWA Speak Send on poor network.
- Android voice dictation.
- Android long recording upload.
