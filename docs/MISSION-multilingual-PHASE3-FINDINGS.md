# Phase 3 findings - reported, NOT fixed

Written by the Phase 3 Manager while building the Language tab (#1010). Nothing here was changed: the
Architect's instruction was to finish Phase 3 without starting on anything else, and the Phase 1 and 2
inspection is a separate lane. Each item is a candidate for the Phase 4 brief.

---

## F1 - The desktop app speaks with the process-global voice, so the language never reaches it

**Where:** `src/CcDirector.Core/Voice/TtsService.cs:111`, reached from
`src/CcDirector.Avalonia/Voice/DesktopTtsPlayer.cs:60`, which `FifoWindow` constructs and uses.

Phase 3 makes the account's language decide the VOICE, and it does so in the one read every Gateway
synthesis path already made - `TenantSettingsResolver.TtsVoice`. Four call sites pick it up by
construction: the narration service, the read-aloud route, the text-to-speech routing in `GatewayHost`,
and the settings snapshot.

`TtsService` is a fifth synthesis path and it is not one of them. It resolves the voice as
`TtsVoiceConfig.Resolve(mode)` - the process-global `config.json` value - and takes no tenant at all, so
there is nothing for a per-account setting to reach. An account set to French, speaking through the
desktop app, is read out by whatever voice the machine's config holds: in practice the English default.

**Why it matters, and why it is not simply cosmetic.** French words in an American voice is the same
class of failure as French narration answered in English - the audio plays, nothing errors, and the only
symptom is that it sounds wrong. It is also the same shape as inspection finding C1: product speech that
lives outside the Gateway's contract and was therefore never migrated.

**Why it was not fixed here.** The fix is not local. `TtsService` lives in `CcDirector.Core` and has no
tenant, no settings store and no Gateway resolver; giving it one means threading a tenant into the
Avalonia desktop app, which is a scope decision and not a Phase 3 edit.

**Open question for whoever picks this up:** whether the TEXT the desktop speaks is Gateway-authored (in
which case only the voice is wrong) or authored locally in English (in which case it is a C1 text gap as
well). Not established here - stating it either way without looking would be a guess.

---

## F2 - The English sample sentence now exists in two places

`SpokenPhrases.SettingsVoiceSample` (the phrase file, three languages) and `AiTab.tsx`'s `SAMPLE_TEXT`
constant hold the same English sentence. The AI tab is out of the strip on both surfaces and is kept
deliberately, reversible by one word, so this is a dormant duplicate rather than a live inconsistency -
and it was left alone on purpose, because the Architect's instruction was not to touch `AiTab.tsx`.

If the AI tab is ever restored to the strip, it should read its sample from the phrase file rather than
carry its own copy: a sentence the product speaks belongs where the accent, completeness and encoding
guards are.

---

## F3 - The voice-resolution path is not covered by the language-to-model guard

Inspection finding C4 already records that `SpokenLanguageContractTests`'
`No_method_derives_a_speech_model_from_a_language` is method-local and cannot see through
`TenantSettingsResolver`. That has a direct consequence for the code Phase 3 added:
`TenantSettingsResolver.TtsVoice` now reads the spoken language, and it is exactly the kind of method
where a speech-model branch would be tempting to add later. It does not touch the model today - the
engine is resolved separately in `TtsModel`, and an HTTP test asserts the Language tab's document names
no model at any depth - but the compiled guard would NOT catch it if someone added one, because a call
through the resolver is invisible to it.

Worth widening the guard in Phase 4 rather than trusting that nobody will.
