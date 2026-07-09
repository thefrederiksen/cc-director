# Gateway-Owned Transcription

Production transcription is Gateway-owned.

Director applications may capture audio, assemble chunks, display recording/transcribing state, and
deliver the final text into a session. They must not resolve transcription providers, read
transcription keys, call OpenAI-compatible `/audio/transcriptions`, or instantiate provider/direct
transcription classes.

Allowed production audio-to-text path:

1. Director captures complete audio.
2. Director sends the audio to the Gateway transcription endpoint.
3. Gateway resolves mode, key, provider URL, model, chunking, and dictionary correction.
4. Director receives text and continues its local workflow.

Guardrail: `GatewayOnlyTranscriptionGuardTests` fails if production Director code instantiates direct
transcription classes such as `BatchTranscriptionPipeline`, `OpenAiTranscriptionProvider`,
`OpenAiRealtimeProvider`, `LivePreviewTranscriber`, `OpenAiSttService`, or
`OpenAiRecordingTranscriber` outside Gateway-owned transcription code.

The old `GET /transcription/routing` escape hatch is removed. Callers must never receive provider
URL/key/model tuples for transcription; they send audio to `POST /transcription` and receive text.
