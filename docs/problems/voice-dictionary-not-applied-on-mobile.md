# Problem: Voice dictionary edits do not reach the mobile/phone recording path until the Gateway restarts

Reported: 2026-05-25
Reporter: the maintainer (via assistant session in a private workspace)
Component: CcDirector.Gateway recording/transcription pipeline + dictation dictionary
Severity: medium (functional - corrections never take effect on the path the user actually uses)

> HISTORICAL RECORD, 2026-05-25. Kept for the history; do not work from it. The class it
> describes, `OpenAiRecordingTranscriber`, no longer exists, and the parts below that talk
> about a speech-to-text bias prompt describe an approach the owner has since REJECTED
> (issue 2481, 2026-08-07). Vocabulary and steering hints are never sent to the transcriber:
> it gets audio only, and the listed words are substituted afterwards by a separate pass over
> the finished transcript. `DictionaryLoader.BuildSttPrompt` and its `_sttPrompt` field are
> deleted. Ignore suggested fix A's second bullet, and the "the correct lever is the STT
> `vocabulary` bias" line - there is no such lever and there is not going to be one.

## Symptom (what the user experienced)

The mobile voice unit keeps mishearing the company name "acmeflow" and the user
has told it about the mistake more than once, yet it keeps happening. In a single
assistant session the SAME word "acmeflow" came through three different wrong ways:

- "acmeflow" -> "acme seeds"  (heard as: "what do you know about acme seeds")
- "acmeflow" -> "my repo"    (heard as: "tell me about my repo / the company")
- "acmeflow" -> "Akmeflow"      (a known variant already in the dictionary)

The user's reasonable suspicion: "is the system actually using the dictionary?"

## Root cause (confirmed by reading the code)

The phone/mobile recording transcriber loads the dictionary ONCE, at Gateway
startup, with the file watcher DISABLED. Dictionary edits therefore never reach
the live recording path until the Gateway process is restarted.

Evidence:

1. `src/CcDirector.Core/Recording/OpenAiRecordingTranscriber.cs:44`
   ```csharp
   _dictionary = new DictionaryLoader(dictionaryPath ?? "", watch: false);
   ```
   `watch: false` => no FileSystemWatcher, so the loader reads the YAML once in
   its constructor and never reloads it.

2. `OpenAiRecordingTranscriber.cs:45`
   ```csharp
   _sttPrompt = DictionaryLoader.BuildSttPrompt(_dictionary.Current);
   ```
   The speech-to-text bias prompt (the "vocabulary" terms) is computed ONCE at
   construction and cached in a readonly field. A later vocabulary edit cannot
   change it for this instance.

   > CORRECTION, 2026-08-07 (owner ruling, issue 2481): there is no speech-to-text bias
   > prompt any more. `BuildSttPrompt` and this `_sttPrompt` field are DELETED, and the
   > class itself no longer exists. Vocabulary is never sent to the transcriber.

3. `OpenAiRecordingTranscriber.cs:72`
   ```csharp
   => _cleanup.CleanAsync(rawTranscript, _dictionary.Current, "default", ct);
   ```
   The cleanup pass reads `_dictionary.Current`, which (with `watch: false`) is a
   frozen snapshot from construction time.

4. `src/CcDirector.Gateway/Api/RecordingEndpoints.cs:45` and `:397`
   `BuildService()` is called once inside `Map()` (i.e. at Gateway startup), and
   it constructs exactly one `OpenAiRecordingTranscriber`. The transcriber and
   its frozen dictionary snapshot live for the whole process lifetime - it is a
   startup singleton, not per-request.

So the recording transcriber's vocabulary bias AND cleanup glossary are both
fixed at the moment the Gateway started.

> CORRECTION, 2026-08-07 (owner ruling, issue 2481): there is no "vocabulary bias" half of
> this any more - only the cleanup glossary. Nothing is sent to the transcriber but audio.

## Why this contradicts the docs (the trap)

Two places promise hot-reload that does not happen on this path:

- `RecordingEndpoints.cs:355` (the agent-info help text) states:
  "Changes apply on the next recording (no restart)."
- `docs/plans/voice-dictionary-page.md:29-31` claims hot reload works because
  "the recording transcriber is constructed per request, so edits take effect on
  the next recording / next dictation with no restart."

The second claim is factually wrong about the current code: the recording
transcriber is NOT constructed per request; it is built once at startup with
`watch: false`. Anyone (a user, or an agent like me editing the YAML or calling
`POST /ingest/dictionary/terms`) sees the file change on disk and via
`GET /ingest/dictionary`, and reasonably believes the next recording will be
corrected - but the running transcriber never re-reads the file.

This fully explains "I told it and it keeps doing it": the correction was real
on disk but never loaded by the live recording path.

## Contributing factor (separate, smaller)

Before this session the dictionary's `common_mistranscriptions` for `acmeflow`
held only: Akmeflow, Acmefloe, Acmeflo, AcmeFlow. Neither "Acme Seeds" nor "my repo" was
listed, so even a freshly-loaded cleanup pass had to rely on the LLM generalizing
"near misses." "my repo" is not a plausible phonetic near-miss the model would
confidently rewrite, so it would pass through uncorrected.

Note on "my repo": it should NOT be added as a literal mistranscription -> acmeflow
mapping. "my repo" is a legitimate everyday phrase for the user (he works in many
repos); a blanket mapping would wrongly rewrite real uses. The correct lever is
the STT `vocabulary` bias (already contains "acmeflow") plus restarting/reloading
so it actually applies - not a risky find-replace rule.

> CORRECTION, 2026-08-07 (owner ruling, issue 2481): the lever named here DOES NOT EXIST and
> is not coming back. There is no speech-to-text vocabulary bias. The reasoning about "my
> repo" still stands - do not add it as a literal mistranscription mapping - but the remedy
> is the cleanup pass over the finished transcript, not priming the transcriber.

## What was changed on disk during the session (does NOT fix the bug by itself)

`%LOCALAPPDATA%\cc-director\dictation\dictionary.yaml` - added to the `acmeflow`
mistranscription list: `Acme Seeds`, `acme seeds`, `Acme Seeds`. This is correct and
helps the desktop dictation path (which uses `watch: true`), but it will NOT take
effect on the mobile recording path until the Gateway restarts, because of the
root cause above.

## Reproduction

1. Start the Gateway.
2. Edit `dictionary.yaml` (or use the Dictionary editor page / `POST
   /ingest/dictionary/terms`) to add a new mistranscription, e.g. map "acmeflow"
   <- "my repo".
3. `GET /ingest/dictionary` confirms the change is on disk.
4. Upload a phone recording that says the affected word.
5. Observed: the transcript still shows the OLD behavior (uncorrected). The edit
   had no effect.
6. Restart the Gateway, upload the same recording: now the edit applies.

## Suggested fix (pick one; option A preferred)

> CORRECTION, 2026-08-07 (owner ruling, issue 2481): DO NOT IMPLEMENT the `_sttPrompt`
> bullet in option A. Recomputing a speech-to-text prompt per transcription is exactly the
> rejected approach - vocabulary and steering hints are never sent to the transcriber,
> because priming it changes wording and sentence structure and corrupts the record of what
> was said. `BuildSttPrompt` is deleted. The live-reload point itself is fine; it applies to
> the cleanup glossary only.

A. Make the recording transcriber reload the dictionary live, the same way
   desktop dictation does:
   - Construct its `DictionaryLoader` with `watch: true`.
   - Stop caching `_sttPrompt` in a readonly field; recompute it per
     transcription from `_dictionary.Current` (it is cheap), OR subscribe to
     `DictionaryLoader.OnReloaded` and refresh `_sttPrompt` there.
   - Keep using `_dictionary.Current` for cleanup (already a live read once the
     watcher is on).

B. Reconstruct the transcriber per recording (what the plan doc assumed). Simple
   but pays YAML read + prompt build per recording; acceptable given recording
   frequency, but A is cleaner.

C. Inject the shared `DictionaryLoader` singleton (the same instance the editor
   endpoints use) into the transcriber instead of giving it its own
   `watch: false` loader, so there is one watched loader for the whole Gateway.

After the fix, correct the two stale docs:
- `RecordingEndpoints.cs:355` agent-info text (keep it true).
- `docs/plans/voice-dictionary-page.md:29-31` ("constructed per request" claim).

## Acceptance

> CORRECTION, 2026-08-07 (owner ruling, issue 2481): the first criterion below names the
> rejected behaviour as REQUIRED, which would make a tester sign off on the thing that is
> banned. There is no "vocabulary bias" to confirm and there must never be one. The criterion
> that applies is that the new CORRECTION is applied to the finished transcript; the
> transcriber is given audio only.

- Edit the dictionary, do NOT restart the Gateway, upload a phone recording with
  the affected term, and confirm the new correction/vocabulary bias is applied.
- A regression test that constructs the recording transcriber, changes the
  dictionary file, and asserts the next transcribe/cleanup sees the new terms.
