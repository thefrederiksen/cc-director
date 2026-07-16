# Increment 3 Review: Injected Text Fixes And CC_FLEET_TOOLS

## Findings

1. The third banner state does not cover hand-edited unrenderable custom text.

   The launch paths now treat unreadable and unrenderable custom text the same way: they inject nothing and do not fall back to the DevThrottle text. `ControlEndpoints` catches `FleetPreambleTemplateException`, and `PiPreambleWriter` writes an empty file for it. That part is fixed.

   The Settings tab only enters the red "NO injected text" state when `_store.ActiveTemplate()` throws `InjectedTextUnavailableException` (`src/CcDirector.Avalonia/Controls/InjectedTextView.axaml.cs:84` to `122`). A hand-edited invalid `yours.txt` is different: `ActiveTemplate()` reads the file successfully, so `readError` is null. The tab then calls `ApplySource(Yours)` and shows `Your agents are getting YOUR text, not DevThrottle's.` plus `Your text (this is what your agents get)` (`src/CcDirector.Avalonia/Controls/InjectedTextView.axaml.cs:134` to `146`).

   `Refresh()` does validate the text and disables saving (`src/CcDirector.Avalonia/Controls/InjectedTextView.axaml.cs:216` to `222`), but the banner and editor label are still wrong. New sessions, Claude clear, Claude compact, Codex clear, Codex compact, and Pi launch will get no injected text, not the invalid custom text shown under a "this is what your agents get" label.

   The fix should classify the loaded custom text through the same renderability rule used by launch. If the selected custom text cannot render, use `ApplyUnavailable` with the validation problem, while still leaving the editor editable so saving a repaired version is the way out.

## Answers To The Questions

1. I did not find a product dependency on `CC_FLEET_TOOLS`. The only code write path is `SessionManager` (`src/CcDirector.Core/Sessions/SessionManager.cs:521` to `524`). Repository search found no product read path. There is one setup comment saying the skill installer covers machines beyond the environment hint, which supports treating the variable as agent-facing prose rather than application state.

2. Removing `CC_FLEET_TOOLS` when the live source is Yours is the right consent boundary. If the user deletes command prose from their custom text, keeping a second command-list channel would make the tab incomplete. Existing sessions can still have an old `CC_FLEET_TOOLS` value because process environments are immutable after launch; the docs now disclose that. There is also a narrow launch race: `CC_FLEET_TOOLS` is decided before process start, while Claude and Codex fetch the preamble from the endpoint at SessionStart. If the user changes the setting in that small window, the environment variable and hook preamble can disagree. That is probably acceptable if documented as "setting changes apply to future fetches, but environment variables are fixed at process start"; it is not a fallback-to-ours bug.

3. Collapsing whitespace in `BuildForSession` is the right shared rule for user text, and putting it in the common render path fixed the delivery-path divergence. It does not create a realistic break for the shipped default because existing default tests assert substantive content through `Build`, and a whitespace-only shipped default would be a catastrophic content regression either way. A small guard that `BuildForSession(..., AlwaysOurs)` is non-empty would make that assumption explicit.

4. The Pi fixes are materially better: unreadable, invalid, and whitespace-only custom text no longer aborts launch or diverges from the hook paths. The new Pi tests also correctly pin their store instead of reading the developer's real config.

5. The POSIX hook fix is correct. `curl -sf` prevents HTTP error bodies from becoming hook stdout, and the endpoint catch for `FleetPreambleTemplateException` removes the known 500 path anyway.

## Documentation Check

The docs are mostly aligned with the new behavior, including `CC_FLEET_TOOLS` and mid-session changes. The statement near the top that the tab shows what agents are actually getting is still false for the invalid-on-disk custom template case above, because the launch paths inject nothing while the tab can show the custom text as live.

## Verification

I ran `dotnet test --filter "InjectedText|FleetPreamble|PiPreamble|ClaudeHookInstaller|FleetTools"`. It passed in this environment: 83 core tests, 2 gateway tests, and 4 Avalonia tests matched and passed.
