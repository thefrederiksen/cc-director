FAIL

# Multilingual final client audit

Commit inspected: `b77c79b912247d032a2487ab6c6bf2d7e72f9a83` in detached HEAD.

Scope: the desktop speech path, the browser speech contract and sinks, the fourth-language claim, raw spoken-content logging, and the browser side of Car Mode removal. The separate Gateway audit owns the Gateway language-to-model guard. I did not duplicate that work.

## CONFIRMED defects

### C1 - Critical - An attached desktop can speak with the machine voice after the account lookup fails

What it is: `AccountUtterance.ForAsync` returns null for an unsuccessful response, a partial response, malformed data, a timeout, or any other exception. `DesktopTtsPlayer.SpeakAsync` treats that null exactly like true standalone mode and calls the old bare-string overload with no voice. `TtsService` then resolves `TtsVoiceConfig` from the machine. The claim that the account voice lookup and account key always fail together is false: the key uses a separate route, a separate request, and an in-memory cache.

Where:

- `src/CcDirector.Core/Voice/AccountUtterance.cs:67-105`
- `src/CcDirector.Avalonia/Voice/DesktopTtsPlayer.cs:63-74`
- `src/CcDirector.Core/Configuration/OpenAiKeyResolver.cs:44,143-169`
- `src/CcDirector.Core/Voice/TtsService.cs:106-116`
- The test that asserts the opposite stops after checking for null and never invokes the player or synthesis service: `src/CcDirector.Core.Tests/Voice/AccountUtteranceTests.cs:113-139`.

How it fails for a real person: a French account speaks once while the Gateway is healthy, which caches the account key. On the next sentence, `/gateway/spoken-language` times out, returns an error, or returns one missing field. The key resolver returns the cached key without contacting the Gateway, the hosted speech provider remains reachable, and the French words are synthesized with the machine's English voice. The audio plays, so the language setting appears to have been ignored silently. The same result is possible without a cache when the vault route works and the spoken-language route does not.

How I verified it: I traced the complete null branch from the account request through `DesktopTtsPlayer` into `TtsService`, then traced the independent key request and its cache hit. The cache is read before any network request at `OpenAiKeyResolver.cs:143-145` and populated at line 169. The fallback voice is selected at `TtsService.cs:113`. No test covers the combined state; the existing failure tests only prove that `AccountUtterance` returns null.

### C2 - High - The browser can speak an utterance with no language without a cast

What it is: `SpokenUtterance` is a public structural interface. Its `decided: true` member is an ordinary public property, not an unforgeable brand. A caller can therefore write this directly and it type-checks:

```ts
speakLocally({ text: "words", language: "", decided: true });
```

The sink performs no run-time validation. It copies the empty language to the platform utterance and speaks it. Casts, `JSON.parse`, and spreading a valid utterance while replacing `language` are additional routes, but none is needed.

Where:

- `packages/client-core/src/speech/spokenUtterance.ts:26-35`
- `packages/client-core/src/speech/spokenUtterance.ts:49-61`
- `packages/client-core/src/speech/localSpeech.ts:74-83`
- `packages/client-core/src/speech/oneSpeechPath.test.ts:120-139`

How it fails for a real person: the browser receives correct French or Spanish words but submits an empty language to the platform. The platform uses its default voice, normally English. Audio still plays, so this is the original silent browser failure.

How I verified it: I planted the direct object literal above in a temporary product file. The strict browser type check passed. The focused one-speech-path guard also passed all three tests. I then planted a temporary run-time test; `speakLocally` returned true and the fake platform engine received the words with `lang` equal to the empty string. All sabotage files were removed.

### C3 - High - The one-browser-sink guard can miss a second speech engine

What it is: the guard is a line-based regular-expression scan. It sees the exact tokens `speechSynthesis` and `new SpeechSynthesisUtterance` in TypeScript source under three directories. It does not enforce the platform capability boundary. A computed property access can reach both platform objects without containing either token the guard searches for. The scan also ignores JavaScript and HTML files.

Where:

- `packages/client-core/src/speech/oneSpeechPath.test.ts:20-31`
- `packages/client-core/src/speech/oneSpeechPath.test.ts:38-51`
- `packages/client-core/src/speech/oneSpeechPath.test.ts:83-106`

How it fails for a real person: a second local speech path can be added outside `localSpeech.ts`, omit the account language, and pass the guard. Correctly translated words are then read in the device default voice.

How I verified it: first I planted the ordinary direct platform call and watched the guard go red with the file and both offending lines. I then replaced it with a temporary product file that joined `speech` plus `Synthesis` and `Speech` plus `Synthesis` plus `Utterance` for computed property access. The strict type check passed and the guard passed all three tests. The sabotage file was removed. A repository-wide source search found no such computed second route in the current client, so this finding is about the claimed enforcement, not a claim that the current TypeScript product already has two live local sinks.

### C4 - High - The browser sink is a voice decider, not a dumb sink

What it is: the browser utterance carries text and language but no voice. `localSpeech.ts` reads the device voice list, compares each voice language to the utterance language, selects the first match, and assigns it. That is voice resolution inside the sink, directly contrary to the reviewed design of text plus language plus voice decided before the sink.

Where:

- The utterance has no voice member: `packages/client-core/src/speech/spokenUtterance.ts:26-35`.
- The sink resolves the first matching voice: `packages/client-core/src/speech/localSpeech.ts:42-48,79-82,102-108`.

How it fails for a real person: the account's selected voice does not determine the local menu-refusal voice. Two devices with different installed voices, or a different browser voice-list order, can speak the same account notice in different voices. The settings screen can say one voice while this path uses another.

How I verified it: I followed the only live TypeScript call at `packages/client-core/src/voice/useVoiceMode.ts:654-656` into the sink. The sink receives no voice and calls `getVoices` itself. Existing tests explicitly assert this first-match behavior, so the implementation is deliberate but still does not match issue 1031.

### C5 - High - Adding German requires at least six files, not one

What it is: the test that walks `SpokenLanguages.All` is useful as a completeness alarm, but it does not make a fourth language a one-place change. Adding a row makes other code and existing tests fail until separate language-specific structures are edited.

The minimum product-correct and existing-test-correct change is six files:

1. `src/CcDirector.Gateway.Contracts/SpokenLanguages.cs:19-29` - add German and put it in `All`.
2. `src/CcDirector.Gateway/Speech/SpokenVoices.cs:75-85,97-105,118-126` - add German voices, a `For` branch, and a default voice branch.
3. `src/CcDirector.Gateway/Speech/SpokenPhrases.cs:249-268,287-295` - add German to the phrase constructor and dictionary, then add a German translation to all fourteen phrase declarations.
4. `packages/client-core/src/settings/LanguageTab.tsx:210` - replace the user-visible claim that dictation understands "all three languages".
5. `src/CcDirector.Gateway.Tests/SpokenLanguageEndpointTests.cs:85` - replace the exact three-code expectation.
6. `src/CcDirector.Core.Tests/Voice/AccountUtteranceTests.cs:96-105` - stop using `de` as the deliberately unknown code that must resolve to English.

How it fails for a real person: a one-row German change either throws because German has no registered voice, throws because fixed phrases have no German translation, leaves the settings page saying there are only three languages, or fails existing tests. The feature cannot ship from one edit.

How I verified it: I traced every consumer of `SpokenLanguages.All`, every explicit language branch in `SpokenVoices`, the fixed-arity phrase constructor, the browser copy, and the two hard-coded tests. I temporarily added only German to `SpokenLanguages.All`; source inspection shows the first German voice lookup reaches the exception at `SpokenVoices.cs:103-106`. The focused managed-code test command did not finish in its four-minute timeout, so I do not claim a run result from it. The source dependency is direct and unconditional.

### C6 - Low - Car Mode left a dead browser style block in the shipped settings bundle

What it is: the live Car Mode route and tab are gone, but `settings.css` still contains the complete Car Mode phrase-tester, result, and log style block. No product component uses any of these selectors, while the stylesheet is imported by every live shared settings tab.

Where:

- Dead selectors: `packages/client-core/src/settings/settings.css:294-379`
- Live imports include `packages/client-core/src/settings/SettingsTabs.tsx:7` and `packages/client-core/src/settings/LanguageTab.tsx:10`.
- The retired mobile route correctly redirects to Assistant at `apps/mobile/src/main.tsx:148-155`.

How it fails for a real person: every settings visitor downloads and parses dead Car Mode styling. The impact is small, but the owner asked for deletion with no browser orphans, and this is an orphan in the shipped client bundle.

How I verified it: a repository-wide selector search found definitions only and no TypeScript or component use. I separately verified that `/car` redirects, the shared tab list contains Assistant and not Car Mode, and no Car Mode hook or page remains in either live browser shell.

## SUSPICIONS

### S1 - Embedded legacy HTML contains two more no-language browser speech implementations, but I found no live route

`src/CcDirector.ControlApi/Web/manager.html:1151-1160` and `src/CcDirector.ControlApi/Web/session-view.html:3231-3237` both construct platform utterances without a language. Both files remain embedded by `src/CcDirector.ControlApi/CcDirector.ControlApi.csproj:40-42`. However, repository search found no call to `EmbeddedResources.Load`, no static-file provider, and no mapped route that serves either resource in the pinned commit. I therefore did not count them as live browser sinks. They remain outside the browser guard and deserve deletion or an explicit reachability test if these legacy resources are intentionally retained.

## Claims checked

- Browser utterance unconstructable without language: FAIL. A plain structural literal is enough.
- Current live TypeScript local-speech sink count: one call through `localSpeech.ts`; PASS for current exact source, FAIL for guard enforcement.
- Every browser sink is dumb: FAIL. The local sink selects a device voice.
- Desktop uses the account utterance voice and passes no language-derived model on the healthy account path: PASS. `TtsService.GenerateAsync(SpokenUtterance)` passes `utterance.Voice` and a null model override at `TtsService.cs:141-145`.
- Desktop attached failure and standalone are safely distinguishable: FAIL. Both become the same null branch, while a cached or separately reachable account key still permits speech.
- Desktop language selects a model: no such path found in the desktop code. Both desktop calls pass a null model override; model resolution remains separate.
- Fourth language is one place: FAIL. Minimum six files.
- Raw spoken content reaches a desktop log or browser console: no direct instance found. Desktop speech logs endpoint, model, voice, length, status, and errors; browser speech code does not log the utterance text.
- Car Mode browser surface is gone while Assistant, voice mode, and dictation remain: PASS for live routes and components; FAIL for the dead style orphan in C6.
- Gateway language-to-model guard and Gateway phrase-file encoding: not checked, by the explicit split with the separate Gateway auditor.

## Verification record

- `npm run typecheck --workspace @devthrottle/client-core`: passed after sabotage removal.
- Focused browser tests: 22 passed across `localSpeech.test.ts`, `oneSpeechPath.test.ts`, and `voiceMenuGuard.test.tsx` after sabotage removal.
- Direct platform-call sabotage: guard failed as expected.
- Structural no-language sabotage: strict type check passed, guard passed, and a temporary run-time test spoke with an empty language.
- Computed-property second-sink sabotage: strict type check passed and guard passed.
- Focused managed-code test attempts produced no output and timed out at four minutes and ninety seconds. They are not used as evidence for this verdict.
- All sabotage files and the temporary German edit were removed before this report was written.
