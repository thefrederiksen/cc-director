# Onboarding wizard rework: independent code review

Reviewed only the requested diff from `3d2c70c6` across the seven specified files.

## Findings

### 1. Removing an auto-detected code root can be undone by the still-running scan

- **Severity:** High
- **Location:** `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml.cs:953-959`, `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml.cs:1133-1160`
- **Problem:** Removal deletes the path from `_codeAddedRoots`, but there is no tombstone recording that the user rejected it. A later progress callback for the same path therefore treats it as new and calls `AutoAddCodeRootAsync`, which writes it back to `root-directories.json` and redraws the row.
- **Concrete failure:** Start with a registered conventional root such as `~/Code` containing repositories. Enter the Code step and click **Remove** before the scout reaches that candidate. When the scout reports `~/Code`, the `ContainsKey` check now returns false and the root is silently re-added. The user's explicit opt-out does not stick.

### 2. The scan completes before its serialized registrations, and New Session reads the rescan only once

- **Severity:** High
- **Location:** `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml.cs:953-968`, `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml.cs:1020-1028`, `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml.cs:1096-1129`, `src/CcDirector.Avalonia/NewSessionDialog.axaml.cs:552-555`, `src/CcDirector.Avalonia/NewSessionDialog.axaml.cs:591-604`
- **Problem:** Each `Progress<T>` callback discards the `AutoAddCodeRootAsync` task. `await CodeFolderScout.ScanAsync(...)` therefore waits for discovery only, not for any of the queued file writes behind `_codeStoreGate`. The screen can announce that the scan is finished while rows are still being persisted. Leaving the step starts a fire-and-forget repository rescan over whatever subset has completed. `NewSessionDialog` then takes one `RepositoryMonitor.Snapshot()` in its constructor and never refreshes when that rescan finishes.
- **Concrete failure:** On a machine where several roots are found, proceed as soon as the scan says it is complete, then click **Start my first agent**. If registrations or the repository rescan are still running, New Session snapshots the old/partial model and can still show **No repositories yet**. Repositories arriving after the dialog opens never appear in that dialog. Closing the wizard while the first registration is still pending is worse: `OnClosed` sees `_codeRootsChanged == false`, then the late task writes the root after the only publish opportunity has passed.

### 3. Review mode promises no unsolicited work but automatically rewrites the roots configuration

- **Severity:** Medium
- **Location:** `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml.cs:1549-1559`, `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml.cs:288-293`, `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml.cs:948-959`
- **Problem:** The review welcome says everything is already set up and "nothing is redone unless you ask," but reaching the Code step immediately starts the scout and automatically persists every detected root. Review mode changes only copy; it does not change this first-run side effect.
- **Concrete failure:** Complete or skip onboarding with no code roots, reopen the wizard from Settings, choose **Review each step**, and advance to Code. Without pressing Browse or any confirmation control, detected folders are written to `root-directories.json`. This is a persistent configuration change caused by inspection alone, contrary to the review-mode promise.

### 4. Removing a repository from New Session immediately adds it back

- **Severity:** Medium
- **Location:** `src/CcDirector.Avalonia/NewSessionDialog.axaml.cs:591-618`, `src/CcDirector.Avalonia/NewSessionDialog.axaml.cs:1236-1249`
- **Problem:** The list now contains both registry entries and monitor-only discoveries, but the existing Remove handler only removes from the registry. It immediately calls `BuildRepositoryList()`, which unions the unchanged monitor snapshot back in.
- **Concrete failure:** For any repository under a watched root, click its Remove button in New Session. `_registry.Remove(path)` succeeds, but the following rebuild sees the same path in `RepositoryMonitor.Snapshot()` and recreates the row. Even a recently used repository reappears as a discovered item, so the UI action can no longer remove it from the displayed list.

### 5. The macOS volume sweep is not bounded by the advertised timeout

- **Severity:** Medium
- **Location:** `src/CcDirector.Core/Onboarding/CodeFolderScout.cs:71-83`, `src/CcDirector.Core/Onboarding/CodeFolderScout.cs:104-122`, `src/CcDirector.Core/Onboarding/CodeFolderScout.cs:142-155`
- **Problem:** `CandidateRoots()` synchronously enumerates `/Volumes` and then every mounted volume before `ScanAsync` reaches its first `ct.ThrowIfCancellationRequested()`. `Directory.GetDirectories` does not accept the token, and `CountRepos` is also uninterruptible. The 10-second cancellation source in the dialog therefore does not bound the whole sweep as the method comment claims.
- **Concrete failure:** Mount an unavailable or stalled network share under `/Volumes` and open the Code step. Enumeration can block past ten seconds; the cancellation token is set but cannot be observed until the filesystem call returns. The progress bar remains indefinitely and the timeout branch that offers **Keep looking** never runs.

### 6. An in-flight tools refresh can create a polling timer after the window has closed

- **Severity:** Medium
- **Location:** `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml.cs:762-820`, `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml.cs:836-880`, `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml.cs:883-900`, `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml.cs:2070-2079`
- **Problem:** Tool refresh and repair operations have no cancellation or closed/current-step guard. `OnClosed` stops the timer that exists at that moment, but an already-awaited catalog read or repair can resume afterwards and call `StartToolsPoll()`. The new timer's Tick handler captures the closed dialog and continues refreshing it.
- **Concrete failure:** Open Tools while catalog inspection is slow, or start **Fix this now**, then close the wizard before the await completes while at least one tool remains missing. The continuation starts a new two-second `DispatcherTimer` after `OnClosed` has already run. The closed window remains referenced and its hidden controls are updated indefinitely.

### 7. The Done receipt discards the new "Not installed" state and repeats the old false promise

- **Severity:** Medium
- **Location:** `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml.cs:779-812`, `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml.cs:1962-1968`
- **Problem:** The Tools step correctly distinguishes a stalled tool as **Not installed**, but the receipt reduces every non-ready state back to **Tools installing** / **Finishes on its own in the background**.
- **Concrete failure:** Leave a tool missing for 45 seconds until the screen says it did not install, then continue to Done. The receipt immediately contradicts the prior screen and promises that the failed install will finish without intervention--the exact unkeepable claim the third state was intended to remove.

### 8. A saved URL is treated as proof that the gateway and all dependent features work

- **Severity:** Medium
- **Location:** `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml.cs:1679-1699`
- **Problem:** `AdoptExistingGateway` checks only `GatewayConfig.IsEnabled`, which is true for any nonblank saved URL. It performs no reachability, authentication, or enrollment check, yet says the machine is enrolled and that phone access, voice, and the morning report are working.
- **Concrete failure:** Leave a stale, mistyped, or unreachable URL in the saved gateway config and rerun the wizard. The gateway step opens in the green connected state with **Continue**, so an unenrolled machine is not offered the normal enrollment path unless the user notices and selects **Connect a different gateway**.

### 9. The gateway choices still do not expose radio-button semantics or selected state to accessibility clients

- **Severity:** Low
- **Location:** `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml:429-476`, `src/CcDirector.Avalonia/FirstRunWizardDialog.axaml.cs:1752-1772`
- **Problem:** Making a `Border` focusable and assigning an automation name does not turn it into a radio button or expose selection state. The custom handlers update only visual styling and `_gatewayChoice`; no automation property tells a screen reader which option is selected. `GotFocus` also changes the choice merely by tabbing through it rather than using standard radio-group keyboard behavior.
- **Concrete failure:** Navigate the three cards with a screen reader. Each has a static name, but the client cannot query "selected" versus "not selected," and moving focus mutates the pending choice without an activation command. A user cannot reliably review the chosen gateway option nonvisually.

### 10. The canonical-order test was weakened enough to miss a real ordering regression

- **Severity:** Low
- **Location:** `src/CcDirector.Core.Tests/Onboarding/FirstRunWizardModelTests.cs:125-143`, `src/CcDirector.Core.Tests/Onboarding/FirstRunWizardModelTests.cs:146-164`
- **Problem:** `Assert.Equal(FirstRunWizardModel.CanonicalOrder, model.Steps)` compares output to the same list used to construct and order it, while the former full sequence assertion was changed to a set comparison. The remaining spot checks constrain Gateway, Tools, and Screenshots but do not assert the full intended sequence.
- **Concrete failure:** Change the production order to `Welcome, Gateway, Code, Tools, Agents, Screenshots, Done`. The count, Gateway index, Tools index, Screenshots index, set comparisons, and subset normalization test all still pass, despite violating the stated `Agents -> Tools -> Code` journey.

## Verification

- `dotnet test src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj --filter "FullyQualifiedName~FirstRunWizardModelTests" --no-restore` - 23 passed.
- `dotnet build src/CcDirector.Avalonia/CcDirector.Avalonia.csproj --no-restore` - succeeded with 0 warnings and 0 errors.
