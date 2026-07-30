FAIL

# Multilingual Gateway audit

Commit inspected: `714cfb77c49b545fc5204b698624015e9bdad7cf`

Confirmed defects: 6

Worst finding: `SpokenUtterance` can be constructed with a null language while retaining usable text, voice, and length. The two live hosted sinks never read its `Language`, so the package is still speakable. The type therefore does not provide the construction invariant claimed by issue 1031.

## Scope

This pass inspected the Gateway half only, as directed. The desktop sink and the browser contract mirror were not judged as missing. The Car Mode deletion was inspected on the Gateway side, with the live Cockpit Assistant call path used only to verify whether the shared Gateway brain still tells the truth.

The checkout was detached at the requested commit. I did not check out, pull, merge, fix, commit, push, or open anything.

## Confirmed defects

### C1 - Critical - A speakable utterance can exist with no language

What it is:

`SpokenUtterance` does not enforce its invariant at its private constructor. The constructor assigns `language` directly at `src/CcDirector.Gateway/Speech/SpokenUtterance.cs:34`. Reflection or the parameterized `Activator.CreateInstance` overload can call that constructor with null. The resulting object still has valid `Text`, `Voice`, and `Length`.

The public factory is also weaker than the claim. It rejects a null language reference at `src/CcDirector.Gateway/Speech/SpokenUtterance.cs:74`, but `SpokenLanguage` is a public positional record with no validation at `src/CcDirector.Gateway/Speech/SpokenLanguage.cs:25`. A caller can pass `new SpokenLanguage("", "", "")`, a record made with null values, or a `with` clone whose code is blank. The factory accepts it.

The test at `src/CcDirector.Gateway.Tests/Speech/SpokenUtteranceTests.cs:46` checks only public constructors and whether public factories have a non-optional `SpokenLanguage` parameter. It never invokes the private constructor, tests the validity of the language object, or tries an activation path.

How it fails for a real person:

Both hosted synthesis sites consume only `Text`, `Voice`, and `Length` at `src/CcDirector.Gateway/Api/GatewayWingmanVoiceEndpoint.cs:440` and `src/CcDirector.Gateway/Wingman/WingmanVoiceService.cs:1015`. Neither reads `Language`. A null-language object made through the constructor can therefore reach synthesis as normal audio. The central claim that a missing language becomes impossible is false.

How I verified it:

I compiled a temporary reflection probe against the built Gateway assembly. It produced these results:

```text
PRIVATE_CONSTRUCTOR=language:<null>;text:hello;voice:af_bella;length:5
ACTIVATOR_PRIVATE_CONSTRUCTOR=language:<null>;text:hello;voice:af_bella;length:5
PUBLIC_FACTORY_BLANK_LANGUAGE=language:;text:hello;voice:af_bella;length:5
MEMBERWISE_CLONE=language:<null>;text:hello;voice:af_bella;length:5
```

`System.Text.Json` refused deserialization with `NotSupportedException`. An uninitialized object had null fields but threw when `Length` was read, so that route did not yield a speakable value. The type is sealed, which closes subclassing, and it is not a record, which closes a `with` expression on `SpokenUtterance` itself. Those closed routes do not repair the successful constructor and activation routes above.

### C2 - Critical - The Gateway speakers are not dumb sinks

What it is:

I enumerated audio production from provider calls rather than from the speech registry. There are three Gateway sites:

1. `WingmanVoiceService.TtsAsync` takes a bare `string text` at `src/CcDirector.Gateway/Wingman/WingmanVoiceService.cs:970`. It calls the resolver itself at line 985, reads the model setting at line 991, and posts audio at line 1015.
2. `/wingman/tts` accepts a mutable request containing bare `Text`, optional `Voice`, and optional `Model` at `src/CcDirector.Gateway/Api/GatewayWingmanVoiceEndpoint.cs:1193`. The route calls the resolver at line 417, resolves or accepts a model at line 426, and posts audio at line 440.
3. `CarModeWarmup.WarmTtsAsync` receives a separate `(BaseUrl, Voice, Model, Key)` tuple, synthesizes the bare string `"ok"`, and discards the audio at `src/CcDirector.Gateway/CarMode/CarModeWarmup.cs:95`. The tuple is assembled from separate voice and model settings at `src/CcDirector.Gateway/GatewayHost.cs:2466`.

None of these speaker entry points takes `SpokenUtterance`. Two decide inside the sink, and the warm-up path bypasses the type entirely. A bare string still compiles at every Gateway speaker boundary.

There is also a live voice-language mismatch route. `TenantSettingsResolver.Utterance` accepts `voiceOverride` without calling `SpokenVoices.Speaks` at `src/CcDirector.Gateway/Settings/TenantSettingsResolver.cs:113`. `/wingman/tts` forwards the caller's `Voice` into that override at `src/CcDirector.Gateway/Api/GatewayWingmanVoiceEndpoint.cs:417`. A French tenant can therefore submit `af_bella`, and the resolver packages a French language with an English voice. The public factory performs no voice-language validation either.

How it fails for a real person:

A stale, incorrect, or hand-written caller can request French text with an English voice, or can send a different model through `WingmanTtsRequest.Model`. The sink obeys. The person hears the wrong pronunciation or can be sent back to an unsuitable engine. More broadly, a future speaker can continue to accept a string and make local decisions because that shape still compiles.

How I verified it:

I searched all Gateway source for provider-compatible `/audio/speech` posts, `TtsSynthesis.PostAsync`, audio response payloads, and direct text-to-speech sends. The three sites above are the complete Gateway audio-production inventory at this commit. I then followed each value from entry signature to provider request. The mismatch follows directly through `req.Voice` -> `voiceOverride` -> `SpokenUtterance.For` -> `spoken.Voice` with no membership check.

### C3 - Critical - The new language-to-model guard is only one call deep

What it is:

The new guard correctly widens the language match through the resolver, but its call traversal is exactly one hop. At `src/CcDirector.Gateway.Tests/Speech/SpokenLanguageContractTests.cs:527`, it marks a language-reading method only when that same method directly calls a method already classified as a model selector. It does not compute transitive reachability.

How it fails for a real person:

A harmless-looking refactor can put model selection two helpers away from a language check. Both guards remain green while French or Spanish selects a different engine. That is the exact reverted failure: real narration can become silence or exceed the deadline even though the architecture suite passes.

How I verified it:

I reproduced the two guard algorithms exactly over the compiled Gateway assembly using the same compiled metadata model as the tests. I then planted methods in a temporary in-memory copy of the module.

The documented combined sabotage read the language through `TenantSettingsResolver.SpokenLanguage` and called a model selector one helper away. The old guard stayed green and the new guard turned red:

```text
COMBINED_ONE_HOP_OLD=GREEN
COMBINED_ONE_HOP_NEW=RED
```

I then inserted one neutral middle helper between the language-reading method and the same selector:

```text
TWO_HOP_OLD=GREEN
TWO_HOP_NEW=GREEN
```

The clean baseline was green under both algorithms. This verifies the builder's claimed one-hop sabotage and also verifies a bypass the new guard does not cover.

### C4 - High - The no-raw-spoken-log guard misses elementary logging forms

What it is:

The guard at `src/CcDirector.Gateway.Tests/Speech/SpokenPhraseTests.cs:217` examines one source line at a time. It only inspects interpolation expressions at line 228. Its logging-sink recognizer at line 465 omits `Console.WriteLine`, and its spoken-name recognizer at line 450 relies on a short variable-name list.

How it fails for a real person:

Accented spoken text can be written raw to a log or console while the guard remains green. That reopens the silent encoding and privacy boundary the accent ruling requires.

How I verified it:

I ran the guard's exact recognition rules against three planted lines. All three stayed green:

```text
FileLog.Write("spoken=" + spoken); => GREEN
Console.WriteLine($"{spoken}"); => GREEN
FileLog.Write($"{payload}"); => GREEN
```

The first uses concatenation, the second uses a console sink named in the ruling, and the third merely aliases the variable. I separately searched current Gateway logging calls and found no confirmed direct logging of a spoken value at this commit. This finding is about a guard that does not enforce its claim, not an assertion that raw spoken text is currently logged.

### C5 - High - Adding German is at least a three-production-file change

What it is:

The one-place acceptance claim is contradicted by the production data model:

1. `src/CcDirector.Gateway/Speech/SpokenLanguages.cs:19` needs a German record and an entry in `All`.
2. `src/CcDirector.Gateway/Speech/SpokenVoices.cs:41` needs a German voice array, a branch in `For`, and a default branch in `Default` through line 126.
3. `src/CcDirector.Gateway/Speech/SpokenPhrases.cs:54` needs a German translation at every phrase declaration, plus a German constructor parameter and dictionary entry at lines 287-295.

`SettingsEndpoints` derives its response from `SpokenLanguages.All`, so its executable path does not need another branch. Its route comments still name only `en|fr|es`, so an honest documentation update would touch a fourth production file, but the minimum runtime implementation count is three.

How it fails for a real person:

German cannot be added as one registry row. If the language row lands without the other coordinated edits, settings can offer a language whose voice lookup throws or whose fixed speech has no translation. The test suite can catch that after the fact, but the owner asked for one edit by construction.

How I verified it:

I traced `SpokenLanguages.All` through settings serialization, voice resolution, default selection, and every fixed phrase lookup. The test claiming to demonstrate one-place addition at `src/CcDirector.Gateway.Tests/Speech/SpokenUtteranceTests.cs:194` only iterates the languages that already exist. Adding German makes that test fail until the two other production files are edited. It demonstrates completeness pressure, not a one-place change.

### C6 - High - Car Mode removal left broken Car Mode instructions in the live Assistant

What it is:

The removed `/carmode` routes are gone, but the shared brain still receives the old Car Mode prompt. `BuildSystemPrompt` returns `SystemPrompt + DeskAddendum` at `src/CcDirector.Gateway/CarMode/CarModeBrain.cs:609`. `SystemPrompt` starts by saying this is Car Mode and that the owner is driving at line 622. The addendum says this is not the car, but also says everything else is unchanged at line 712. The model receives conflicting surface instructions on every Assistant turn.

The help path is concretely wrong. `get_help` returns `CarModeHelp.SpokenScript` with `CarModeEndPhrase` at `src/CcDirector.Gateway/CarMode/CarModeBrain.cs:262`. The script at `src/CcDirector.Gateway/Speech/SpokenPhrases.cs:93` tells the person to say the configured end phrase when done. The live Assistant uses an explicit Send action and has no end-phrase watcher at `packages/client-core/src/assistant/useAssistant.ts:136`. The client no longer exposes any `carModeEndPhrase` setter, while the Gateway setter remains mapped at `src/CcDirector.Gateway/Api/AiModelsEndpoint.cs:232`.

How it fails for a real person:

Someone asking the Cockpit Assistant for help is taught a command that no longer ends anything. Normal Assistant turns are also prompted as if the owner were driving and hands-free, then told by a later addendum that the opposite is true. This is a real regression in the shared feature the Car Mode deletion was required to preserve.

How I verified it:

I traced `/assistant/turn` from `src/CcDirector.Gateway/Api/FleetBrainEndpoint.cs:64` into `CarModeBrain.RunTurnAsync`, followed the `get_help` tool to the fixed script, and checked the live Assistant turn machine for end-phrase handling. I also searched all mapped Gateway routes: no `/carmode` route remains. Voice mode is still mapped through `GatewayWingmanVoiceEndpoint.Map` at `src/CcDirector.Gateway/GatewayHost.cs:2432`, and dictation is still mapped through `GatewayDictationEndpoint.Map` at line 2494.

## Suspicions

### S1 - A provider error body may leak spoken content into the Gateway log

`src/CcDirector.Gateway/Api/GatewayWingmanVoiceEndpoint.cs:445` logs the first 200 characters of the raw text-to-speech provider response body. Some provider validation formats echo rejected input. I did not have a captured provider response proving that this provider does so, so I am not counting it as a confirmed raw-spoken-log defect. The line is outside the current guard's notion of a spoken variable and deserves a provider-response check.

## Sharp-question results

1. Utterance unconstructable without language: FAIL. Reflection and parameterized activation produce a speakable null-language object. A blank or null-valued `SpokenLanguage` record also passes the public factory.
2. Every Gateway sink dumb: FAIL. All three audio-production sites accept or construct from loose values; none receives `SpokenUtterance` as its entry contract.
3. Different project or language missed: Gateway production source was inventoried from provider calls. Desktop and browser contract mirrors were explicitly out of scope and were not reported missing.
4. Language can reach model: no current language-derived model branch was found, but the new prevention guard is defeated by a two-hop call chain.
5. Guards real: FAIL. The documented combined model sabotage was verified red only under the new guard; the two-hop form bypassed both. The raw-log guard also missed three elementary sabotages.
6. Car Mode gone and shared paths intact: PARTIAL. The Car Mode routes and surface are gone. Voice mode and dictation remain mapped. The live Assistant retained false Car Mode and end-phrase instructions.
7. Encoding: PASS for the Gateway phrase path. The compiled-content assertions use `\u` escapes at `src/CcDirector.Gateway.Tests/Speech/SpokenPhraseTests.cs:157`, and the source-byte guard requires the UTF-8 byte order mark at line 184. Saving the phrase file as cp1252 removes that byte order mark and fails the byte test; preserving a UTF-8 mark over cp1252 payload bytes makes the compiler decode corrupt characters and fails the escaped character assertions.
8. One-place fourth language: FAIL. The minimum is three production files: `SpokenLanguages.cs`, `SpokenVoices.cs`, and `SpokenPhrases.cs`.
9. Raw spoken content in current Gateway logs: no confirmed current direct write found. The guard that claims to enforce this is incomplete, and the raw provider-body line remains a suspicion.

## Verification limits

The normal focused `dotnet test` invocation restored and built the target assemblies, but the test host stalled without emitting a result on this machine. I do not count that attempt as a pass. The reflection probes and compiled-metadata guard sabotages above were run directly against the resulting Gateway assembly without the stalled test host. No production source was changed for those probes, and no sabotage was retained.

I did not run a live provider synthesis, a phone, or a Cockpit browser session. The mission brief assigns live end-to-end verification to the owner, and this pass was an architecture inspection rather than a proof rig.
