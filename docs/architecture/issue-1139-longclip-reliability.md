# Issue #1139 - Long-clip transcription reliability (evidence + plan)

Transcription lane (worker), coordinating with Mobile Resiliency (owns upload/chunk lifecycle,
endpoint/client mapping, loop-stop). This lane owns: provider per-attempt timeout, fallback/routing,
WebM/Opus -> PCM WAV transcode + sub-4MB split, and permanent-vs-transient failure classification.

## Two independent long-clip failure modes

### A. Format/size: a >4MB non-WAV clip cannot be split at all (the live incident)
- The managed proxy caps a request at ~4MB (serverless FUNCTION_PAYLOAD_TOO_LARGE / 413);
  `BatchTranscriptionPipeline.MaxTranscriptionUploadBytes = 4_000_000`.
- Splitting is PCM-WAV-only: `TranscribeBatchAsync` calls `WavSplitter.TrySplitByDuration`, and for a
  non-WAV clip over the byte budget it THROWS `InvalidOperationException` ("not a PCM WAV that can be
  split") - `BatchTranscriptionPipeline.cs:223-226`.
- Live incident: a 5m39s WebM/Opus clip was 5.4MB. Chunks uploaded 200 OK, but transcription failed
  because the Gateway could not split the WebM to get under 4MB. The Gateway has NO transcode today.

### B. Reliability: one transient chunk sinks the whole long clip (all-or-nothing)
- A long PCM WAV IS split (~60s chunks, max 90s, <4MB each), transcribed up to 4 in parallel, joined in
  order. But `TranscribeChunksInParallelAsync` uses `Task.WhenAll` and rethrows the FIRST chunk failure
  (`BatchTranscriptionPipeline.cs:265`), so ONE transient chunk fails the ENTIRE recording even though
  every other chunk transcribed fine.
- The proxy times out at 15s/attempt x 2 (~30s) then circuit-breaks (issue #1139 logs). Each chunk also
  gets one local retry (`PerChunkRetries = 1`). During a transient window, a long clip's many chunks
  make whole-clip failure disproportionately likely: `P(fail) = 1 - (1 - p)^N` in the chunk count N.
  For N=25 and p=5%/chunk, that is ~72% - a 25-minute recording is far more fragile than a 1-minute one.
- Proof: `src/CcDirector.Gateway.Tests/Issue1139LongClipReliabilityTests.cs` (3 tests, passing) pins:
  healthy multi-chunk clip transcribes fully; one 504 chunk fails the whole clip while all other chunks
  succeeded and were discarded; a breaker-open window fails with a classified-transient status.

## Classification gap
Failures surface to the caller as one typed `TranscriptionFailedException` (transient vs permanent by
HTTP status) wrapped to HTTP 502 with the raw provider string. It does NOT distinguish, per long clip,
which chunk failed / how many succeeded, nor does it classify PERMANENT format/size errors
(unsupported-format, audio-too-large-and-untranscodable) as non-retryable - so the client keeps
retrying a clip that can never succeed (the incident's retry loop).

## Plan

1. **Transcode (mode A - priority).** Before splitting, if the clip is not already a splittable PCM WAV,
   transcode it to PCM WAV (16 kHz mono 16-bit), then run the existing WAV splitter + parallel path. This
   makes WebM/Opus (and any decodable format) chunkable, fixing the 5.4MB incident.
   - **Decision to make (architecture/deployment - for the manager / Soren):**
     - (a) **Bundle ffmpeg** with the Gateway and shell out to it. Robust, handles every format; mirrors
       AgentEyes (same ecosystem already bundles `ffmpeg.exe`). Cost: ships an ~80MB binary with the
       Gateway / installer.
     - (b) **Managed decoder** - Concentus (pure-C# Opus, MIT) + a minimal WebM/Matroska demuxer. No
       binary dependency (ships in the assembly), but Opus/WebM-only and more code to own.
   - Recommendation: (a) bundled ffmpeg for breadth + proven precedent, unless the installer size is a
     hard constraint, in which case (b) covers the actual MediaRecorder case (WebM/Opus).

2. **Permanent-vs-transient classification (mode A + B).** Classify unsupported-format and
   audio-too-large-that-cannot-be-transcoded/split as PERMANENT (non-retryable) so Mobile's loop-stop can
   stop retrying a doomed clip; keep provider 5xx/429/timeout as transient. Surface a clear reason.

3. **Reliability (mode B - after transcode).** Options to discuss with the manager: keep the successful
   chunks and fail only the unrecoverable one (partial result + classified per-chunk failure), and/or
   raise per-chunk retries with backoff so a chunk can outlast a ~30s breaker window, instead of losing
   the whole recording to one chunk. Cross-checks the Mobile lane (durable retry) - coordinate the boundary.

## Build status (2026-07-09)

DONE (code + tests, Gateway builds 0/0):
- `FfmpegAudioTranscoder` (`IAudioTranscoder`) - bundled ffmpeg (option (a)), resolved lazily beside the
  Gateway exe or via `CCDIRECTOR_FFMPEG`; transcodes any decodable clip to 16 kHz mono 16-bit PCM WAV via
  temp files; a clip ffmpeg cannot decode throws the permanent exception.
- Pipeline integration (`BatchTranscriptionPipeline`): an over-budget NON-WAV clip is transcoded to PCM
  WAV, then run through the EXISTING duration-split + parallel + concat path. Short non-WAV clips keep
  their single-request fast path unchanged.
- Classification: `TranscriptionPermanentException` (Core; codes unsupported_format / audio_too_large /
  non_decodable; IsTransient=false), a `PermanentError` `TranscriptionOutcome`, and the
  `TranscriptionBatchEndpoint` maps it to HTTP 415 (non-retryable) - so the durable loop stops.
- Tests (`Issue1139LongClipReliabilityTests`, `FfmpegAudioTranscoderTests`): 7 passing, incl. two REAL
  ffmpeg integration tests that decode an actual WebM/Opus clip to a splittable WAV and prove undecodable
  bytes fail permanently.

DONE (packaging, issue #1186 - 2026-07-09):
- **ffmpeg now ships with the Gateway** as a side-car release asset, so the transcode works on every
  machine with no manual copy. It mirrors the proven mobile-app / Cockpit side-car-zip delivery (the
  single-file Gateway exe carries no loose content):
  - `scripts/ffmpeg-pin.json` - the single source of truth for the ffmpeg version: a pinned, reputable
    static Windows build (gyan.dev / GyanD/codexffmpeg 8.1.2 essentials, GPL) with its URL + SHA-256 +
    size. Native decoders cover the incident case (WebM/Opus, MP4/AAC, MP3 -> pcm_s16le/wav).
  - `release.yml` (`build-gateway-win`) downloads the pin, SHA-256 verifies it (fails the release on a
    mismatch - never ships an unverified binary), and repacks `ffmpeg.exe` + the GPL `LICENSE` + a
    source-offer note as `ffmpeg-win-x64.zip`; `create-release` publishes it and the completeness gate
    now requires it.
  - `FfmpegPackage` (setup engine) unpacks it BESIDE the Gateway exe (Gateway dir root, where
    `FfmpegAudioTranscoder.ResolveFfmpegPath` looks) on clean install (`GatewayTrayInstaller`) AND
    self-update (`GatewayUpdater` stages + SHA-verifies it; `GatewayApp/Program.cs --apply-update`
    applies it after a successful swap). Unlike the mobile/Cockpit extracts it does NOT wipe the
    directory (the exe + wwwroot live there) - it only overwrites the ffmpeg files, and asserts
    `ffmpeg.exe` landed (no silent degrade).
  - `redeploy-gateway.ps1` lays the same pinned `ffmpeg.exe` beside the exe (from a version-keyed local
    cache) so a dev redeploy has working transcode with no manual copy.
  - Tests: `FfmpegPackageTests` + `GatewayUpdaterFfmpegStagingTests` (9 passing) pin the extract/staging
    contract, incl. that the Gateway dir is not wiped and a zip missing ffmpeg.exe fails loud.

REMAINING:
- Live 5-min WebM deploy proof against the real package (over-budget non-WAV -> ffmpeg transcode ->
  split -> all chunks -> OK) - the shipping-gate evidence for #1139/#1186.
- Mobile lane: map the `PermanentError` outcome / HTTP 415 in `GatewayDictationEndpoint` to loop-stop
  (their lane; the outcome + code are produced here).
- Reliability mode B (all-or-nothing on one transient chunk) - separate follow-up to discuss.
