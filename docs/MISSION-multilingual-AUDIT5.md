PASS

# Multilingual mission audit 5

## Scope and basis

This audit inspected detached HEAD `27049168032fb392dc167d36c4a52a2a96624903`. I read the full HEAD diff before testing and read issue 547 from `thefrederiksen/devthrottle_internal` after the old product repository redirected. The audit was limited to F2, F1, F3, the named nonregressions, and the required suites.

All three fixes hold. No build, product-code fix, commit, push, merge, or branch checkout was performed.

## F2 - speech failure cannot authorize delivery

I drove the real `useVoiceMode` hook with temporary probes at its API and delivery boundaries. The important result was measured at `backgroundTranscribeAndSend`, not inferred from state names.

| Attack | Recording delivered | Menu notice after the call |
| --- | --- | --- |
| Successful menu read, unknown spoken language | No | Original notice remained; the language refusal reached the hook error state |
| Successful menu read, known language, `cancel()` throws | No | Original notice remained |
| Successful menu read, known language, `speak()` throws synchronously | No | Original notice remained |
| Successful menu read, platform speech engine absent | No | Original notice remained |
| Successful menu read, adversarial async failure or rejected return from `speak()` | No | Original notice remained |
| Screen read throws synchronously | Yes, once | Cleared to null |
| Screen read returns a rejected promise | Yes, once | Cleared to null |
| Screen read rejects while a notice from the previous turn is visible | Yes, once | The stale notice was cleared |
| Successful 200 with ordinary text screen | Yes, once | Cleared to null |
| Invalid JSON or missing/unknown screen discriminator | Yes, once | Cleared to null after rejection or normalization to `blocked` |
| Valid `menu` discriminator with missing optional prose fields | No | Notice state remained non-null but empty; both clients mount the menu alert, and Cockpit also shows its generic `Waiting on a menu` label |
| Speech-field getter throws after a valid menu response | No | Original notice remained |

The supplemental ordering probe recorded `message` before `spoken`, then forced the speech-field read to throw. The hook still committed the menu notice and never called delivery. This matches the code: only `getWaitingScreen` is inside the first try/catch; `setMenuBlocked(screen.message)` occurs before speech; the menu branch returns unconditionally; and only then can the non-menu path clear the notice and start delivery.

The Web Speech `speak` contract is synchronous and returns void. An adversarial rejected promise or later asynchronous platform failure cannot cross the already-taken menu return and start delivery. Platform absence and platform exceptions are intentionally converted to `false` by the local sink; the notice remains the durable output.

A malformed body that explicitly retains `kind: "menu"` is still a positive menu discriminator, so blocking is the safe result. The current Gateway always supplies the spoken line, language, and visible message for that discriminator. Bodies without a valid discriminator are not treated as menus and do not over-correct ordinary sending.

Result: the two failure classes take different paths. Speech failure after a recognized menu never sends. A genuine read failure still sends and clears any stale notice.

## F1 - absence defaults, malformed stored values refuse

The resolver and real store were attacked with this matrix:

| Stored case | Result |
| --- | --- |
| No row | English default |
| Null value | Not persistable: `TenantSettingsStore.Set` rejects null and the database column is required; null from `Get` therefore means no row |
| Empty string | Refused |
| Spaces | Refused |
| Tab, newline, or mixed whitespace | Refused |
| ` fr ` | Canonicalized to `fr` |
| `FR` | Canonicalized to `fr` |
| `Fr` | Canonicalized to `fr` |
| Unknown nonblank code such as `zz` | Refused |
| Region form `fr-FR` | Refused, because it is not one of the product's language codes |

`SpokenLanguage` defaults only when `_store.Get` returns null. Every present string goes through `SpokenLanguages.Require`, which trims and resolves only the known `en`, `fr`, and `es` codes.

I searched every production read and write of `TenantSettingKeys.SpokenLanguage`. The only product writer is `TenantSettingsResolver.SetSpokenLanguage`, reached by the settings endpoint. It checks support and stores `Require(code).Code`, so accepted casing and surrounding whitespace are persisted as the canonical lower-case code. No other production call site writes that key. The lower-level `TenantSettingsStore` is intentionally an opaque string store and is used directly by tests to seed corrupt rows; it is not another product setting write path.

Result: no row remains the legitimate English default, while every persistable present malformed row fails loudly. The normal writer cannot create such a row.

## F3 - stale red test now asserts the real contract

The changed test was not weakened or deleted. It now proves that an unknown code cannot construct a `SpokenLanguage`, asserts that the exception names the safe code and not phrase text, and verifies `CarModeGiveUp` is nonblank for every constructible language.

That test is paired with the stronger completeness checks in the same file: every declared phrase is present in `SpokenPhrases.All`, and every phrase has a translation for every language in `SpokenLanguages.All`. The former test's missing-translation call is now unreachable because unknown languages cannot exist and all known languages are covered.

Result: the new assertion matches a stronger production invariant rather than changing a test to excuse missing behavior.

## Required nonregressions

- Custom self-hosted voice: a temporary probe sent a `SpokenUtterance` with an operator-defined voice through the real `TtsService` and captured the provider request. The custom voice string, input text, and named model were posted unchanged. The focused Core test passed 1 of 1.
- Casing and refusal: `FR`, ` fr `, and `Fr` resolve and store as `fr`; `fr-FR` is refused.
- Model framing: the Car Mode system prompt still identifies the DevThrottle Assistant and contains no Car Mode, driving, or hands-free framing. The existing prompt test pins this.
- Desktop refusal surfaces: `DesktopTtsPlayer` returns false when attached-account voice lookup refuses. Both `FifoWindow` playback paths turn false into visible status text, and `SpeakPlaybackDialog` renders its refusal error.

## Test evidence

- `npm test --workspace @devthrottle/client-core`: 77 files passed, 811 tests passed, 0 failed.
- Temporary F2 hook matrix: 10 of 10 passed, followed by 3 of 3 supplemental state-seam probes.
- Focused custom-voice Core probe: 1 passed, 0 failed.
- `dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj -c Release --no-build --nologo`: 4,510 passed, 0 failed, 17 environment-gated skips, total 4,527, duration 38 minutes 6 seconds.

The Gateway result came from this exact clean audit worktree after deliberately waiting for and acquiring the per-user suite lock. An older attempt that exhausted the lock's 45-minute wait was treated only as infrastructure refusal, never as a test result.

## Cleanup

Every temporary probe was removed. The final worktree status is clean apart from this uncommitted report.
