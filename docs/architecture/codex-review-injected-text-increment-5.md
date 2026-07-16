# Increment 5 Review: Confirmation Pass

## Result

I would not block this on the injected-text work reviewed here.

The blocking defect from increment 4 is fixed in the right place. The button handlers now mutate the store and re-enter `LoadAsync`, and `LoadAsync` is the single path that asks `FleetPreamble.BuildForSession` whether the selected text can actually be delivered. That removes the class of bugs where a button updates the banner by hand without checking the launch path.

## Checks

1. Save that throws is handled correctly.

   `BtnSave_Click` calls `_store.SaveYours(text)` before `LoadAsync`. If `SaveYours` throws `FleetPreambleTemplateException`, the store is not changed, `LoadAsync` is not reached, and the tab keeps the previous source state while showing the validation error. That is correct. A failed save should not change what agents get.

2. WriteMyOwn that throws is handled correctly.

   If switching to a saved custom version fails before the store changes, the old state remains live and the handler shows the error. If the store changes and the custom text is not deliverable, the following `LoadAsync` detects that through `BuildForSession` and shows the red no-injected-text state. The new `SwitchingBackToAStaleInvalidVersion_DoesNotAnnounceItAsLive` test covers the important version of this.

3. The sticky `_unavailable` flag is now scoped correctly.

   `LoadAsync` sets it only through `ApplyUnavailable` and clears it only after the launch path says the selected text is deliverable. The button handlers no longer clear it directly. That is the right ownership boundary.

4. `async void` handlers are acceptable here.

   Avalonia event handlers are a normal place for `async void`, and these handlers wrap their awaited work in try/catch. The main residual risk is rapid repeated clicks causing overlapping `LoadAsync` calls and last-completion-wins rendering. I would not block on that for this change because each load recomputes from the store through the same authoritative path, so the final state should still converge to the current store choice. Disabling buttons during reload would be polish, not a correctness gate.

5. I did not find a remaining tab versus launch disagreement in the reviewed paths.

   Initial load, UseOurs, WriteMyOwn, SwitchBack, and Save now all end by asking the launch render path. The earlier gap around hand-edited invalid files is covered both on first load and through the switch-back button.

## Verification

I ran `dotnet test --filter "InjectedText|FleetPreamble|PiPreamble|ClaudeHookInstaller|FleetTools"`. It passed in this environment: 84 core tests, 9 Avalonia tests, and 2 gateway tests matched and passed.
