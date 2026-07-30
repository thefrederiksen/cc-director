FAIL

# Final focused audit

## Verdict

Known languages are not enforced on every path. A present but blank tenant setting is treated as if the setting were absent, silently becomes English, and reaches a real speech POST. A separate browser caller also swallows the new unknown-language refusal and proceeds through a recognized menu guard.

## F1 - A stored blank language silently becomes English and is spoken

Where:

- `src/CcDirector.Gateway/Settings/TenantSettingsStore.cs:76-79`
- `src/CcDirector.Gateway/Settings/TenantSettingsResolver.cs:292-297`
- `src/CcDirector.Gateway/Settings/TenantSettingsResolver.cs:113-119`

The raw tenant store rejects only null values. It permits a row whose `spoken_language` value is `"   "`. The resolver then uses `string.IsNullOrWhiteSpace(stored)` and returns `SpokenLanguages.Default`, which is English. This is not the legitimate no-choice case: the store represents no choice by having no row and returning null. A present blank row can only be malformed persisted data, the same corruption or rollback case for which a nonblank unknown value now throws.

How it fails in practice: a malformed blank row is laundered into a valid English `SpokenUtterance`. Every downstream known-language check sees `en`, so the hosted or desktop speech sink posts it normally. The original invalid value is no longer observable.

Compiled probe against the built Gateway, contracts, and Core assemblies:

```text
store.Set(TenantId.Local, SpokenLanguage, "   ")
resolver.Utterance(..., "operators_own_voice", "words")
TtsService.GenerateAsync(utterance)

stored-blank=en;posts=1;success=True
```

This is a direct surviving route where a blank language ends in real English speech.

## F2 - The browser audio-send caller swallows refusal and sends into a recognized menu

Where: `packages/client-core/src/voice/useVoiceMode.ts:700-712`.

On the fire-and-forget audio path, `getWaitingScreen` and `speakBlocked` share one broad `try` block. For a healthy 200 menu response with `spokenLanguage: "zz"`, `utteranceFor` throws as intended. The surrounding catch mistakes that sink refusal for a failed screen read, clears the visible menu notice, and calls `backgroundTranscribeAndSend` anyway.

That is worse than invisible refusal alone. This guard exists because sending the recording and its Enter into a chooser can activate the highlighted option. The product positively recognized the menu, but an unknown response language converts that refusal into delivery.

A temporary Vitest probe drove the real hook with a 200 menu response naming `zz`. Observed result:

```text
speechSynthesis.speak calls: 0
backgroundTranscribeAndSend calls: 1
menuBlocked after the turn: null
```

The probe passed against the built client and was removed. The typed reply path does not have this defect: its outer catch surfaces the thrown refusal as an error and returns false.

## F3 - One shipped Gateway test is broken by the constructor change

Where: `src/CcDirector.Gateway.Tests/Speech/SpokenPhraseTests.cs:131`.

`An_unknown_language_throws_naming_the_key_and_never_the_words` still constructs `new SpokenLanguage("xx", ...)` before its `Assert.Throws<InvalidOperationException>`. The new constructor now throws `ArgumentException` on that line, so the assertion body is never reached and the test fails.

The serialized Gateway suite was not awaited. This result does not depend on it: the compiled contracts probe produced `constructor:zz=REFUSED` with `ArgumentException`, while the test source requires construction to succeed and a later phrase lookup to throw a different exception.

## Entry-point attack matrix

| Entry point | Malformed result | Evidence |
|---|---|---|
| Desktop HTTP response body | Blank, unknown, and region codes are refused as `AccountVoiceLookup.Unavailable`; no fallback utterance is built | `AccountUtterance.cs:101-119`; 12 focused Core tests passed |
| Browser HTTP response body, typed send | Factory refuses unknown/blank; caller shows an error | Source trace plus browser tests |
| Browser HTTP response body, audio send | Sink refuses, but caller swallows it and sends anyway | F2 compiled hook probe |
| Stored tenant value, unknown nonblank | `SpokenLanguages.Require` throws before utterance creation | Source and compiled resolver behavior |
| Stored tenant value, blank present row | Silently defaults to English and posts | F1 compiled provider-post probe |
| Missing tenant value | English default | Intended compatibility behavior; distinct from the malformed present row |
| Settings language write | Unknown, blank, and `fr-FR` are refused; case and surrounding space are canonicalized | Endpoint and resolver trace |
| `SpokenLanguage` constructor | Unknown, blank, and `fr-FR` refused; case and surrounding space canonicalized | Compiled probe |
| JSON deserializer | Unknown constructor value refused | Compiled `System.Text.Json` probe |
| Browser factory | Unknown/blank/region refused; case and surrounding space canonicalized | 20 local-speech tests passed |
| Browser fabricated plain object | Sink independently refuses unknown/blank before looking for an engine | 20 local-speech tests passed |
| C# `with` clone | `SpokenLanguages.English with { Code = "zz" }` succeeds because the init setter bypasses `RequireKnownCode` | Compiled probe produced `with-clone=zz` |
| C# uninitialized object | Can enter `SpokenUtterance.For` with a custom voice, but the desktop typed sink refuses before POST | Compiled probe produced `posts=0` |
| Default/config path | No spoken-language code is read from `config.json`; standalone speech uses the operator voice because there is no account language | Source trace |
| Test helper | The phrase helper test is now blocked by the constructor and fails | F3 |

The `with` clone shows that `SpokenLanguage` itself does not fully uphold its claimed invariant. The current public desktop sink still rejects the resulting utterance. The two hosted sinks at `GatewayWingmanVoiceEndpoint.cs:452` and `WingmanVoiceService.cs:1016` only read `LanguageCode`; they do not resolve it against the known set. Current production construction reaches them through `TenantSettingsResolver`, so I did not count the clone alone as a demonstrated production speech route. F1 is the demonstrated route through that resolver.

## Casing and whitespace

Compiled results were identical in the C# and browser resolution rules:

| Input | Result |
|---|---|
| `FR` | accepted and canonicalized to `fr` |
| ` fr ` | accepted and canonicalized to `fr` |
| `Fr` | accepted and canonicalized to `fr` |
| `fr-FR` | refused |
| blank or null | refused at ordinary input boundaries |

This is internally consistent and supportable: codes are language identifiers, not locales, while harmless case and surrounding whitespace are normalized. F1 is the exception at the persisted read boundary.

## Static initialization

I forced first touch in two separate processes so neither order inherited initialized state:

```text
SpokenLanguage first: first=fr; second=en
SpokenLanguages first: first=en; second=fr
```

Neither order raised `TypeInitializationException`. Keeping `KnownCodes` on `SpokenLanguage` avoids the initialization cycle as claimed.

## Custom self-hosted voice

The tightening did not reject a voice belonging to no registered language. A compiled probe built an English utterance with `operators_own_voice` and drove the typed sink:

```text
custom-voice-posts=1;success=True
```

Known voices belonging to another language remain refused. The intended self-hosted escape hatch still works.

## Desktop refusal visibility

Both requested `FifoWindow` paths now consume the returned boolean:

- Briefing refusal sets `Briefing not read aloud - see the log` in yellow at `FifoWindow.axaml.cs:203`.
- Ask Wingman refusal leaves a yellow `Answer not read aloud` resting status at `FifoWindow.axaml.cs:488-492` instead of restoring green.

`AccountUtterance` converts blank, unknown, partial, malformed JSON, and request failures into the unavailable result used by `DesktopTtsPlayer`, so these normal refusal cases reach the boolean rather than the exception-only logging branches.

## Car Mode framing in model input

No fixed Car Mode framing remains in the Assistant model input.

- The actual system prompt identifies the DevThrottle Assistant.
- The tool catalog says `get_help` explains the Assistant.
- The prompt examples use `Local Files Manager`, not `Car Mode Demo`.
- A production string search found no old phrases such as `what Car Mode can do`, `ways to talk to Car Mode`, `voice of DevThrottle Car Mode`, `owner is driving`, or `hands-free` in the system prompt, tool catalog, descriptions, or examples.

Remaining `Car Mode Demo` occurrences are test data and comments in legacy-named implementation files, not fixed model input.

## Executed checks

- Read both issue 1031 and issue 547 from `thefrederiksen/devthrottle_internal`.
- Read `git log -2` and the full HEAD diff at `8c4ef2a4`.
- Ran 12 focused `AccountUtterance` tests: all passed.
- Ran 28 focused browser speech and menu-guard tests: all passed before sabotage.
- Ran the temporary unknown-language hook sabotage: reproduced F2.
- Ran isolated compiled C# probes for both static first-touch orders, constructor, resolver, casing, deserialization, `with` clone, uninitialized object, custom voice, sink POST count, and raw stored blank value.
- Did not wait on the lock-serialized Gateway suite. F3 was established from the compiled constructor behavior and the exact test statement.

All temporary source probes and generated probe files were removed after use.
