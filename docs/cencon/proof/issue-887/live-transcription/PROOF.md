# #887 live hosted-transcription proof (2026-07-02)

Verified the DevThrottle hosted transcription path end-to-end against the LIVE cloud
(`https://devthrottle.com/api/v1`) using the real `dt_live_` key from the Gateway vault.

## Round-trip
1. **TTS** `POST /api/v1/audio/speech` (model `hexgrad/Kokoro-82M`, input "testing one two three four", `Authorization: Bearer dt_live_...`)
   -> HTTP 200, `audio/wav`, 90044 bytes (saved as `tts-clip.wav`).
2. **Transcription** `POST /api/v1/audio/transcriptions` (multipart file=tts-clip.wav, model `whisper-large-v3`)
   -> HTTP 200 `{"text":"Testing 1234."}`.

This is exactly the path `BatchTranscriptionPipeline` drives for `TranscriptionMode.DevThrottle`
(base URL, `whisper-large-v3`, Bearer dt_ key, multipart `/audio/transcriptions` returning `{text}`).
So #887's DevThrottle default is correct AND the hosted endpoint is alive and funded.

## Credential architecture (confirmed empirically)
- Inference endpoints (`/audio/speech`, `/audio/transcriptions`) authenticate with a **`dt_live_` API key** -> 200.
- Account endpoints (`/account/credits`, `/auth/me`, `/usage`) reject the `dt_live_` key with **401** (they are JWT-only).

## The auto-wiring gap (blocks the "fresh signed-in user just works" bar)
Sign-in stores only `{AccessToken JWT, RefreshToken}` in a DPAPI blob
(`config\gateway\devthrottle-credential.bin`); the transcription path reads `DEVTHROTTLE_API_KEY`
(a `dt_` key) from `keyvault.json`. Nothing bridges the two, and `LoopbackLoginListener` drops any
non-JWT param the `/activate` handback delivers. So a signed-in user with no manually-pasted `dt_`
key gets `TranscriptionOutcome.NoKey`. The manual-key path (proven above) works; the zero-config
signed-in path does not yet. Fixing that is #881's remaining essence.
