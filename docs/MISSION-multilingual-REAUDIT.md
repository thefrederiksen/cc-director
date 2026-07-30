FAIL

# Focused multilingual re-audit

Commit inspected: `7582b360be0afeb181957920f3268cdff472913a` in detached HEAD.

Scope was limited to the four fixes named in the re-audit brief. I did not re-report the deferred
findings in issue 1034. I did not fix, commit, push, merge, or open anything. All probe files and
processes were removed.

## Fix 1 - BROKEN

The configured/unreachable split itself works, but an attached desktop can still treat an unknown
language as resolved, and two live callers silently swallow the refusal result.

### What I attacked

- How `HasAccount` is decided in standalone, configured-but-never-reached, and changed-config
  topologies.
- Successful JSON responses with missing, null, blank, wrong-type, and unknown values.
- Cached language, voice, utterance, and credential state.
- Every production caller of `TtsService` and `DesktopTtsPlayer`.
- Every caller that receives the new `bool` result.

### What holds

- `HasAccount` comes from `GatewayConfig.IsEnabled`, which is exactly a nonblank configured URL. It
  is not a reachability probe and it is not cached. No URL returns `NoAccount`; a configured URL
  that has never connected attempts the lookup and returns `Unavailable` on failure. That errs in
  the safe direction: it refuses rather than using the machine voice.
- The account lookup is performed on every utterance. I found no cached language, voice, or
  utterance. The independent hosted key cache still exists, which is why refusal here is required.
- Missing, null, blank, and wrong-type `language` or `voice` fields all become `Unavailable`. A
  wrong-type `GetString()` throws inside the catch and is also refused.
- No other production Core or desktop code constructs `TtsService`. The only production instance is
  inside `DesktopTtsPlayer`; its bare-string call is the explicit no-Gateway arm. The old
  `ITextToSpeech` implementations have no production callers.
- `SpeakPlaybackDialog` checks the returned boolean and shows an error instead of closing.

### What is broken

1. A nonblank unknown language is not refused. `AccountUtterance.ForAsync` calls
   `SpokenLanguages.Resolve(code)` at
   `src/CcDirector.Core/Voice/AccountUtterance.cs:111`; that resolver converts every unknown code
   to English. A 200 response such as `{"language":"de","voice":"af_bella"}` therefore produces
   `HasAccount=true` with a nonnull English utterance. `DesktopTtsPlayer` sends it to synthesis. This
   is an attached Director speaking after it failed to establish the response's language, which is
   the condition the fix says must refuse. The shipped test at
   `src/CcDirector.Core.Tests/Voice/AccountUtteranceTests.cs:100` explicitly expects this behavior,
   and the 12 focused `AccountUtteranceTests` passed.

2. The refusal is silent on both FIFO speech paths. `FifoWindow` ignores the returned boolean for
   the automatic briefing at `src/CcDirector.Avalonia/FifoWindow.axaml.cs:195` and the Ask Wingman
   answer at line 473. The latter then restores the ordinary green resting status. The person gets
   no explanation that speech was deliberately refused, even though the player logged one. Only
   `SpeakPlaybackDialog` handles the result.

The supported Settings reapply path stops and rebuilds the running Gateway clients before a cleared
configuration becomes local-only. A raw external edit can temporarily make the live connection and
the freshly loaded config disagree, but the two confirmed failures above do not depend on that edge.

## Fix 2 - BROKEN

The visible help script was repaired, but the model still receives Car Mode framing through the live
Assistant tool catalog on every turn.

### What I attacked

- The complete system message and tool catalog supplied to the model.
- The `get_help` execution path and all three fixed-language help scripts.
- The Cockpit and mobile Assistant entry paths.
- End-phrase instructions and other car-only framing.

### What holds

- `BuildSystemPrompt` now opens as the DevThrottle Assistant and contains no driving or hands-free
  premise. The appended block only changes answer length.
- `CarModeHelp.SpokenScript` now returns `AssistantHelpScript`, which describes the fleet manager,
  command-versus-relay grammar, and no end phrase. English, French, and Spanish versions take no
  end-phrase setting.
- Cockpit and mobile both use the shared `useAssistant` hook and `/assistant/turn`; there is no
  second client prompt or help implementation to drift.

### What is broken

`CarModeBrain.RunTurnAsync` passes `ToolCatalogJson` to the model on every round at
`src/CcDirector.Gateway/CarMode/CarModeBrain.cs:193`. The live `get_help` tool description at line
862 still says, twice, that it explains what Car Mode can do and the two ways to talk to Car Mode.
This is model input just as surely as the system message is. Both live Assistant shells therefore
still frame part of every turn as the removed surface. The eventual fixed help text is accurate if
the model chooses the tool, but the prompt path itself does not satisfy the fix.

The focused Gateway test host stalled on this machine and produced no result, so I do not count it as
a pass or failure. The source data flow above is direct: the constant is handed to the chat transport
on every live turn.

## Fix 3 - BROKEN

Ordinary construction now rejects blank language parts and known cross-language voices, but blank
and unknown language codes can still reach a real synthesis call through the desktop utterance sink.

### What I attacked

- Blank, whitespace, differently cased, unknown, and fabricated language values.
- Known wrong-language and unknown voice overrides.
- Read-aloud, narration, stored voice, settings write, warm-up, and desktop synthesis paths.
- Whether each sink actually makes its behavior depend on the language read.

### What holds

- `SpokenLanguage` rejects blank `Code`, `EnglishName`, and `NativeName` during ordinary construction
  and init assignment.
- `SpokenUtterance.For` rejects a voice that the built-in catalog assigns to a different language.
  `/wingman/tts` catches that exception and returns 400 before its provider call.
- Per-language stored voices are revalidated on every read. A missing, corrupt, retired, or
  wrong-language stored choice degrades to that language's registered default.
- The Language settings writes reject unsupported language codes and voices. The narration service
  and interactive Gateway read-aloud sink both access `LanguageCode` before posting.

### What is broken

1. `SpokenLanguage` validates only that each string is nonblank. It does not require a supported or
   even canonical code. `new SpokenLanguage("zz", "Unknown", "Unknown")` is valid.
   `SpokenUtterance.For` then permits it when the voice is also outside the built-in catalog, because
   `SpokenVoices.LanguageOf` returns null and only a known cross-language owner is rejected.

2. The desktop utterance sink does not read the language. Its overload at
   `src/CcDirector.Core/Voice/TtsService.cs:141` immediately delegates using only `Text` and `Voice`
   at line 144. A focused compiled probe built an utterance with code `zz` and another whose
   `SpokenLanguage` object was uninitialized, leaving `Code` null. Both calls returned success and
   the fake provider recorded two synthesis posts:

   ```text
   unknownCode=zz;unknownSuccess=True
   blankCodeIsNull=True;blankSuccess=True
   providerCalls=2
   ```

   The second case also exposes a narrower validation error in `LanguageCode`: it throws when the
   `Language` reference is null, but returns a null or blank `Language.Code` from a fabricated
   nonnull language object. The probe used the public utterance factory after fabricating only the
   language object. This is the same compiled sabotage standard used by the earlier audits, and the
   probe was removed after the run.

I did not count the already-deferred general Gateway bare-string sink design from issue 1034 as a
new finding. This break is in the fixed language type plus the desktop's typed utterance overload.

## Fix 4 - BROKEN

The sink correctly refuses an empty language before engine lookup, but it still speaks a nonsense
nonblank language through the device default voice.

### What I attacked

- Missing, empty, whitespace, and nonsense language values on a forged structural utterance.
- Validation order in a browser with no speech engine.
- The separation between contract failures and swallowed platform failures.

### What holds

- Empty and whitespace languages throw before `platformEngine()` is called. A browser without speech
  synthesis cannot absorb that error.
- Missing or null language values also throw before engine lookup through the attempted `.trim()`.
- Platform `cancel`, voice-list, construction, and `speak` failures below validation are deliberately
  swallowed and return false, so an engine failure does not take down the caller.
- The shipped 20 focused browser speech tests passed.

### What is broken

The only runtime check at `packages/client-core/src/speech/localSpeech.ts:84` is
`utterance.language.trim().length === 0`. `utteranceFor` has the same nonblank-only rule. A language
such as `not-a-language` passes, finds no device voice, is assigned to the platform utterance, and
reaches `engine.speak` at line 99 with `voice` unset. That leaves pronunciation to the device default,
the silent failure shape this sink exists to prevent.

A temporary Vitest probe passed while proving the defect: `speakLocally` returned true and the fake
engine received one utterance with `lang` equal to `not-a-language`. The probe file was removed.

## Verification record

- Read issues 1031, 547, and 1034 from `thefrederiksen/devthrottle_internal`; the public-repository
  issue numbers had been transferred/deleted.
- Read and inspected all four commit diffs and the production call paths named above.
- `AccountUtteranceTests`: 12 passed.
- Browser `localSpeech.test.ts` plus `oneSpeechPath.test.ts`: 20 passed.
- Temporary nonsense-language browser probe: 1 passed, demonstrating the provider call.
- Temporary compiled C# language/synthesis probe: two successful provider calls, one unknown code and
  one null code inside a nonnull language object.
- Focused Gateway tests: no result because the test host stalled; lingering probe/test processes were
  terminated by exact process id.
- `git status` was clean after all sabotage removal and before this report was added.
