# Gateway Transcription Implementation Report - 2026-07-07

## Summary

This pass created the unified Gateway transcription job protocol and migrated the
first client paths to it. The migrated clients now capture audio and upload it to
the Gateway; the Gateway owns assembly, transcription, correction, retries,
status, and result provenance.

## Implemented

| Area | Implementation | Proof |
| --- | --- | --- |
| Gateway protocol | Added `POST /transcription/upload`, `PUT /transcription/{jobId}/chunk/{index}`, `POST /transcription/{jobId}/complete`, `GET /transcription/{jobId}/status`, and `GET /transcription/{jobId}/result`. | `dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj --no-restore` passed. |
| Gateway storage | Added `CcStorage.TranscriptionUploads()` for the protocol upload inbox. | Full solution build passed. |
| Mobile PWA Pause/Insert transcription | Replaced `/wingman/utterance/*` calls in shared client-core with `/transcription/*`. | `npm run typecheck --workspace @devthrottle/client-core` and `npm run typecheck --workspace @devthrottle/mobile` passed. |
| Desktop Speak/FIFO/VoiceView transcription | Replaced desktop `BatchDictationRecorder` direct provider pipeline with Gateway `/transcription/*` upload/complete. | `dotnet build src/CcDirector.Avalonia/CcDirector.Avalonia.csproj --no-restore` passed. |
| Desktop durable Send transcription | Replaced `DictationTranscriber` direct provider execution with the Gateway transcription job client. | `dotnet test cc-director.sln --no-build` passed. |
| Director voice compatibility endpoints | Replaced `VoiceService` transcription execution with the Gateway transcription job client; `/voice/command` and `/voice/utterance/*` are now compatibility shells around Gateway-backed transcription. | `dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --no-restore --filter "VoiceEndpointTests"` passed. |
| Director browser dictation | Replaced `/dictate` WebSocket stop-time transcription execution with the Gateway transcription job client. | `dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --no-restore --filter "DictationEndpointTests"` passed. |
| Android voice dictation | Replaced Director `/voice/utterance/*` calls with Gateway `/transcription/*` upload/complete and updated callers to pass the Gateway URL. | `dotnet build phone/CcDirectorClient/CcDirectorClient.csproj --no-restore` passed with existing warnings. |
| Enforcement | Added architecture guard tests for direct transcription execution and legacy client endpoints. | `dotnet test src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj --no-build --filter TranscriptionGatewayGuardTests` passed. |
| Build health | Updated stale test compatibility around removed transcription mode APIs without restoring provider choice. | `dotnet build cc-director.sln --no-restore`, `dotnet test cc-director.sln --no-build`, `npm run typecheck --workspace @devthrottle/client-core`, `npm run typecheck --workspace @devthrottle/mobile`, and `npm run build` passed. |

## Remaining Work

| Area | Current state | Required migration |
| --- | --- | --- |
| Director web Voice tab | `session-view.html` still calls Director `/voice/utterance/*`, but transcription behind that endpoint is Gateway-backed. | Move the client directly to Gateway job protocol or remove the compatibility endpoint. |
| Director manager Voice panel | `manager.html` still calls `/voice/command`, but transcription behind that endpoint is Gateway-backed. | Move the client directly to Gateway job protocol or remove the compatibility endpoint. |
| Director browser dictation | `dictation-overlay.js` and `dictate.html` still open Director `/dictate`, but transcription behind that socket is Gateway-backed. | Move the client directly to Gateway job protocol if the socket is no longer needed for capture UX. |
| Director Control API compatibility shells | `/dictate`, `/voice/command`, `/voice/utterance/*`, and audio voice-turn branches no longer run direct provider transcription, but the old endpoint names remain. | Delete, alias through Gateway, or change to accept Gateway job ids only. |
| Desktop durable Send | `DictationTranscriber` now uses the Gateway job client, but send/inject semantics still live outside the Gateway job action model. | Move durable send to Gateway job action `inject_session` if strict single-action semantics are required. |
| Android long recordings | Recording ingest already goes through Gateway `/ingest/recording/*`, not the new job protocol. | Adapt to `recording_ingest` action if strict single-protocol consolidation is required. |
| Generated API schema | `packages/client-core/src/api/schema.ts` still reflects old Gateway routes until OpenAPI is regenerated. | Regenerate from the Gateway after endpoint cleanup. |

## QA Notes

Microphone QA was not performed. Automated proof covered compile/typecheck, static
guard enforcement, and source-level endpoint migration. Manual microphone QA is
still needed for desktop Speak, desktop FIFO/VoiceView, Mobile PWA Pause/Insert,
Android voice dictation, and the remaining Director web/control paths after they
are migrated.
