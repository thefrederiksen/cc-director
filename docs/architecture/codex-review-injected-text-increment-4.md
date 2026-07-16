# Increment 4 Review: Final Pass

## Finding

1. Switching back to a stale invalid custom file can still make the banner lie.

   The load path fix is correct: `InjectedTextView.LoadAsync` now asks `FleetPreamble.BuildForSession` whether the selected text can actually be delivered (`src/CcDirector.Avalonia/Controls/InjectedTextView.axaml.cs:113` to `128`). That closes the original missing-file and hand-edited-invalid-file mismatch on first load.

   The remaining gap is the `Write my own version` / `Switch back to my version` button path. When ours is live and `yours.txt` exists, the button calls `_store.UseYours()`, reads the file, clears `_unavailable`, applies the normal Yours banner, and then calls `Refresh()` (`src/CcDirector.Avalonia/Controls/InjectedTextView.axaml.cs:375` to `397`). It never asks `FleetPreamble.BuildForSession` whether that saved file can be delivered.

   Reproducible sequence:

   1. The user has a custom file on disk.
   2. The user is currently running the DevThrottle text.
   3. The custom file is edited outside the product into an invalid template such as `[IF_SIGNED_IN]\nhello`.
   4. The user opens Settings, Injected text. The banner correctly says the DevThrottle text is live.
   5. The user clicks `Switch back to my version`.

   After step 5, config is set to Yours. Launch paths call `BuildForSession`, catch `FleetPreambleTemplateException`, and inject nothing. The tab says `Your agents are getting YOUR text, not DevThrottle's.` and labels the editor `Your text (this is what your agents get)`. `Refresh()` shows the validation error and disables Save, but the source banner is still wrong.

   There is a similar variant with an existing but unreadable `yours.txt`: `_store.UseYours()` can succeed, `_store.ReadYours()` can throw, the catch only shows an error, and the old Ours banner remains even though the live config has already changed to Yours.

   This is the same class of defect as the previous one, just through a button transition instead of initial load. The fix is to make this transition use the same deliverability path as initial load, or simply reload from the store after `_store.UseYours()` rather than manually reassembling the state.

## Answers

1. The sticky `_unavailable` flag is correct for the loaded-unavailable state and for Save / UseOurs. The bug is that `BtnWriteMyOwn_Click` clears it and applies the normal Yours state before proving the selected custom file is deliverable.

2. Yes, there is still a tab versus launch disagreement: switching back to an invalid or unreadable saved custom file while ours is live.

3. I would block this before main. The core injection paths are much better, and I did not find a remaining path that silently substitutes DevThrottle text after the user declined it. But the stated product requirement is that the banner never mislead the user about what agents are getting, and this transition still violates that.

## Verification

I ran `dotnet test --filter "InjectedText|FleetPreamble|PiPreamble|ClaudeHookInstaller|FleetTools"`. It passed in this environment: 84 core tests, 8 Avalonia tests, and 2 gateway tests matched and passed.
