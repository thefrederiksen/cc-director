# Multilingual mission inspection - phases 1 and 2

## Verdict

FAIL. The pinned implementation does not meet the phase 2 requirement that every product utterance use the account language. Five defects are confirmed. The worst is an active Car Mode path that still speaks several hard-coded English sentences, including a normal empty-request response and the offline recovery announcements. These paths sit outside the new speech registry, and both the focused server tests and the entire client-core test suite remain green.

Inspection target: detached commit `d6f32c23`, compared as `origin/main...d6f32c23` after fetching `origin`. Phase 3 was not reviewed. No implementation fixes were made.

## Confirmed defects

### C1 - High - Active Car Mode utterances remain hard-coded English

What: The multilingual work migrated Gateway speech but did not migrate speech authored in the shared browser client. Car Mode still sends a normal empty-request nudge in English, speaks three offline lifecycle messages in English, and can speak English Gateway error messages locally.

Where:

- `packages/client-core/src/carmode/useCarMode.ts:897` sends the English empty-request sentence directly to `speakAndPlay`.
- `packages/client-core/src/carmode/turnRetry.ts:53-65` defines the English holding message, recovery prefix, and connection-down message.
- `packages/client-core/src/carmode/useCarMode.ts:674`, `:765`, and `:1184` speak those constants.
- `packages/client-core/src/carmode/useCarMode.ts:480-483` speaks the raw error message passed to `announceError`.
- `packages/client-core/src/carmode/carModeApi.ts:48-53` forwards caller text unchanged to `/wingman/tts`; `src/CcDirector.Gateway/Api/GatewayWingmanVoiceEndpoint.cs:413-432` limits that text and chooses the voice/model but does not translate it.
- `apps/mobile/src/pages/CarMode.tsx:3` imports this hook and `:29` enables the real fleet brain, so this is the active mobile Car Mode path, not a dormant harness.

Practical failure: An account set to French or Spanish hears English after an empty transcription, while waiting for connectivity, when connectivity returns, when the end-phrase watcher loses the Gateway, and for multiple failure announcements. The recovery case is especially clear: the English `Back online` prefix is concatenated to a model reply that is otherwise in the target language, producing the mixed-language utterance the mission explicitly forbids.

How verified: I traced the text from each literal or constant through the active `useCarMode` callbacks to either browser speech synthesis or the raw-text Gateway TTS endpoint. I also searched every production `SpeechSynthesisUtterance`, `speakLocal`, and `speakAndPlay` call outside generated dependencies. The multilingual diff changes no file under `packages/client-core` or `apps/mobile`, so none of these paths can consume `SpokenLanguage` or `SpokenPhrases`. The full client-core suite passed 822 tests despite these paths.

### C2 - Medium - French and Spanish menu readings preserve and speak English option labels

What: The menu extraction prompt deliberately preserves each option `key` exactly as displayed on the English terminal, but the speech builder then reads that key aloud. Only the surrounding sentence, question, and note are translated.

Where:

- `src/CcDirector.Gateway/Wingman/WingmanTranslator.cs:534-538` says the question and note use the target language while `key` stays exactly as it appears on screen.
- `src/CcDirector.Gateway/Wingman/WingmanTranslator.cs:620-625` removes only the numeric marker from `o.Key` and inserts the remaining label into a target-language sentence.
- `src/CcDirector.Gateway.Tests/Speech/SpokenPhraseTests.cs:317-333` supplies already-translated keys (`Oui` and `Non`) directly to `BuildMenuSpoken`, which is not the object shape produced by the real extraction prompt.
- `src/CcDirector.Gateway/Api/GatewayWingmanVoiceEndpoint.cs:776-791` maps the production endpoint and returns the resulting `spoken` field.

Practical failure: A French terminal menu such as `1. Yes, 2. No` is spoken as a French frame around the English words `Yes` and `No`. Spanish has the same failure. The API is production-mapped even though no current source client calls `getWingmanMenu`.

How verified: This is a deterministic data-flow check, not a model-quality inference. The producer contract requires an unchanged English key, and the consumer reads that same key. The passing test substitutes a translated key before calling the consumer and therefore cannot reproduce the real path.

### C3 - Medium - A spoken browser message is written raw to the console

What: `announceError` writes its `message` argument verbatim to `console.log` and then sends that exact same value to local speech synthesis.

Where:

- `packages/client-core/src/carmode/useCarMode.ts:480-483` logs and speaks the same `message` value.
- `src/CcDirector.Gateway.Tests/Speech/SpokenPhraseTests.cs:217-239` claims no spoken text can reach a log, but its source helper at `:471-482` scans only C# files in `src/CcDirector.Gateway`.

Practical failure: Spoken output, including target-language accents once the caller is corrected, is emitted raw to a browser console. This violates the explicit accent ruling and defeats the ASCII-safe output-channel boundary the logging guard claims to enforce.

How verified: I followed one value inside a single callback: `message` is interpolated at line 482 and passed unchanged to `speakLocal` at line 483. The focused server tests and all client-core tests pass because no test scans this TypeScript logging sink.

### C4 - High - The structural regression guards do not enforce their stated guarantees

What: The prompt completeness guard and language-to-model guard both have large, documented blind spots. They can stay green while the original failure returns.

Where:

- `src/CcDirector.Gateway/Speech/SpokenPaths.cs:16-25` claims a fifth spoken path cannot ship without registration and language propagation.
- `src/CcDirector.Gateway.Tests/Speech/SpokenLanguageContractTests.cs:483-493` limits the source inventory to Gateway C# files.
- `src/CcDirector.Gateway.Tests/Speech/SpokenLanguageContractTests.cs:499-512` recognizes only static string methods named `Build*Prompt`.
- `src/CcDirector.Gateway.Tests/Speech/SpokenLanguageContractTests.cs:407-432` analyzes one compiled method at a time and counts language access only when an instruction operand is declared directly on `SpokenLanguage` or `SpokenLanguages`.
- `src/CcDirector.Gateway/Api/GatewayWingmanVoiceEndpoint.cs:413` calls `TenantSettingsResolver.SpokenLanguage` and `:418` calls `TenantSettingsResolver.TtsModel` in the same endpoint lambda, yet the guard passes. Its type-name heuristic cannot see the language access through the resolver.

Practical failure: A new spoken path can be an instance method, have another name or return type, live outside the Gateway project, or simply be browser-authored, and the completeness test will not see it. Language-dependent model selection can also be split across helpers or hidden behind a resolver and escape the method-local check. That permits the issue 547 failure mode - switching French or Spanish to an engine that returns silence or times out at real narration length - while the advertised acceptance tests remain green.

How verified: I compared each prose guarantee with the exact source-selection regex and compiled-instruction predicate, then checked a concrete current counterexample. The TTS endpoint calls both resolver methods in one lambda, but the focused suite passed all 130 selected tests. C1 supplies a second concrete counterexample to the broader completeness claim: real product speech exists outside every scanned directory and naming convention.

### C5 - Low - Newly added mission documentation violates the mission's ASCII boundary

What: The rulings require documentation to remain ASCII, but every new mission/review document contains non-ASCII characters.

Where and measured line counts:

- `docs/MISSION-multilingual.md:27` and `:95` (2 lines).
- `docs/MISSION-multilingual-RULINGS.md:10`, `:18`, `:19`, and `:56` (4 lines).
- `docs/MISSION-multilingual-INSPECTION.md:63` (1 line).
- `docs/reviews/spoken-translation-review-pass-1.md:24` onward (123 lines).
- `docs/reviews/spoken-translation-review-pass-2.md:27` onward (59 lines).

Practical failure: The patch contradicts its own durable boundary that only actual spoken content and test fixtures may contain accents. Documentation is one of the named ASCII-only output channels, so the encoding protection is incomplete even though production spoken resources are correctly encoded.

How verified: I scanned the five added Markdown files with the byte-safe regular expression `[^\x00-\x7F]` and counted matching lines. This review itself is ASCII-only.

## Suspicions and reachability limits

### S1 - Menu speech bypasses the deterministic speech sanitizer

`src/CcDirector.Gateway/Wingman/WingmanTranslator.cs:619-631` concatenates model-produced `Question` and `Note` values and returns them without `SpeechContract.Finish`. The menu prompt is a JSON contract and does not append the full plain-spoken-prose rule. A model-emitted formatting marker could therefore be voiced. I did not count this as confirmed because the current source has no caller of `getWingmanMenu`, and no captured model response demonstrated the bad output.

### S2 - Local browser speech has no target-language phonemization setting

`packages/client-core/src/carmode/useCarMode.ts:465-470` and `packages/client-core/src/voice/useVoiceMode.ts:647-653` construct `SpeechSynthesisUtterance` objects without setting `lang` or selecting a target-language voice. The translated menu-block refusal can therefore be pronounced by the device default voice. I did not count this as confirmed because the actual voice choice is browser/device dependent and was not exercised on the production phone.

## Spoken-path inventory

| Spoken path | Production result |
|---|---|
| Turn narration, `WingmanTranslator.TranslateAsync` | Checked: tenant language reaches `BuildPrompt`; `SpeechContract.Finish` is applied before return. |
| Direct reply, `AskDirectAsync` | Checked: tenant language reaches `BuildDirectPrompt`; `Finish` is applied. |
| In-product help, `AskAboutDevThrottleAsync` | Checked: tenant language reaches `BuildDevThrottlePrompt`; `Finish` is applied. |
| Car Mode model reply, car surface | Checked: tenant language reaches `BuildSystemPrompt`; the single `RunTurnAsync` exit applies `Finish`. |
| Cockpit Assistant model reply, desk surface | Checked: shares the Car Mode brain and the same language/sanitizer exit. |
| Model-extracted menu question/note | Defective: question/note are requested in the target language, but the unchanged English option key is also spoken; sanitizer reach is uncertain. |
| Car Mode delete done/cancel/give-up and help | Checked: all route through `SpokenPhrases` using the tenant language. The configured end phrase is intentionally preserved literally. |
| Car Mode spoken confirmation recognition | Checked: English, French, and Spanish affirmative/negative lists are matched together; diacritics are normalized for input only; negatives win. |
| Voice-mode blocked-menu/unreadable notices | Checked on the server: target-language phrase is returned. Local browser phonemization remains S2. |
| Prompt-send and waiting-screen menu refusal | Checked: target language is resolved on the server; the on-screen message remains English by design. |
| Narration suffix and length-cut notice | Checked: both use the tenant language. |
| Menu sentence frame | Defective as C2. |
| Car Mode empty-request nudge | Defective as C1. |
| Car Mode holding, reconnect, and connection-down announcements | Defective as C1. |
| Car Mode error announcement | Defective as C1 and logged raw as C3. |

The embedded Control API HTML files also contain old browser speech calls, but their `/chat` and `/voice/command` backends are not mapped in the current Control API source. I treated those as unreachable legacy code rather than confirmed product speech.

## Claims checked

- Pinned history: confirmed HEAD is `d6f32c23`; the reviewed range contains commits `d1c0ca10`, `581b5ba8`, `84f6024d`, and `d6f32c23`.
- Issue 547 failure: confirmed from `thefrederiksen/devthrottle_internal#547`. The prior engine returned effective silence for French at 155 characters and exceeded the deadline for Spanish at 208 characters; it also missed three of four model generators.
- Current model choice: checked in the phase 1/2 diff. Spoken language selects content/voice, not a TTS model. No current language-dependent model branch was found. C4 means the regression guard is not proof that this remains true.
- Shared model output contract: checked for all registered Gateway model paths. All append the language contract, and their ordinary spoken outputs reach `SpeechContract.Finish`.
- Fixed Gateway phrases: checked against `SpokenPhrases.All` and call sites. The migrated Gateway constants use target-language lookup.
- Menu answer fallback: not verified as a live behavior. `WingmanMenuLogic.MatchOption` and `WingmanTranslator.MapChoiceAsync` have no production callers; current voice/prompt flows refuse menu answering. This is safe today, but the claimed French/Spanish model fallback is not exercised by the product.
- Display-only English: checked for the Car Mode cheat sheet, waiting-screen display message, and the `nothing yet`/retrying notes from `/wingman/explain`. Current clients display these values and do not send them to speech.
- Encoding: checked. `SpokenPhrases.cs` starts with the UTF-8 byte order mark, and the focused tests validate accented compiled values with ASCII `\u` expectations.
- Translation quality: not independently verified. The repository contains machine-translation and second-model review artifacts, but no native human review by explicit mission decision. This remains the mission's accepted risk.
- Live voice quality and latency: not rerun. This inspection reviewed phases 1 and 2 only and had no live synthesis requirement. The no-model-switch code path was checked; the recorded performance numbers were not independently reproduced.

## Verification executed

- `git fetch origin` and exact `git diff origin/main...d6f32c23` inspection.
- Focused server tests: 130 passed, 0 failed (`SpokenLanguageContractTests`, `SpokenPhraseTests`, and `CarModeBrainTests`).
- Full `@devthrottle/client-core` suite: 822 passed across 77 files.
- Strict workspace type checks ran as part of the Gateway test build and passed for client-core, Cockpit, and mobile.
- `git diff --check origin/main...d6f32c23`: clean.

The green suites do not change the verdict. C1, C3, and C4 explain precisely why the checks do not observe the failing product paths.
