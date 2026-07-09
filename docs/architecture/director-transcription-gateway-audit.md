# Audit: CC Director transcription Gateway boundary

Date: 2026-07-08
Repository state: post Gateway-only transcription cleanup

## Current rule

All production audio-to-text outside the Gateway goes through:

1. A Director surface captures complete audio.
2. The Director calls `GatewayTranscriptionClient`.
3. `GatewayTranscriptionClient` sends the audio to the Gateway `POST /transcription` endpoint.
4. The Gateway resolves mode, key, provider URL, model, chunking, and dictionary correction.
5. The Gateway-owned transport makes the provider request and returns text to the caller.

Director/Core code must not resolve transcription providers, hand out transcription routing tuples,
instantiate provider transcription classes, or call OpenAI-compatible transcription endpoints.

## Removed escape hatches

- `TranscriptionRoutingEndpoint` and `GET /transcription/routing` were removed. The Gateway no
  longer exposes provider URL/key/model tuples to callers.
- `OpenAiKeyResolver.ResolveEndpointAsync` and the Core `ResolvedTranscription` DTO were removed.
  Core no longer has a helper that assembles a provider-direct transcription target.
- Provider/direct transcription classes were deleted from Core/Avalonia:
  `OpenAiTranscriptionProvider`, `OpenAiRealtimeProvider`, `OpenAiRealtimeProtocol`,
  `LivePreviewTranscriber`, `DictationPipeline`, `DictationSession`, `OpenAiSttService`,
  `OpenAiRecordingTranscriber`, and related interfaces/tests.
- `BatchTranscriptionPipeline` was moved out of Core and into
  `src/CcDirector.Gateway/Transcription/` under the Gateway namespace.

## Active production paths

| Path | Current transcription behavior |
|---|---|
| Desktop dictation recorder | Captures audio locally, posts WAV to `GatewayTranscriptionClient`. |
| Control API `/dictate` websocket | Captures PCM, wraps one WAV on stop, posts to `GatewayTranscriptionClient`. |
| Durable dictation retry | Replays saved audio through `GatewayTranscriptionClient`. |
| Voice command / voice-turn | Posts recorded audio to `GatewayTranscriptionClient`. |
| Wake-word test dialog | Uses the Gateway-backed batch recorder; no realtime provider remains. |
| Gateway phone/browser/recording paths | Use `GatewayTranscriptionService` and the Gateway-local `BatchTranscriptionPipeline`. |

## Guardrail

`GatewayOnlyTranscriptionGuardTests` scans production source and fails if Director code reintroduces
direct transcription classes or the removed `/transcription/routing` escape hatch. The allowed
provider transport implementation lives only under `src/CcDirector.Gateway/Transcription/`.
